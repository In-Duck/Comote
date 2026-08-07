#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [switch]$AcknowledgeDisposableVm,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$SigningSnapshotName,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$RecoverySnapshotName,

    [ValidatePattern("^19045$")]
    [string]$RequiredBuildNumber = "19045"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot "Phase2Runtime.Common.ps1")

$runtimeLock = Enter-ComotePhase2RuntimeLock
try {

if ($SigningSnapshotName -eq $RecoverySnapshotName) {
    throw "The signing and recovery snapshot names must be different."
}

foreach ($boundaryName in @(
    "Test-Phase2TestSigningBoundary.ps1",
    "Test-Phase2EnumerationBoundary.ps1",
    "Test-Phase2RecoveryBoundary.ps1"
)) {
    & (Join-Path $PSScriptRoot $boundaryName)
}

& (Join-Path $PSScriptRoot "Repair-Phase2EnumerationState.ps1") `
    -AcknowledgeDisposableVm:$AcknowledgeDisposableVm `
    -RecoverySnapshotName $RecoverySnapshotName `
    -RequiredBuildNumber $RequiredBuildNumber

$projectRoot = Split-Path -Parent $PSScriptRoot
$stateDirectory = Join-Path $projectRoot "artifacts\phase2-runtime-state"
$receiptPath = Join-Path `
    $stateDirectory `
    "test-signing-preparation.json"
$installReceiptPath = Join-Path `
    $stateDirectory `
    "enumeration-installation.json"

if (Test-Path -LiteralPath $installReceiptPath -PathType Leaf) {
    & (Join-Path $PSScriptRoot "Test-Phase2InstalledState.ps1") `
        -AcknowledgeDisposableVm:$AcknowledgeDisposableVm `
        -RecoverySnapshotName $RecoverySnapshotName `
        -RequiredBuildNumber $RequiredBuildNumber
    Write-Host "The requested Phase 2 installation was already complete."
    return
}

$preflightValidated = $false
if (Test-Path -LiteralPath $receiptPath -PathType Leaf) {
    try {
        & (Join-Path $PSScriptRoot "Test-Phase2EnumerationPreflight.ps1") `
            -AcknowledgeDisposableVm:$AcknowledgeDisposableVm `
            -SnapshotName $SigningSnapshotName `
            -RequiredBuildNumber $RequiredBuildNumber
        $preflightValidated = $true
    }
    catch {
        Write-Verbose (
            "The existing signing receipt failed full preflight: {0}" -f
            $_.Exception.Message
        )
    }
}

if (-not $preflightValidated) {
    $repairArguments = @{
        AcknowledgeDisposableVm = $AcknowledgeDisposableVm
        SigningSnapshotName = $SigningSnapshotName
        RequiredBuildNumber = $RequiredBuildNumber
    }
    if (Test-Path -LiteralPath $receiptPath) {
        $repairArguments.ReplaceInvalidReceipt = $true
    }
    & (Join-Path $PSScriptRoot "Repair-Phase2SigningReceipt.ps1") `
        @repairArguments
    & (Join-Path $PSScriptRoot "Test-Phase2EnumerationPreflight.ps1") `
        -AcknowledgeDisposableVm:$AcknowledgeDisposableVm `
        -SnapshotName $SigningSnapshotName `
        -RequiredBuildNumber $RequiredBuildNumber
}

& (Join-Path $PSScriptRoot "Install-Phase2Enumeration.ps1") `
    -AcknowledgeDisposableVm:$AcknowledgeDisposableVm `
    -SigningSnapshotName $SigningSnapshotName `
    -RecoverySnapshotName $RecoverySnapshotName `
    -RequiredBuildNumber $RequiredBuildNumber

& (Join-Path $PSScriptRoot "Test-Phase2InstalledState.ps1") `
    -AcknowledgeDisposableVm:$AcknowledgeDisposableVm `
    -RecoverySnapshotName $RecoverySnapshotName `
    -RequiredBuildNumber $RequiredBuildNumber
}
finally {
    Exit-ComotePhase2RuntimeLock -Mutex $runtimeLock
}
