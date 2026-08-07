#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [switch]$AcknowledgeDisposableVm,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$SnapshotName,

    [ValidatePattern("^19045$")]
    [string]$RequiredBuildNumber = "19045"
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

$environment = Assert-ComotePhase1RuntimeEnvironment `
    -AcknowledgeDisposableVm:$AcknowledgeDisposableVm `
    -RequiredBuildNumber $RequiredBuildNumber
[void](Assert-ComotePhase1SigningPrerequisites)
if (-not (Get-ComotePhase1TestSigningState)) {
    throw "TESTSIGNING is not configured."
}
$codeIntegrityState = Get-ComotePhase1ActiveCodeIntegrityState
if (-not $codeIntegrityState.TestSigningActive) {
    throw "The active kernel does not allow test-signed drivers."
}

foreach ($commandName in @(
    "Get-WindowsDriver",
    "Get-PnpDevice",
    "Get-PnpDeviceProperty",
    "verifier.exe"
)) {
    if (-not (Get-Command $commandName -ErrorAction SilentlyContinue)) {
        throw "Required Windows command was not found: $commandName"
    }
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$stateDirectory = Join-Path `
    $projectRoot `
    "artifacts\phase1-runtime-state"
$installReceiptPath = Join-Path `
    $stateDirectory `
    "enumeration-installation.json"
$coldBootReceiptPath = Join-Path `
    $stateDirectory `
    "cold-boot-preparation.json"
$verifierReceiptPath = Join-Path `
    $stateDirectory `
    "verifier-preparation.json"
foreach ($requiredReceiptPath in @(
    $installReceiptPath,
    $coldBootReceiptPath
)) {
    if (-not (Test-Path `
            -LiteralPath $requiredReceiptPath `
            -PathType Leaf)) {
        throw "A required Phase 1 receipt is missing: $requiredReceiptPath"
    }
}
if (Test-Path -LiteralPath $verifierReceiptPath) {
    throw "A Phase 1 Verifier preparation receipt already exists."
}

$installReceipt = Get-Content `
    -LiteralPath $installReceiptPath `
    -Raw |
    ConvertFrom-Json
$coldBootReceipt = Get-Content `
    -LiteralPath $coldBootReceiptPath `
    -Raw |
    ConvertFrom-Json
if ([string]$installReceipt.status -ne "installed-enumerated" -or
    [string]$installReceipt.snapshotName -ne $SnapshotName -or
    [string]$installReceipt.serviceName -ne "ComoteVirtualHid") {
    throw "The installed Phase 1 state is not eligible for Verifier."
}
if ([string]$coldBootReceipt.status -ne "passed" -or
    [string]$coldBootReceipt.snapshotName -ne $SnapshotName -or
    [string]$coldBootReceipt.cycleId -ne
        [string]$installReceipt.cycleId) {
    throw "A matching passed cold-boot receipt was not found."
}
if ($null -eq $coldBootReceipt.PSObject.Properties["reportPath"] -or
    -not (Test-Path `
        -LiteralPath ([string]$coldBootReceipt.reportPath) `
        -PathType Leaf)) {
    throw "The passed cold-boot report is missing."
}
$coldBootReport = Get-Content `
    -LiteralPath ([string]$coldBootReceipt.reportPath) `
    -Raw |
    ConvertFrom-Json
if ([string]$coldBootReport.status -ne "passed" -or
    [string]$coldBootReport.cycleId -ne [string]$installReceipt.cycleId) {
    throw "The cold-boot report does not match this installation."
}

$driverPackages = @(Get-ComotePhase1DriverPackages)
if ($driverPackages.Count -ne 1 -or
    ([string]$driverPackages[0].Driver).ToLowerInvariant() -ne
        ([string]$installReceipt.publishedInfName).ToLowerInvariant()) {
    throw "The installed Driver Store package does not match its receipt."
}
$rootDevices = @(Get-ComotePhase1RootDevices)
if ($rootDevices.Count -ne 1 -or
    -not ([string]$rootDevices[0].PNPDeviceID).Equals(
        [string]$installReceipt.rootDeviceInstanceId,
        [StringComparison]::OrdinalIgnoreCase) -or
    [int]$rootDevices[0].ConfigManagerErrorCode -ne 0) {
    throw "The installed Comote root device is not healthy."
}
$driverService = Get-CimInstance `
    -ClassName Win32_SystemDriver `
    -Filter "Name='ComoteVirtualHid'" `
    -ErrorAction SilentlyContinue
if ($null -eq $driverService -or
    [string]$driverService.State -ne "Running" -or
    [IO.Path]::GetFileName([string]$driverService.PathName) -ne
        "ComoteVirtualHid.sys") {
    throw "The ComoteVirtualHid kernel driver is not running."
}

$rootInstanceId = [string]$rootDevices[0].PNPDeviceID
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
    throw "The VHF children are not present before Verifier."
}
foreach ($device in @($linkedKeyboards + $linkedMice)) {
    if ([string]$device.Status -ne "OK") {
        throw "A VHF child is not healthy before Verifier."
    }
}

$memoryManagementPath =
    "HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management"
$memoryManagement = Get-ItemProperty `
    -LiteralPath $memoryManagementPath
$verifyDriversProperty =
    $memoryManagement.PSObject.Properties["VerifyDrivers"]
$verifyLevelProperty =
    $memoryManagement.PSObject.Properties["VerifyDriverLevel"]
$verifyDrivers = if ($null -eq $verifyDriversProperty) {
    ""
}
else {
    [string]$verifyDriversProperty.Value
}
$verifyDriverLevel = if ($null -eq $verifyLevelProperty) {
    [uint32]0
}
else {
    [uint32]$verifyLevelProperty.Value
}
if (-not [string]::IsNullOrWhiteSpace($verifyDrivers) -or
    $verifyDriverLevel -ne 0) {
    throw "Driver Verifier already has persistent settings."
}

$querySettingsOutput = (& verifier.exe /querysettings 2>&1 | Out-String)
if ($LASTEXITCODE -ne 0) {
    throw "Unable to query Driver Verifier settings."
}
if ($querySettingsOutput.IndexOf(
        "ComoteVirtualHid.sys",
        [StringComparison]::OrdinalIgnoreCase) -ge 0) {
    throw "ComoteVirtualHid.sys is already selected by Driver Verifier."
}

$crashControlPath =
    "HKLM:\SYSTEM\CurrentControlSet\Control\CrashControl"
$crashControl = Get-ItemProperty -LiteralPath $crashControlPath
$crashDumpProperty =
    $crashControl.PSObject.Properties["CrashDumpEnabled"]
$autoRebootProperty =
    $crashControl.PSObject.Properties["AutoReboot"]
if ($null -eq $crashDumpProperty -or
    [int]$crashDumpProperty.Value -notin @(1, 2, 3, 7)) {
    throw "Windows crash-dump collection is not enabled."
}
$pageFiles = @(
    Get-CimInstance `
        -ClassName Win32_PageFileUsage `
        -ErrorAction SilentlyContinue
)
if ($pageFiles.Count -lt 1) {
    throw "No active page file was found for crash-dump support."
}
$autoReboot = if ($null -eq $autoRebootProperty) {
    $null
}
else {
    [int]$autoRebootProperty.Value
}

New-Item -ItemType Directory -Path $stateDirectory -Force |
    Out-Null
$receipt = [ordered]@{
    schemaVersion = 1
    status = "prepared"
    preparedUtc = [DateTime]::UtcNow.ToString("o")
    preparedBootUtc = (
        [DateTime]$environment.OperatingSystem.LastBootUpTime
    ).ToUniversalTime().ToString("o")
    snapshotName = $SnapshotName
    cycleId = [string]$installReceipt.cycleId
    targetDriver = "ComoteVirtualHid.sys"
    publishedInfName = [string]$installReceipt.publishedInfName
    rootDeviceInstanceId = [string]$installReceipt.rootDeviceInstanceId
    coldBootReport = [string]$coldBootReceipt.reportPath
    initialVerifyDrivers = $verifyDrivers
    initialVerifyDriverLevel = $verifyDriverLevel
    verifierQuerySettings = $querySettingsOutput.Trim()
    crashDumpEnabled = [int]$crashDumpProperty.Value
    autoReboot = $autoReboot
    pageFiles = @(
        $pageFiles |
            ForEach-Object {
                [ordered]@{
                    name = [string]$_.Name
                    allocatedBaseSize = [uint32]$_.AllocatedBaseSize
                }
            }
    )
    activeCodeIntegrityOptions = [uint32]$codeIntegrityState.Options
}
$receipt | ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath $verifierReceiptPath -Encoding UTF8

Write-Host ""
Write-Host "Phase 1 Driver Verifier preflight passed." `
    -ForegroundColor Green
Write-Host "Current persistent Verifier settings: empty"
Write-Host "Crash dump setting: $($receipt.crashDumpEnabled)"
Write-Host "Active page files: $($pageFiles.Count)"
Write-Host "No Driver Verifier setting was changed."
Write-Host "No device state was changed."
Write-Host "Receipt: $verifierReceiptPath"
