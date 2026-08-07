#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [switch]$AcknowledgeDisposableVm,

    [ValidatePattern("^19045$")]
    [string]$RequiredBuildNumber = "19045"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot "Phase1Runtime.Common.ps1")

[void](Assert-ComotePhase1RuntimeEnvironment `
    -AcknowledgeDisposableVm:$AcknowledgeDisposableVm `
    -RequiredBuildNumber $RequiredBuildNumber)
Assert-ComotePhase1NoInstalledDevice

foreach ($commandName in @(
    "Get-WindowsDriver",
    "Get-PnpDevice"
)) {
    if (-not (Get-Command $commandName -ErrorAction SilentlyContinue)) {
        throw "Required Windows command was not found: $commandName"
    }
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$installReceiptPath = Join-Path `
    $projectRoot `
    "artifacts\phase1-runtime-state\enumeration-installation.json"
if (Test-Path -LiteralPath $installReceiptPath) {
    $installReceipt = Get-Content `
        -LiteralPath $installReceiptPath `
        -Raw |
        ConvertFrom-Json
    if ([string]$installReceipt.status -ne "removed") {
        throw "An active Phase 1 enumeration installation receipt still exists."
    }
}

$packages = @(
    Get-WindowsDriver -Online -ErrorAction Stop |
        Where-Object {
            [string]$_.ProviderName -eq "Comote" -and
            [string]$_.ClassName -eq "System" -and
            [IO.Path]::GetFileName([string]$_.OriginalFileName) -eq
                "ComoteVirtualHid.inf"
        }
)
if ($packages.Count -ne 0) {
    throw "A Comote Virtual HID package remains in the Driver Store."
}

$presentDevices = @(
    Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue |
        Where-Object {
            [string]$_.InstanceId -like "ROOT\COMOTEVIRTUALHID*"
        }
)
if ($presentDevices.Count -ne 0) {
    throw "A present Comote Virtual HID PnP device remains."
}

Write-Host ""
Write-Host "Phase 1 enumeration state is clean." -ForegroundColor Green
Write-Host "No device, service, Driver Store package, or active installation receipt was found."
Write-Host "No system state was changed."