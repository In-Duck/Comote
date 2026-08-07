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

    [Parameter(Mandatory)]
    [switch]$AcknowledgeOneBootCrashRisk,

    [ValidatePattern("^19045$")]
    [string]$RequiredBuildNumber = "19045",

    [switch]$ValidateOnly
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot "Phase1Runtime.Common.ps1")

function Get-ComotePhase1VerifierRegistryState {
    $path =
        "HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management"
    $memoryManagement = Get-ItemProperty -LiteralPath $path
    $driversProperty =
        $memoryManagement.PSObject.Properties["VerifyDrivers"]
    $levelProperty =
        $memoryManagement.PSObject.Properties["VerifyDriverLevel"]
    $drivers = if ($null -eq $driversProperty) {
        ""
    }
    else {
        [string]$driversProperty.Value
    }
    $level = if ($null -eq $levelProperty) {
        [uint32]0
    }
    else {
        [uint32]$levelProperty.Value
    }

    return [PSCustomObject]@{
        Drivers = $drivers
        Level = $level
    }
}

function Test-ComotePhase1SingleVerifierTarget {
    param(
        [Parameter(Mandatory)]
        [string]$DriverList
    )

    $drivers = @(
        $DriverList -split '[,\s]+' |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    )
    return [bool](
        $drivers.Count -eq 1 -and
        $drivers[0].Equals(
            "ComoteVirtualHid.sys",
            [StringComparison]::OrdinalIgnoreCase)
    )
}

if (-not $AcknowledgeOneBootCrashRisk.IsPresent) {
    throw "Explicit one-boot Driver Verifier crash-risk acknowledgement is required."
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
if (-not (Get-Command verifier.exe -ErrorAction SilentlyContinue)) {
    throw "verifier.exe was not found."
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$stateDirectory = Join-Path `
    $projectRoot `
    "artifacts\phase1-runtime-state"
$preparationReceiptPath = Join-Path `
    $stateDirectory `
    "verifier-preparation.json"
$activationReceiptPath = Join-Path `
    $stateDirectory `
    "verifier-activation.json"
if (-not (Test-Path `
        -LiteralPath $preparationReceiptPath `
        -PathType Leaf)) {
    throw "The Phase 1 Verifier preparation receipt was not found."
}
if (Test-Path -LiteralPath $activationReceiptPath) {
    throw "A Phase 1 Verifier activation receipt already exists."
}

$preparationReceipt = Get-Content `
    -LiteralPath $preparationReceiptPath `
    -Raw |
    ConvertFrom-Json
if ([string]$preparationReceipt.status -ne "prepared" -or
    [string]$preparationReceipt.snapshotName -ne $SnapshotName -or
    [string]$preparationReceipt.targetDriver -ne
        "ComoteVirtualHid.sys") {
    throw "The Phase 1 Verifier preparation receipt is not eligible."
}
$preparedBootUtc = [DateTime]::Parse(
    [string]$preparationReceipt.preparedBootUtc,
    [Globalization.CultureInfo]::InvariantCulture,
    [Globalization.DateTimeStyles]::RoundtripKind
).ToUniversalTime()
$currentBootUtc = (
    [DateTime]$environment.OperatingSystem.LastBootUpTime
).ToUniversalTime()
if ([Math]::Abs(
        ($currentBootUtc - $preparedBootUtc).TotalSeconds) -gt 2) {
    throw "The VM boot changed after Verifier preflight."
}

$driverService = Get-CimInstance `
    -ClassName Win32_SystemDriver `
    -Filter "Name='ComoteVirtualHid'" `
    -ErrorAction SilentlyContinue
if ($null -eq $driverService -or
    [string]$driverService.State -ne "Running" -or
    [IO.Path]::GetFileName([string]$driverService.PathName) -ne
        "ComoteVirtualHid.sys") {
    throw "The target ComoteVirtualHid driver is not running."
}

$initialRegistryState = Get-ComotePhase1VerifierRegistryState
if (-not [string]::IsNullOrWhiteSpace(
        [string]$initialRegistryState.Drivers) -or
    [uint32]$initialRegistryState.Level -ne 0) {
    throw "Driver Verifier settings changed after preflight."
}
$initialQueryOutput = (& verifier.exe /querysettings 2>&1 | Out-String)
if ($LASTEXITCODE -ne 0) {
    throw "Unable to query Driver Verifier settings."
}
if ($initialQueryOutput.IndexOf(
        "ComoteVirtualHid.sys",
        [StringComparison]::OrdinalIgnoreCase) -ge 0) {
    throw "ComoteVirtualHid.sys is already selected by Driver Verifier."
}

if ($ValidateOnly.IsPresent) {
    Write-Host ""
    Write-Host "Phase 1 Driver Verifier activation validation passed." `
        -ForegroundColor Green
    Write-Host "Target driver: ComoteVirtualHid.sys only"
    Write-Host "Boot mode to be used: oneboot"
    Write-Host "No Driver Verifier setting was changed."
    Write-Host "No restart was requested."
    return
}

$configurationStarted = $false
try {
    $standardOutput = (& verifier.exe `
        /standard `
        /driver `
        ComoteVirtualHid.sys 2>&1 | Out-String)
    $standardExitCode = $LASTEXITCODE
    $configurationStarted = $true
    if ($standardExitCode -notin @(0, 2)) {
        throw ("Driver Verifier rejected the single-driver standard settings " +
            "(exit code {0}): {1}" -f
            $standardExitCode,
            $standardOutput)
    }

    $bootModeOutput = (& verifier.exe `
        /bootmode `
        oneboot 2>&1 | Out-String)
    $bootModeExitCode = $LASTEXITCODE
    if ($bootModeExitCode -notin @(0, 2)) {
        throw ("Driver Verifier rejected oneboot mode " +
            "(exit code {0}): {1}" -f
            $bootModeExitCode,
            $bootModeOutput)
    }

    $configuredRegistryState =
        Get-ComotePhase1VerifierRegistryState
    if (-not (Test-ComotePhase1SingleVerifierTarget `
            -DriverList ([string]$configuredRegistryState.Drivers)) -or
        [uint32]$configuredRegistryState.Level -eq 0) {
        throw "Driver Verifier was not limited to the single Comote target."
    }
    $configuredQueryOutput =
        (& verifier.exe /querysettings 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0 -or
        $configuredQueryOutput.IndexOf(
            "ComoteVirtualHid.sys",
            [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Driver Verifier query did not confirm the Comote target."
    }

    $receipt = [ordered]@{
        schemaVersion = 1
        status = "configured-oneboot"
        configuredUtc = [DateTime]::UtcNow.ToString("o")
        configuredBootUtc = $currentBootUtc.ToString("o")
        snapshotName = $SnapshotName
        verifierSnapshotName = $VerifierSnapshotName
        cycleId = [string]$preparationReceipt.cycleId
        targetDriver = "ComoteVirtualHid.sys"
        bootMode = "oneboot"
        verifyDrivers = [string]$configuredRegistryState.Drivers
        verifyDriverLevel = [uint32]$configuredRegistryState.Level
        standardExitCode = $standardExitCode
        bootModeExitCode = $bootModeExitCode
        querySettings = $configuredQueryOutput.Trim()
        activeCodeIntegrityOptions =
            [uint32]$codeIntegrityState.Options
    }
    $receipt | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath $activationReceiptPath -Encoding UTF8
}
catch {
    $configurationError = $_
    $resetOutput = ""
    $resetExitCode = $null
    if ($configurationStarted) {
        $resetOutput = (& verifier.exe /reset 2>&1 | Out-String)
        $resetExitCode = $LASTEXITCODE
    }
    throw ("Verifier activation failed: {0} Pending settings were reset " +
        "with exit code {1}: {2}" -f
        $configurationError.Exception.Message,
        $resetExitCode,
        $resetOutput)
}

Write-Host ""
Write-Host "Phase 1 Driver Verifier is configured for one boot." `
    -ForegroundColor Yellow
Write-Host "Target driver: ComoteVirtualHid.sys only"
Write-Host "Boot mode: oneboot"
Write-Host "No restart was requested by this script."
Write-Host "Restart the VM manually only after the final query is reviewed."
Write-Host "Receipt: $activationReceiptPath"
