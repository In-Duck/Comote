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
    [switch]$AcknowledgeVerifierReset,

    [ValidatePattern("^19045$")]
    [string]$RequiredBuildNumber = "19045",

    [switch]$ValidateOnly
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot "Phase1Runtime.Common.ps1")

if (-not $AcknowledgeVerifierReset.IsPresent) {
    throw "Explicit Driver Verifier reset acknowledgement is required."
}
[void](Assert-ComotePhase1RuntimeEnvironment `
    -AcknowledgeDisposableVm:$AcknowledgeDisposableVm `
    -RequiredBuildNumber $RequiredBuildNumber)
if (-not (Get-Command verifier.exe -ErrorAction SilentlyContinue)) {
    throw "verifier.exe was not found."
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$activationReceiptPath = Join-Path `
    $projectRoot `
    "artifacts\phase1-runtime-state\verifier-activation.json"
if (-not (Test-Path `
        -LiteralPath $activationReceiptPath `
        -PathType Leaf)) {
    throw "The Phase 1 Verifier activation receipt was not found."
}
$activationReceipt = Get-Content `
    -LiteralPath $activationReceiptPath `
    -Raw |
    ConvertFrom-Json
if ([string]$activationReceipt.status -notin @(
        "configured-oneboot",
        "active-verified",
        "unload-in-progress",
        "unload-passed"
    ) -or
    [string]$activationReceipt.snapshotName -ne $SnapshotName -or
    [string]$activationReceipt.verifierSnapshotName -ne
        $VerifierSnapshotName -or
    [string]$activationReceipt.targetDriver -ne
        "ComoteVirtualHid.sys") {
    throw "The Phase 1 Verifier activation receipt is not reset-eligible."
}

$memoryManagementPath =
    "HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management"
$memoryManagement = Get-ItemProperty `
    -LiteralPath $memoryManagementPath
$driversProperty =
    $memoryManagement.PSObject.Properties["VerifyDrivers"]
$verifyDrivers = if ($null -eq $driversProperty) {
    ""
}
else {
    [string]$driversProperty.Value
}
$targets = @(
    $verifyDrivers -split '[,\s]+' |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
)
$pendingTargetIsComote = [bool](
    $targets.Count -eq 1 -and
    $targets[0].Equals(
        "ComoteVirtualHid.sys",
        [StringComparison]::OrdinalIgnoreCase)
)
$activeQuery = (& verifier.exe /query 2>&1 | Out-String)
$activeQueryExitCode = $LASTEXITCODE
$activeDriverNames = @(
    [Regex]::Matches(
        $activeQuery,
        '(?i)\b[A-Za-z0-9_.-]+\.sys\b') |
        ForEach-Object { $_.Value } |
        Sort-Object -Unique
)
$otherActiveTargets = @(
    $activeDriverNames |
        Where-Object {
            -not $_.Equals(
                "ComoteVirtualHid.sys",
                [StringComparison]::OrdinalIgnoreCase)
        }
)
$activeTargetIsComote = [bool](
    $activeQueryExitCode -eq 0 -and
    $activeQuery.IndexOf(
        "ComoteVirtualHid.sys",
        [StringComparison]::OrdinalIgnoreCase) -ge 0
)
$unloadReceiptAllowsEmptyTargets = [bool](
    [string]$activationReceipt.status -in @(
        "unload-in-progress",
        "unload-passed"
    ) -and
    $targets.Count -eq 0 -and
    $activeQueryExitCode -eq 0 -and
    $otherActiveTargets.Count -eq 0
)
if (-not (
        $pendingTargetIsComote -and
        $otherActiveTargets.Count -eq 0
    ) -and
    -not (
        $targets.Count -eq 0 -and
        $activeTargetIsComote -and
        $otherActiveTargets.Count -eq 0
    ) -and
    -not $unloadReceiptAllowsEmptyTargets) {
    throw "Verifier reset refused because the pending or active target is not exactly ComoteVirtualHid.sys."
}

if ($ValidateOnly.IsPresent) {
    Write-Host ""
    Write-Host "Phase 1 Driver Verifier reset validation passed." `
        -ForegroundColor Green
    Write-Host "Reset target: ComoteVirtualHid.sys only"
    Write-Host "No Driver Verifier setting was changed."
    Write-Host "No restart was requested."
    return
}

$resetOutput = (& verifier.exe /reset 2>&1 | Out-String)
$resetExitCode = $LASTEXITCODE
if ($resetExitCode -notin @(0, 2)) {
    throw ("Driver Verifier reset failed with exit code {0}: {1}" -f
        $resetExitCode,
        $resetOutput)
}
Set-ComotePhase1NoteProperty `
    -InputObject $activationReceipt `
    -Name "status" `
    -Value "reset-requested"
Set-ComotePhase1NoteProperty `
    -InputObject $activationReceipt `
    -Name "resetRequestedUtc" `
    -Value ([DateTime]::UtcNow.ToString("o"))
Set-ComotePhase1NoteProperty `
    -InputObject $activationReceipt `
    -Name "resetExitCode" `
    -Value $resetExitCode
$activationReceipt | ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath $activationReceiptPath -Encoding UTF8

Write-Host ""
Write-Host "Phase 1 Driver Verifier reset was requested." `
    -ForegroundColor Yellow
Write-Host "No restart was requested by this script."
Write-Host "Restart the VM manually to complete the reset."
