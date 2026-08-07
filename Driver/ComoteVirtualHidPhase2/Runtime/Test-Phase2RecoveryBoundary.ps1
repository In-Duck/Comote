#Requires -Version 5.1
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$paths = [ordered]@{
    SigningRepair = Join-Path $PSScriptRoot "Repair-Phase2SigningReceipt.ps1"
    EnumerationRepair = Join-Path $PSScriptRoot "Repair-Phase2EnumerationState.ps1"
    Wrapper = Join-Path $PSScriptRoot "Invoke-Phase2RepairAndInstall.ps1"
}
$sources = @{}
foreach ($entry in $paths.GetEnumerator()) {
    if (-not (Test-Path -LiteralPath $entry.Value -PathType Leaf)) {
        throw "Required Phase 2 recovery source is missing: $($entry.Value)"
    }
    $source = Get-Content -LiteralPath $entry.Value -Raw
    if ($source -match '[^\x00-\x7F]') {
        throw "Phase 2 recovery PowerShell must remain ASCII-compatible."
    }
    $sources[$entry.Key] = $source
}

foreach ($requiredText in @(
    "Assert-ComotePhase2RuntimeEnvironment",
    "Assert-ComotePhase2SigningPrerequisites",
    "Assert-ComotePhase2NoInstalledDevice",
    "Get-ComotePhase2DriverPackages",
    "ReplaceInvalidReceipt",
    "invalid-signing-receipt-",
    "phase2-test-signed",
    "ComotePhase2Test.cer",
    "Get-ComotePhase2NamedCertificates",
    "Get-ComotePhase2CertificateCopies",
    "Test-ComotePhase2CodeSigningEku",
    "Get-AuthenticodeSignature",
    "SHA256.json",
    '"verify", "/pa", "/c"',
    "Write-ComotePhase2JsonAtomically",
    "Enter-ComotePhase2RuntimeLock",
    "Exit-ComotePhase2RuntimeLock",
    "reconstructedFromValidatedPartialState",
    "No certificate was created and no driver or device was installed"
)) {
    if ($sources.SigningRepair.IndexOf(
            $requiredText,
            [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Required signing-receipt recovery guard is missing: $requiredText"
    }
}
$signingVerifyIndex = $sources.SigningRepair.IndexOf(
    '"verify", "/pa", "/c"',
    [StringComparison]::OrdinalIgnoreCase
)
$signingWriteIndex = $sources.SigningRepair.IndexOf(
    "Write-ComotePhase2JsonAtomically",
    $signingVerifyIndex,
    [StringComparison]::OrdinalIgnoreCase
)
if ($signingVerifyIndex -lt 0 -or
    $signingWriteIndex -lt $signingVerifyIndex) {
    throw "Signing receipt validation must precede atomic reconstruction."
}
foreach ($forbiddenText in @(
    "New-SelfSignedCertificate",
    "Import-Certificate",
    "Remove-ComotePhase2TestCertificate",
    "pnputil.exe",
    "devgen.exe",
    "DeviceIoControl",
    "Restart-Computer",
    "shutdown.exe",
    "Set-Content"
)) {
    if ($sources.SigningRepair.IndexOf(
            $forbiddenText,
            [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Signing receipt recovery crossed its boundary: $forbiddenText"
    }
}

foreach ($requiredText in @(
    "Assert-ComotePhase2EnumerationTransaction",
    "Invoke-ComotePhase2ExactEnumerationCleanup",
    "enumeration-transaction.json",
    "Read-ComotePhase2JsonDocument",
    "Write-ComotePhase2JsonAtomically",
    "Enter-ComotePhase2RuntimeLock",
    "Exit-ComotePhase2RuntimeLock",
    "recovered-from-install-receipt",
    "installed-enumerated",
    "removed-legacy",
    "history",
    "state exists without an exact transaction or receipt",
    "no target was mutated"
)) {
    if ($sources.EnumerationRepair.IndexOf(
            $requiredText,
            [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Required enumeration recovery guard is missing: $requiredText"
    }
}
foreach ($forbiddenText in @(
    '"/force"',
    '"/reboot"',
    "DeviceIoControl",
    "Restart-Computer",
    "shutdown.exe",
    "verifier.exe"
)) {
    if ($sources.EnumerationRepair.IndexOf(
            $forbiddenText,
            [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Enumeration recovery crossed its boundary: $forbiddenText"
    }
}

foreach ($requiredText in @(
    "Test-Phase2TestSigningBoundary.ps1",
    "Test-Phase2EnumerationBoundary.ps1",
    "Test-Phase2RecoveryBoundary.ps1",
    "Repair-Phase2EnumerationState.ps1",
    "Test-Phase2EnumerationPreflight.ps1",
    "Repair-Phase2SigningReceipt.ps1",
    "ReplaceInvalidReceipt",
    "Install-Phase2Enumeration.ps1",
    "Test-Phase2InstalledState.ps1",
    "SigningSnapshotName",
    "RecoverySnapshotName",
    "must be different",
    "Enter-ComotePhase2RuntimeLock",
    "Exit-ComotePhase2RuntimeLock"
)) {
    if ($sources.Wrapper.IndexOf(
            $requiredText,
            [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Required repair/install orchestration is missing: $requiredText"
    }
}
$enumerationRepairIndex = $sources.Wrapper.IndexOf(
    "Repair-Phase2EnumerationState.ps1",
    [StringComparison]::OrdinalIgnoreCase
)
$firstPreflightIndex = $sources.Wrapper.IndexOf(
    "Test-Phase2EnumerationPreflight.ps1",
    $enumerationRepairIndex,
    [StringComparison]::OrdinalIgnoreCase
)
$signingRepairIndex = $sources.Wrapper.IndexOf(
    "Repair-Phase2SigningReceipt.ps1",
    $firstPreflightIndex,
    [StringComparison]::OrdinalIgnoreCase
)
$secondPreflightIndex = $sources.Wrapper.IndexOf(
    "Test-Phase2EnumerationPreflight.ps1",
    $signingRepairIndex,
    [StringComparison]::OrdinalIgnoreCase
)
$installIndex = $sources.Wrapper.IndexOf(
    "Install-Phase2Enumeration.ps1",
    $secondPreflightIndex,
    [StringComparison]::OrdinalIgnoreCase
)
$postInstallIndex = $sources.Wrapper.IndexOf(
    "Test-Phase2InstalledState.ps1",
    $installIndex,
    [StringComparison]::OrdinalIgnoreCase
)
if ($enumerationRepairIndex -lt 0 -or
    $firstPreflightIndex -lt $enumerationRepairIndex -or
    $signingRepairIndex -lt $firstPreflightIndex -or
    $secondPreflightIndex -lt $signingRepairIndex -or
    $installIndex -lt $secondPreflightIndex -or
    $postInstallIndex -lt $installIndex) {
    throw "Recovery, full preflight, repair, install, and verification order is invalid."
}
foreach ($forbiddenText in @(
    "pnputil.exe",
    "devgen.exe",
    "Remove-Item",
    "DeviceIoControl",
    "Restart-Computer",
    "shutdown.exe"
)) {
    if ($sources.Wrapper.IndexOf(
            $forbiddenText,
            [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "The one-command wrapper contains a direct mutation: $forbiddenText"
    }
}

Write-Host "Phase 2 transactional recovery boundaries verified." `
    -ForegroundColor Green