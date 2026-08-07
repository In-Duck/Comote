#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [switch]$AcknowledgeDisposableVm,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$RecoverySnapshotName,

    [ValidatePattern("^19045$")]
    [string]$RequiredBuildNumber = "19045"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot "Phase2Runtime.Common.ps1")
. (Join-Path $PSScriptRoot "Phase2Enumeration.Common.ps1")

$environment = Assert-ComotePhase2RuntimeEnvironment `
    -AcknowledgeDisposableVm:$AcknowledgeDisposableVm `
    -RequiredBuildNumber $RequiredBuildNumber
if (-not (Get-ComotePhase2TestSigningState)) {
    throw "TESTSIGNING is not configured."
}
$codeIntegrity = Get-ComotePhase2ActiveCodeIntegrityState
if (-not $codeIntegrity.TestSigningActive -or
    $codeIntegrity.HvciKernelModeActive) {
    throw "The current kernel is not ready for the Phase 2 test driver."
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$installReceiptPath = Join-Path `
    $projectRoot `
    "artifacts\phase2-runtime-state\enumeration-installation.json"
$signingReceiptPath = Join-Path `
    $projectRoot `
    "artifacts\phase2-runtime-state\test-signing-preparation.json"
if (-not (Test-Path -LiteralPath $installReceiptPath -PathType Leaf)) {
    throw "The Phase 2 installation receipt was not found."
}
$installReceipt = Read-ComotePhase2JsonDocument `
    -LiteralPath $installReceiptPath `
    -Description "Phase 2 installation receipt"
if ([string]$installReceipt.recoverySnapshotName -ne
        $RecoverySnapshotName -or
    [string]$installReceipt.manufacturer -ne
        [string]$environment.Computer.Manufacturer -or
    [string]$installReceipt.model -ne
        [string]$environment.Computer.Model -or
    [string]$installReceipt.osBuildNumber -ne
        [string]$environment.OperatingSystem.BuildNumber) {
    throw "The installation receipt does not match this VM and recovery snapshot."
}
$signingReceipt = Read-ComotePhase2JsonDocument `
    -LiteralPath $signingReceiptPath `
    -Description "Phase 2 signing receipt"
foreach ($requiredProperty in @(
    "schemaVersion",
    "status",
    "snapshotName",
    "certificateSubject",
    "certificateThumbprint",
    "certificateNotAfterUtc",
    "signedPackagePath",
    "files"
)) {
    if ($null -eq $signingReceipt.PSObject.Properties[$requiredProperty]) {
        throw "The Phase 2 signing receipt is missing: $requiredProperty"
    }
}
$thumbprint = ([string]$signingReceipt.certificateThumbprint).
    ToUpperInvariant()
$certificateSubject = [string]$signingReceipt.certificateSubject
if ([int]$signingReceipt.schemaVersion -ne 1 -or
    [string]$signingReceipt.status -ne "signed-package-ready" -or
    [string]$signingReceipt.snapshotName -ne
        [string]$installReceipt.signingSnapshotName -or
    $thumbprint -notmatch "^[0-9A-F]{40}$" -or
    $certificateSubject -notmatch
        "^CN=Comote Phase 2 VM Test Signing [0-9a-f]{32}$") {
    throw "The signing and installation receipts do not agree."
}

$allNamedCertificates = @(Get-ComotePhase2NamedCertificates)
if ($allNamedCertificates.Count -ne 3) {
    throw "Unexpected extra or missing installed Phase 2 certificates exist."
}
$certificateCopies = @(
    Get-ComotePhase2CertificateCopies -Thumbprint $thumbprint
)
if ($certificateCopies.Count -ne 3) {
    throw "The installed Phase 2 certificate is not present in exactly three stores."
}
$certificateExpiry = [DateTime]::Parse(
    [string]$signingReceipt.certificateNotAfterUtc,
    [Globalization.CultureInfo]::InvariantCulture,
    [Globalization.DateTimeStyles]::RoundtripKind
).ToUniversalTime()
if ($certificateExpiry -le [DateTime]::UtcNow.AddHours(12)) {
    throw "The installed Phase 2 certificate is expired or too near expiry."
}
foreach ($certificateCopy in $certificateCopies) {
    if ([string]$certificateCopy.Subject -ne $certificateSubject -or
        $certificateCopy.NotAfter.ToUniversalTime() -ne
            $certificateExpiry) {
        throw "An installed Phase 2 certificate copy does not match its receipt."
    }
}

$signedPackagePath = Join-Path $projectRoot "artifacts\phase2-test-signed"
$recordedSignedPackagePath = [IO.Path]::GetFullPath(
    [string]$signingReceipt.signedPackagePath
)
if (-not $recordedSignedPackagePath.Equals(
        [IO.Path]::GetFullPath($signedPackagePath),
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "The installed signing receipt points outside this Phase 2 project."
}
$expectedFileNames = @(
    "comotephase2test.cer",
    "comotevirtualhidphase2.cat",
    "comotevirtualhidphase2.inf",
    "comotevirtualhidphase2.sys",
    "comotevirtualhidprobe.exe"
)
$fileEntries = @($signingReceipt.files)
$actualFiles = @(
    Get-ChildItem -LiteralPath $signedPackagePath -File -ErrorAction Stop
)
if ($fileEntries.Count -ne $expectedFileNames.Count -or
    $actualFiles.Count -ne $expectedFileNames.Count) {
    throw "The installed Phase 2 signed-package inventory is invalid."
}
$recordedSysHash = $null
foreach ($expectedName in $expectedFileNames) {
    $entry = @(
        $fileEntries |
            Where-Object {
                ([string]$_.file).ToLowerInvariant() -eq $expectedName
            }
    )
    $file = @(
        $actualFiles |
            Where-Object {
                $_.Name.ToLowerInvariant() -eq $expectedName
            }
    )
    if ($entry.Count -ne 1 -or $file.Count -ne 1) {
        throw "The installed Phase 2 package is missing or duplicates: $expectedName"
    }
    $actualHash = (Get-FileHash `
        -Algorithm SHA256 `
        -LiteralPath $file[0].FullName).Hash
    if ($actualHash -ne [string]$entry[0].sha256) {
        throw "The installed Phase 2 package hash changed: $expectedName"
    }
    if ($expectedName -eq "comotevirtualhidphase2.sys") {
        $recordedSysHash = $actualHash
    }
}
foreach ($signedName in @(
    "ComoteVirtualHidPhase2.sys",
    "comotevirtualhidphase2.cat"
)) {
    $signature = Get-AuthenticodeSignature `
        -LiteralPath (Join-Path $signedPackagePath $signedName)
    if ($signature.Status -ne
            [System.Management.Automation.SignatureStatus]::Valid -or
        $signature.SignerCertificate.Thumbprint.ToUpperInvariant() -ne
            $thumbprint) {
        throw "A Phase 2 package signature does not match its receipt."
    }
}

$state = Assert-ComotePhase2InstalledState `
    -InstallReceipt $installReceipt
$systemRootPrefix = "\SystemRoot\"
$servicePath = [string]$state.DriverService.PathName
if (-not $servicePath.StartsWith(
        $systemRootPrefix,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "The loaded Phase 2 driver path is not rooted at SystemRoot."
}
$loadedDriverPath = Join-Path `
    $env:SystemRoot `
    $servicePath.Substring($systemRootPrefix.Length)
if (-not (Test-Path -LiteralPath $loadedDriverPath -PathType Leaf)) {
    throw "The loaded Phase 2 driver binary was not found."
}
$loadedDriverHash = (Get-FileHash `
    -Algorithm SHA256 `
    -LiteralPath $loadedDriverPath).Hash
if ($loadedDriverHash -ne $recordedSysHash) {
    throw "The loaded Phase 2 driver does not match the signed package."
}
$loadedDriverSignature = Get-AuthenticodeSignature `
    -LiteralPath $loadedDriverPath
if ($loadedDriverSignature.Status -ne
        [System.Management.Automation.SignatureStatus]::Valid -or
    $loadedDriverSignature.SignerCertificate.Thumbprint.ToUpperInvariant() -ne
        $thumbprint) {
    throw "The loaded Phase 2 driver signature does not match its receipt."
}
if ([bool]$installReceipt.probeLaunched -or
    [int]$installReceipt.inputReportsSubmitted -ne 0) {
    throw "The installation receipt unexpectedly reports prior input."
}

Write-Host ""
Write-Host "Phase 2 installed state is healthy." -ForegroundColor Green
Write-Host "Kernel service: $($state.DriverService.State)"
Write-Host "VHF keyboards: $($state.Keyboards.Count)"
Write-Host "VHF mice: $($state.Mice.Count)"
Write-Host "The probe has not been launched and no input report was submitted."
