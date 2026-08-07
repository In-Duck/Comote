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

function Test-ComotePhase1DeviceDescendant {
    param(
        [Parameter(Mandatory)]
        [string]$InstanceId,

        [Parameter(Mandatory)]
        [string]$AncestorInstanceId
    )

    $current = $InstanceId
    for ($depth = 0; $depth -lt 10; $depth++) {
        $parentProperties = @(
            Get-PnpDeviceProperty `
                -InstanceId $current `
                -KeyName "DEVPKEY_Device_Parent" `
                -ErrorAction SilentlyContinue
        )
        if ($parentProperties.Count -ne 1) {
            return $false
        }
        $dataProperty =
            $parentProperties[0].PSObject.Properties["Data"]
        if ($null -eq $dataProperty) {
            return $false
        }
        $parent = [string]$dataProperty.Value
        if ([string]::IsNullOrWhiteSpace($parent)) {
            return $false
        }
        if ($parent.Equals(
                $AncestorInstanceId,
                [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
        if ($parent.Equals(
                $current,
                [StringComparison]::OrdinalIgnoreCase)) {
            return $false
        }
        $current = $parent
    }

    return $false
}

[void](Assert-ComotePhase1RuntimeEnvironment `
    -AcknowledgeDisposableVm:$AcknowledgeDisposableVm `
    -RequiredBuildNumber $RequiredBuildNumber)
[void](Assert-ComotePhase1SigningPrerequisites)
if (-not (Get-ComotePhase1TestSigningState)) {
    throw "TESTSIGNING is not active."
}

foreach ($commandName in @(
    "Get-WindowsDriver",
    "Get-PnpDevice",
    "Get-PnpDeviceProperty"
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
    [string]$installReceipt.snapshotName -ne $SnapshotName) {
    throw "The Phase 1 installation receipt is not removable by this gate."
}

$publishedInfName =
    ([string]$installReceipt.publishedInfName).ToLowerInvariant()
$rootInstanceId = [string]$installReceipt.rootDeviceInstanceId
if ($publishedInfName -notmatch "^oem\d+\.inf$" -or
    $rootInstanceId -notlike "ROOT\*") {
    throw "The Phase 1 installation receipt contains an invalid target."
}

$driverPackages = @(Get-ComotePhase1DriverPackages)
if ($driverPackages.Count -ne 1 -or
    ([string]$driverPackages[0].Driver).ToLowerInvariant() -ne
        $publishedInfName) {
    throw "The installed Comote Driver Store package does not match its receipt."
}
$rootDevices = @(Get-ComotePhase1RootDevices)
if ($rootDevices.Count -ne 1 -or
    -not ([string]$rootDevices[0].PNPDeviceID).Equals(
        $rootInstanceId,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "The installed Comote root device does not match its receipt."
}
if ([int]$rootDevices[0].ConfigManagerErrorCode -ne 0) {
    throw "The installed Comote root device is not healthy."
}

$driverService = Get-CimInstance `
    -ClassName Win32_SystemDriver `
    -Filter "Name='ComoteVirtualHid'" `
    -ErrorAction SilentlyContinue
if ($null -eq $driverService -or
    [string]$driverService.State -ne "Running") {
    throw "The ComoteVirtualHid kernel driver service is not running."
}

$linkedKeyboards = @(
    Get-PnpDevice `
        -PresentOnly `
        -Class Keyboard `
        -ErrorAction SilentlyContinue |
        Where-Object {
            Test-ComotePhase1DeviceDescendant `
                -InstanceId ([string]$_.InstanceId) `
                -AncestorInstanceId $rootInstanceId
        }
)
$linkedMice = @(
    Get-PnpDevice `
        -PresentOnly `
        -Class Mouse `
        -ErrorAction SilentlyContinue |
        Where-Object {
            Test-ComotePhase1DeviceDescendant `
                -InstanceId ([string]$_.InstanceId) `
                -AncestorInstanceId $rootInstanceId
        }
)
if ($linkedKeyboards.Count -lt 1 -or
    $linkedMice.Count -lt 1) {
    throw "The current VHF keyboard and mouse children were not found."
}
foreach ($device in @($linkedKeyboards + $linkedMice)) {
    if ([string]$device.Status -ne "OK") {
        throw "A current VHF input child is not healthy."
    }
}
$expectedInputIds = @(
    @($linkedKeyboards + $linkedMice) |
        ForEach-Object { [string]$_.InstanceId }
)
$programFilesX86 = [Environment]::GetFolderPath(
    [Environment+SpecialFolder]::ProgramFilesX86
)
$devGen = Find-ComotePhase1Tool `
    -Root (Join-Path $programFilesX86 "Windows Kits\10\Tools") `
    -Name "devgen.exe"
if (-not $devGen) {
    throw "DevGen.exe was not found in the installed WDK."
}

if ($ValidateOnly.IsPresent) {
    Write-Host ""
    Write-Host "Phase 1 installed enumeration state is healthy." -ForegroundColor Green
    Write-Host "No device or driver package was removed."
    return
}

$removeOutput = (& $devGen `
    /remove `
    $rootInstanceId `
    /subtree 2>&1 | Out-String)
if ($LASTEXITCODE -ne 0) {
    throw "DevGen failed to remove the root device: $removeOutput"
}
for ($attempt = 0; $attempt -lt 20; $attempt++) {
    if (@(Get-ComotePhase1RootDevices).Count -eq 0) {
        break
    }
    Start-Sleep -Seconds 1
}
if (@(Get-ComotePhase1RootDevices).Count -ne 0) {
    throw "The Comote root device remained after DevGen removal."
}

$deleteOutput = (& pnputil.exe `
    /delete-driver `
    $publishedInfName `
    /uninstall 2>&1 | Out-String)
if ($LASTEXITCODE -ne 0) {
    throw "PnPUtil failed to remove the driver package: $deleteOutput"
}
for ($attempt = 0; $attempt -lt 20; $attempt++) {
    if (@(Get-ComotePhase1DriverPackages).Count -eq 0) {
        break
    }
    Start-Sleep -Seconds 1
}
if (@(Get-ComotePhase1DriverPackages).Count -ne 0) {
    throw "The Comote package remained in the Driver Store."
}

$orphanServiceOutput = (& sc.exe query ComoteVirtualHid 2>&1 |
    Out-String)
$orphanServiceExitCode = $LASTEXITCODE
if ($orphanServiceExitCode -eq 0) {
    & (Join-Path `
        $PSScriptRoot `
        "Repair-Phase1RemovedEnumerationState.ps1") `
        -AcknowledgeDisposableVm:$AcknowledgeDisposableVm `
        -SnapshotName $SnapshotName `
        -RequiredBuildNumber $RequiredBuildNumber
}
elseif ($orphanServiceExitCode -ne 1060) {
    throw ("Unable to verify the Comote service removal " +
        "(sc.exe exit code {0}): {1}" -f
        $orphanServiceExitCode,
        $orphanServiceOutput)
}

Assert-ComotePhase1NoInstalledDevice
$remainingPresentInputIds = @(
    Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue |
        ForEach-Object { [string]$_.InstanceId }
)
foreach ($expectedInputId in $expectedInputIds) {
    if ($remainingPresentInputIds -contains $expectedInputId) {
        throw "A VHF input child remained present after removal."
    }
}

Set-ComotePhase1NoteProperty `
    -InputObject $installReceipt `
    -Name "status" `
    -Value "removed"
Set-ComotePhase1NoteProperty `
    -InputObject $installReceipt `
    -Name "removedUtc" `
    -Value ([DateTime]::UtcNow.ToString("o"))
$installReceipt | ConvertTo-Json -Depth 6 |
    Set-Content -LiteralPath $installReceiptPath -Encoding UTF8

Write-Host ""
Write-Host "Phase 1 root device and Driver Store package were removed." -ForegroundColor Green
Write-Host "The test certificate and TESTSIGNING state were not changed."
