#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [switch]$AcknowledgeDisposableVm,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$SnapshotName,

    [Parameter(Mandatory)]
    [ValidatePattern("^comote-phase1-pre-verifier-19045\.[0-9]+$")]
    [string]$VerifierSnapshotName,

    [ValidatePattern("^19045$")]
    [string]$RequiredBuildNumber = "19045"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot "Phase1Runtime.Common.ps1")

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
    throw "TESTSIGNING is not configured during Verifier testing."
}
$codeIntegrityState = Get-ComotePhase1ActiveCodeIntegrityState
if (-not $codeIntegrityState.TestSigningActive) {
    throw "The active kernel does not allow test-signed drivers."
}
foreach ($commandName in @(
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
$activationReceiptPath = Join-Path `
    $stateDirectory `
    "verifier-activation.json"
foreach ($receiptPath in @(
    $installReceiptPath,
    $activationReceiptPath
)) {
    if (-not (Test-Path -LiteralPath $receiptPath -PathType Leaf)) {
        throw "A required Phase 1 receipt is missing: $receiptPath"
    }
}
$installReceipt = Get-Content `
    -LiteralPath $installReceiptPath `
    -Raw |
    ConvertFrom-Json
$activationReceipt = Get-Content `
    -LiteralPath $activationReceiptPath `
    -Raw |
    ConvertFrom-Json
if ([string]$installReceipt.status -ne "installed-enumerated" -or
    [string]$installReceipt.snapshotName -ne $SnapshotName) {
    throw "The installed Phase 1 state is not eligible."
}
if ([string]$activationReceipt.status -ne "configured-oneboot" -or
    [string]$activationReceipt.snapshotName -ne $SnapshotName -or
    [string]$activationReceipt.verifierSnapshotName -ne
        $VerifierSnapshotName -or
    [string]$activationReceipt.targetDriver -ne
        "ComoteVirtualHid.sys" -or
    [string]$activationReceipt.bootMode -ne "oneboot") {
    throw "The Phase 1 Verifier activation receipt is not eligible."
}

$configuredBootUtc = [DateTime]::Parse(
    [string]$activationReceipt.configuredBootUtc,
    [Globalization.CultureInfo]::InvariantCulture,
    [Globalization.DateTimeStyles]::RoundtripKind
).ToUniversalTime()
$currentBootUtc = (
    [DateTime]$environment.OperatingSystem.LastBootUpTime
).ToUniversalTime()
if ($currentBootUtc -le $configuredBootUtc) {
    throw "The VM has not completed the oneboot Verifier restart."
}

$queryOutput = (& verifier.exe /query 2>&1 | Out-String)
if ($LASTEXITCODE -ne 0 -or
    $queryOutput.IndexOf(
        "ComoteVirtualHid.sys",
        [StringComparison]::OrdinalIgnoreCase) -lt 0) {
    throw "Active Driver Verifier did not report ComoteVirtualHid.sys."
}
$querySettingsOutput =
    (& verifier.exe /querysettings 2>&1 | Out-String)
if ($LASTEXITCODE -ne 0) {
    throw "Unable to query next-boot Driver Verifier settings."
}
if ($querySettingsOutput.IndexOf(
        "ComoteVirtualHid.sys",
        [StringComparison]::OrdinalIgnoreCase) -ge 0) {
    throw "Oneboot did not clear the Comote target for the next boot."
}

$memoryManagementPath =
    "HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management"
$memoryManagement = Get-ItemProperty `
    -LiteralPath $memoryManagementPath
$driversProperty =
    $memoryManagement.PSObject.Properties["VerifyDrivers"]
$levelProperty =
    $memoryManagement.PSObject.Properties["VerifyDriverLevel"]
$nextBootDrivers = if ($null -eq $driversProperty) {
    ""
}
else {
    [string]$driversProperty.Value
}
$nextBootLevel = if ($null -eq $levelProperty) {
    [uint32]0
}
else {
    [uint32]$levelProperty.Value
}
if (-not [string]::IsNullOrWhiteSpace($nextBootDrivers) -or
    $nextBootLevel -ne 0) {
    throw "Oneboot next-boot Verifier settings were not cleared."
}

$rootDevices = @(Get-ComotePhase1RootDevices)
if ($rootDevices.Count -ne 1 -or
    -not ([string]$rootDevices[0].PNPDeviceID).Equals(
        [string]$installReceipt.rootDeviceInstanceId,
        [StringComparison]::OrdinalIgnoreCase) -or
    [int]$rootDevices[0].ConfigManagerErrorCode -ne 0) {
    throw "The Verifier-boot Comote root device is not healthy."
}
$driverService = Get-CimInstance `
    -ClassName Win32_SystemDriver `
    -Filter "Name='ComoteVirtualHid'" `
    -ErrorAction SilentlyContinue
if ($null -eq $driverService -or
    [string]$driverService.State -ne "Running") {
    throw "The Verifier-boot Comote kernel driver is not running."
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
    throw "The VHF children did not enumerate under Driver Verifier."
}
foreach ($device in @($linkedKeyboards + $linkedMice)) {
    if ([string]$device.Status -ne "OK") {
        throw "A VHF child is not healthy under Driver Verifier."
    }
}

$completedUtc = [DateTime]::UtcNow.ToString("o")
$reportDirectory = Join-Path `
    $projectRoot `
    "artifacts\phase1-verifier"
New-Item -ItemType Directory -Path $reportDirectory -Force |
    Out-Null
$reportPath = Join-Path `
    $reportDirectory `
    ("verifier-active-{0}.json" -f
        (Get-Date -Format "yyyyMMdd-HHmmss"))
$report = [ordered]@{
    completedUtc = $completedUtc
    status = "active-verified"
    snapshotName = $SnapshotName
    verifierSnapshotName = $VerifierSnapshotName
    cycleId = [string]$installReceipt.cycleId
    configuredBootUtc = $configuredBootUtc.ToString("o")
    currentBootUtc = $currentBootUtc.ToString("o")
    targetDriver = "ComoteVirtualHid.sys"
    bootMode = "oneboot"
    activeVerifyDriverLevel =
        [uint32]$activationReceipt.verifyDriverLevel
    nextBootVerifyDrivers = $nextBootDrivers
    nextBootVerifyDriverLevel = $nextBootLevel
    verifierQuery = $queryOutput.Trim()
    verifierQuerySettings = $querySettingsOutput.Trim()
    serviceState = [string]$driverService.State
    keyboardInstanceIds = @(
        $linkedKeyboards |
            ForEach-Object { [string]$_.InstanceId }
    )
    mouseInstanceIds = @(
        $linkedMice |
            ForEach-Object { [string]$_.InstanceId }
    )
}
$report | ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath $reportPath -Encoding UTF8
Set-ComotePhase1NoteProperty `
    -InputObject $activationReceipt `
    -Name "status" `
    -Value "active-verified"
Set-ComotePhase1NoteProperty `
    -InputObject $activationReceipt `
    -Name "activeVerifiedUtc" `
    -Value $completedUtc
Set-ComotePhase1NoteProperty `
    -InputObject $activationReceipt `
    -Name "activeReportPath" `
    -Value $reportPath
$activationReceipt | ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath $activationReceiptPath -Encoding UTF8

Write-Host ""
Write-Host "Phase 1 Driver Verifier oneboot session is active." `
    -ForegroundColor Green
Write-Host "Verified driver: ComoteVirtualHid.sys only"
Write-Host "Next-boot Verifier settings: empty"
Write-Host "Kernel driver service: Running"
Write-Host "VHF keyboard and mouse children: OK"
Write-Host "No input reports were submitted."
Write-Host "Do not restart or reset Verifier until the unload gate is prepared."
Write-Host "Report: $reportPath"
