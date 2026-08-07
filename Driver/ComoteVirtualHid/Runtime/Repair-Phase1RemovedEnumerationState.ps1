#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [switch]$AcknowledgeDisposableVm,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$SnapshotName,

    [ValidatePattern("^19045$")]
    [string]$RequiredBuildNumber = "19045",

    [switch]$ValidateOnly
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot "Phase1Runtime.Common.ps1")

function Get-ComotePhase1DriverPackages {
    return @(
        Get-WindowsDriver -Online -ErrorAction Stop |
            Where-Object {
                [string]$_.ProviderName -eq "Comote" -and
                [string]$_.ClassName -eq "System" -and
                [IO.Path]::GetFileName([string]$_.OriginalFileName) -eq
                    "ComoteVirtualHid.inf"
            }
    )
}

function Get-ComotePhase1RootDevices {
    return @(
        Get-CimInstance `
            -ClassName Win32_PnPEntity `
            -ErrorAction SilentlyContinue |
            Where-Object {
                @($_.HardwareID) -contains "ROOT\COMOTEVIRTUALHID"
            }
    )
}

[void](Assert-ComotePhase1RuntimeEnvironment `
    -AcknowledgeDisposableVm:$AcknowledgeDisposableVm `
    -RequiredBuildNumber $RequiredBuildNumber)

foreach ($commandName in @(
    "Get-WindowsDriver",
    "Get-PnpDevice",
    "Get-Service"
)) {
    if (-not (Get-Command $commandName -ErrorAction SilentlyContinue)) {
        throw "Required Windows command was not found: $commandName"
    }
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$installReceiptPath = Join-Path `
    $projectRoot `
    "artifacts\phase1-runtime-state\enumeration-installation.json"
if (-not (Test-Path -LiteralPath $installReceiptPath -PathType Leaf)) {
    throw "The Phase 1 installation receipt was not found."
}
$installReceipt = Get-Content `
    -LiteralPath $installReceiptPath `
    -Raw |
    ConvertFrom-Json
if ([string]$installReceipt.status -ne "installed-enumerated" -or
    [string]$installReceipt.snapshotName -ne $SnapshotName -or
    [string]$installReceipt.serviceName -ne "ComoteVirtualHid") {
    throw "The Phase 1 receipt is not eligible for removal recovery."
}

if (@(Get-ComotePhase1RootDevices).Count -ne 0) {
    throw "A Comote root device still exists; removal recovery is not safe."
}
if (@(Get-ComotePhase1DriverPackages).Count -ne 0) {
    throw "A Comote Driver Store package still exists; removal recovery is not safe."
}

$presentInputIds = @(
    Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue |
        ForEach-Object { [string]$_.InstanceId }
)
foreach ($expectedInputId in @(
    @($installReceipt.keyboardInstanceIds) +
    @($installReceipt.mouseInstanceIds)
)) {
    if ($presentInputIds -contains [string]$expectedInputId) {
        throw "A VHF input child remains present; removal recovery is not safe."
    }
}

$serviceRegistryPath =
    "HKLM:\SYSTEM\CurrentControlSet\Services\ComoteVirtualHid"
if (-not (Test-Path -LiteralPath $serviceRegistryPath)) {
    throw "The orphaned Comote service registry key was not found."
}
$serviceRegistry = Get-ItemProperty `
    -LiteralPath $serviceRegistryPath
$serviceImagePath = [string]$serviceRegistry.ImagePath
if ([int]$serviceRegistry.Type -ne 1 -or
    [int]$serviceRegistry.Start -ne 4 -or
    $serviceImagePath -notmatch
        '(?i)^\\SystemRoot\\System32\\DriverStore\\FileRepository\\comotevirtualhid\.inf_amd64_[0-9a-f]+\\ComoteVirtualHid\.sys$') {
    throw "The orphaned service does not match the expected stopped Comote driver."
}

$service = Get-Service `
    -Name "ComoteVirtualHid" `
    -ErrorAction Stop
if ($service.Status -ne
    [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
    throw "The orphaned Comote driver service is not stopped."
}

if ($ValidateOnly.IsPresent) {
    Write-Host ""
    Write-Host "Phase 1 partial-removal recovery preflight passed." `
        -ForegroundColor Green
    Write-Host "Only the stopped, disabled Comote service is eligible for deletion."
    Write-Host "No system state was changed."
    return
}

$deleteOutput = (& sc.exe delete ComoteVirtualHid 2>&1 |
    Out-String)
if ($LASTEXITCODE -ne 0) {
    throw "SC failed to delete the orphaned Comote service: $deleteOutput"
}

$serviceGone = $false
for ($attempt = 0; $attempt -lt 30; $attempt++) {
    $queryOutput = (& sc.exe query ComoteVirtualHid 2>&1 |
        Out-String)
    if ($LASTEXITCODE -eq 1060) {
        $serviceGone = $true
        break
    }
    Start-Sleep -Seconds 1
}
if (-not $serviceGone) {
    throw "The orphaned Comote service remained after deletion."
}

Set-ComotePhase1NoteProperty `
    -InputObject $installReceipt `
    -Name "status" `
    -Value "removed"
Set-ComotePhase1NoteProperty `
    -InputObject $installReceipt `
    -Name "removedUtc" `
    -Value ([DateTime]::UtcNow.ToString("o"))
Set-ComotePhase1NoteProperty `
    -InputObject $installReceipt `
    -Name "removalRecovery" `
    -Value "orphaned-service-deleted"
$installReceipt | ConvertTo-Json -Depth 6 |
    Set-Content -LiteralPath $installReceiptPath -Encoding UTF8

Assert-ComotePhase1NoInstalledDevice

Write-Host ""
Write-Host "Phase 1 partial removal was finalized safely." `
    -ForegroundColor Green
Write-Host "The stopped orphaned service was deleted."
Write-Host "The removal receipt was finalized."
