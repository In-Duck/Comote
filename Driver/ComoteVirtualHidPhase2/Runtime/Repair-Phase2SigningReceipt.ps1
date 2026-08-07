#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [switch]$AcknowledgeDisposableVm,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$SigningSnapshotName,

    [switch]$ReplaceInvalidReceipt,

    [ValidatePattern("^19045$")]
    [string]$RequiredBuildNumber = "19045"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot "Phase2Runtime.Common.ps1")
. (Join-Path $PSScriptRoot "Phase2Enumeration.Common.ps1")

$runtimeLock = Enter-ComotePhase2RuntimeLock
try {

$environment = Assert-ComotePhase2RuntimeEnvironment `
    -AcknowledgeDisposableVm:$AcknowledgeDisposableVm `
    -RequiredBuildNumber $RequiredBuildNumber
$prerequisites = Assert-ComotePhase2SigningPrerequisites
Assert-ComotePhase2NoInstalledDevice
if (@(Get-ComotePhase2DriverPackages).Count -ne 0) {
    throw "A Phase 2 Driver Store package already exists."
}
if (-not (Get-ComotePhase2TestSigningState)) {
    throw "TESTSIGNING is not configured."
}
$codeIntegrity = Get-ComotePhase2ActiveCodeIntegrityState
if (-not $codeIntegrity.TestSigningActive -or
    $codeIntegrity.HvciKernelModeActive) {
    throw "The active kernel is not ready for the Phase 2 test driver."
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$artifactsRoot = Join-Path $projectRoot "artifacts"
$unsignedPackagePath = Join-Path $artifactsRoot "phase2-unsigned"
$signedPackagePath = Join-Path $artifactsRoot "phase2-test-signed"
$stateDirectory = Join-Path $artifactsRoot "phase2-runtime-state"
$receiptPath = Join-Path `
    $stateDirectory `
    "test-signing-preparation.json"
$receiptNeedsArchive = $false
$invalidReceiptPath = $null
if (Test-Path -LiteralPath $receiptPath) {
    if (-not $ReplaceInvalidReceipt.IsPresent) {
        throw "The Phase 2 signing receipt already exists."
    }
    $receiptNeedsArchive = $true
    $historyDirectory = Join-Path $stateDirectory "history"
    $invalidReceiptPath = Join-Path `
        $historyDirectory `
        ("invalid-signing-receipt-{0}.json" -f
            [Guid]::NewGuid().ToString("N"))
}
if (-not (Test-Path -LiteralPath $signedPackagePath -PathType Container)) {
    throw "The partial state does not contain a signed package directory."
}

$expectedNames = @(
    "comotephase2test.cer",
    "comotevirtualhidphase2.cat",
    "comotevirtualhidphase2.inf",
    "comotevirtualhidphase2.sys",
    "comotevirtualhidprobe.exe"
)
$actualFiles = @(
    Get-ChildItem -LiteralPath $signedPackagePath -File
)
if ($actualFiles.Count -ne $expectedNames.Count) {
    throw "The partial signed package has an unexpected file count."
}
foreach ($actualFile in $actualFiles) {
    if ($expectedNames -notcontains
        $actualFile.Name.ToLowerInvariant()) {
        throw "Unexpected partial signed-package file: $($actualFile.Name)"
    }
}
foreach ($expectedName in $expectedNames) {
    if (@(
            $actualFiles |
                Where-Object {
                    $_.Name.ToLowerInvariant() -eq $expectedName
                }
        ).Count -ne 1) {
        throw "The partial signed package is missing or duplicates: $expectedName"
    }
}

$sysPath = Join-Path $signedPackagePath "ComoteVirtualHidPhase2.sys"
$catalogPath = Join-Path `
    $signedPackagePath `
    "comotevirtualhidphase2.cat"
$infPath = Join-Path $signedPackagePath "ComoteVirtualHidPhase2.inf"
$probePath = Join-Path $signedPackagePath "ComoteVirtualHidProbe.exe"
$certificatePath = Join-Path $signedPackagePath "ComotePhase2Test.cer"
$exportedCertificate = New-Object `
    -TypeName Security.Cryptography.X509Certificates.X509Certificate2 `
    -ArgumentList $certificatePath
$thumbprint = $exportedCertificate.Thumbprint.ToUpperInvariant()
$certificateSubject = [string]$exportedCertificate.Subject
if ($thumbprint -notmatch "^[0-9A-F]{40}$" -or
    $certificateSubject -notmatch
        "^CN=Comote Phase 2 VM Test Signing [0-9a-f]{32}$") {
    throw "The exported Phase 2 certificate identity is invalid."
}

$allNamedCertificates = @(Get-ComotePhase2NamedCertificates)
if ($allNamedCertificates.Count -ne 3) {
    throw "Unexpected extra or missing Phase 2 certificate-store copies exist."
}
$certificateCopies = @(
    Get-ComotePhase2CertificateCopies -Thumbprint $thumbprint
)
if ($certificateCopies.Count -ne 3) {
    throw "The Phase 2 certificate is not present in exactly three stores."
}
foreach ($certificateCopy in $certificateCopies) {
    if ([string]$certificateCopy.Subject -ne $certificateSubject) {
        throw "A certificate copy does not match the exported certificate."
    }
}
foreach ($requiredCertificatePath in @(
    "Cert:\LocalMachine\My\$thumbprint",
    "Cert:\LocalMachine\Root\$thumbprint",
    "Cert:\LocalMachine\TrustedPublisher\$thumbprint"
)) {
    if (-not (Test-Path -LiteralPath $requiredCertificatePath)) {
        throw "A required Phase 2 certificate store copy is missing."
    }
}
$privateCertificate = Get-Item `
    -LiteralPath "Cert:\LocalMachine\My\$thumbprint"
if (-not $privateCertificate.HasPrivateKey -or
    -not (Test-ComotePhase2CodeSigningEku `
        -Certificate $privateCertificate) -or
    $privateCertificate.NotAfter.ToUniversalTime() -le
        [DateTime]::UtcNow.AddHours(12)) {
    throw "The Phase 2 private certificate is invalid or too near expiry."
}

foreach ($signedPath in @($sysPath, $catalogPath)) {
    $signature = Get-AuthenticodeSignature -LiteralPath $signedPath
    if ($signature.Status -ne
        [System.Management.Automation.SignatureStatus]::Valid -or
        $signature.SignerCertificate.Thumbprint -ne $thumbprint) {
        throw "The partial package signature is invalid: $signedPath"
    }
}
$probeSignature = Get-AuthenticodeSignature -LiteralPath $probePath
if ($probeSignature.Status -ne
    [System.Management.Automation.SignatureStatus]::NotSigned) {
    throw "The Phase 2 probe must remain unsigned."
}

$unsignedManifestPath = Join-Path `
    $unsignedPackagePath `
    "SHA256.json"
if (-not (Test-Path -LiteralPath $unsignedManifestPath -PathType Leaf)) {
    throw "The Phase 2 unsigned manifest is missing."
}
$manifestDocument = Get-Content `
    -LiteralPath $unsignedManifestPath `
    -Raw |
    ConvertFrom-Json
if ($manifestDocument -is [Array]) {
    $manifestEntries = $manifestDocument
}
else {
    $manifestEntries = @($manifestDocument)
}
foreach ($unchangedName in @(
    "ComoteVirtualHidPhase2.inf",
    "ComoteVirtualHidProbe.exe"
)) {
    $manifestMatch = @(
        $manifestEntries |
            Where-Object {
                [string]$_.file -eq $unchangedName
            }
    )
    if ($manifestMatch.Count -ne 1) {
        throw "The unsigned manifest is missing: $unchangedName"
    }
    $signedCopyPath = Join-Path $signedPackagePath $unchangedName
    $actualHash = (Get-FileHash `
        -Algorithm SHA256 `
        -LiteralPath $signedCopyPath).Hash
    if ($actualHash -ne [string]$manifestMatch[0].sha256) {
        throw "An unchanged signed-package file differs from its build: $unchangedName"
    }
}

$programFilesX86 = [Environment]::GetFolderPath(
    [Environment+SpecialFolder]::ProgramFilesX86
)
$kitRoot = Join-Path $programFilesX86 "Windows Kits\10"
$signTool = Find-ComotePhase2Tool `
    -Root (Join-Path $kitRoot "bin") `
    -Name "signtool.exe"
$inf2Cat = Find-ComotePhase2Tool `
    -Root (Join-Path $kitRoot "bin") `
    -Name "inf2cat.exe" `
    -PreferredArchitectures @("x86", "x64", "amd64")
if (-not $signTool -or -not $inf2Cat) {
    throw "Required WDK signing tools were not found."
}
$verifySysResult = Invoke-ComotePhase2NativeCommand `
    -FilePath $signTool `
    -Arguments @("verify", "/pa", $sysPath)
$verifySys = $verifySysResult.Output
if ($verifySysResult.ExitCode -ne 0) {
    throw "SignTool rejected the Phase 2 driver: $verifySys"
}
$verifyCatalogResult = Invoke-ComotePhase2NativeCommand `
    -FilePath $signTool `
    -Arguments @("verify", "/pa", $catalogPath)
$verifyCatalog = $verifyCatalogResult.Output
if ($verifyCatalogResult.ExitCode -ne 0) {
    throw "SignTool rejected the Phase 2 catalog: $verifyCatalog"
}
$verifyMembershipResult = Invoke-ComotePhase2NativeCommand `
    -FilePath $signTool `
    -Arguments @(
        "verify", "/pa", "/c", $catalogPath, $infPath
    )
$verifyMembership = $verifyMembershipResult.Output
if ($verifyMembershipResult.ExitCode -ne 0) {
    throw "The Phase 2 INF is not in the signed catalog: $verifyMembership"
}

$hashes = @(
    $actualFiles |
        Sort-Object Name |
        ForEach-Object {
            [ordered]@{
                file = $_.Name
                sha256 = (Get-FileHash `
                    -Algorithm SHA256 `
                    -LiteralPath $_.FullName).Hash
            }
        }
)
$currentVersion = Get-ItemProperty `
    -LiteralPath "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion"
$receipt = [ordered]@{
    schemaVersion = 1
    status = "signed-package-ready"
    createdUtc = [DateTime]::UtcNow.ToString("o")
    reconstructedFromValidatedPartialState = $true
    snapshotName = $SigningSnapshotName
    manufacturer = [string]$environment.Computer.Manufacturer
    model = [string]$environment.Computer.Model
    osBuildNumber = [string]$environment.OperatingSystem.BuildNumber
    osUbr = [int]$currentVersion.UBR
    secureBootEnabled = $prerequisites.SecureBootEnabled
    bitLockerProtection = $prerequisites.BitLockerProtection
    testSigningInitiallyEnabled = $true
    testSigningChangedByComote = $false
    activeCodeIntegrityOptions = [uint32]$codeIntegrity.Options
    certificateSubject = $certificateSubject
    certificateThumbprint = $thumbprint
    certificateNotAfterUtc =
        $privateCertificate.NotAfter.ToUniversalTime().ToString("o")
    signedPackagePath = $signedPackagePath
    signTool = $signTool
    inf2Cat = $inf2Cat
    files = $hashes
}
New-Item -ItemType Directory -Path $stateDirectory -Force |
    Out-Null
if ($receiptNeedsArchive) {
    [IO.Directory]::CreateDirectory(
        [IO.Path]::GetDirectoryName($invalidReceiptPath)
    ) | Out-Null
    Move-Item `
        -LiteralPath $receiptPath `
        -Destination $invalidReceiptPath `
        -ErrorAction Stop
}
Write-ComotePhase2JsonAtomically `
    -LiteralPath $receiptPath `
    -InputObject $receipt

Write-Host ""
Write-Host "Phase 2 signing receipt was reconstructed safely." -ForegroundColor Green
Write-Host "Existing package signatures, certificate stores, and hashes were verified."
Write-Host "No certificate was created and no driver or device was installed."
Write-Host "Receipt: $receiptPath"
}
finally {
    Exit-ComotePhase2RuntimeLock -Mutex $runtimeLock
}
