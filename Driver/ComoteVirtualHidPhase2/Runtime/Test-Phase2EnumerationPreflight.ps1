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

. (Join-Path $PSScriptRoot "Phase2Runtime.Common.ps1")
. (Join-Path $PSScriptRoot "Phase2Enumeration.Common.ps1")

$environment = Assert-ComotePhase2RuntimeEnvironment `
    -AcknowledgeDisposableVm:$AcknowledgeDisposableVm `
    -RequiredBuildNumber $RequiredBuildNumber
[void](Assert-ComotePhase2SigningPrerequisites)
Assert-ComotePhase2NoInstalledDevice

if (-not (Get-ComotePhase2TestSigningState)) {
    throw "TESTSIGNING is not configured in the current BCD entry."
}
$codeIntegrity = Get-ComotePhase2ActiveCodeIntegrityState
if (-not $codeIntegrity.TestSigningActive) {
    throw "TESTSIGNING is not active in the current Windows kernel."
}
if ($codeIntegrity.HvciKernelModeActive) {
    throw "HVCI kernel-mode enforcement must remain inactive in the VM."
}

foreach ($commandName in @(
    "Get-WindowsDriver",
    "Get-PnpDevice",
    "Get-PnpDeviceProperty"
)) {
    if (-not (Get-Command $commandName -ErrorAction Stop)) {
        throw "Required Windows command was not found: $commandName"
    }
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$stateDirectory = Join-Path $projectRoot "artifacts\phase2-runtime-state"
$signingReceiptPath = Join-Path `
    $stateDirectory `
    "test-signing-preparation.json"
$installReceiptPath = Join-Path `
    $stateDirectory `
    "enumeration-installation.json"
$transactionPath = Join-Path `
    $stateDirectory `
    "enumeration-transaction.json"
if (Test-Path -LiteralPath $transactionPath) {
    throw "An interrupted enumeration transaction exists; run the recovery gate first."
}
if (-not (Test-Path -LiteralPath $signingReceiptPath -PathType Leaf)) {
    throw "The Phase 2 signing receipt was not found."
}
if (Test-Path -LiteralPath $installReceiptPath) {
    throw "A Phase 2 enumeration installation receipt already exists."
}

$signingReceipt = Read-ComotePhase2JsonDocument `
    -LiteralPath $signingReceiptPath `
    -Description "Phase 2 signing receipt"
foreach ($requiredProperty in @(
    "status",
    "snapshotName",
    "manufacturer",
    "model",
    "osBuildNumber",
    "testSigningChangedByComote",
    "activeCodeIntegrityOptions",
    "certificateSubject",
    "certificateThumbprint",
    "certificateNotAfterUtc",
    "signedPackagePath",
    "files"
)) {
    if ($null -eq
        $signingReceipt.PSObject.Properties[$requiredProperty]) {
        throw "The signing receipt is missing: $requiredProperty"
    }
}
if ([string]$signingReceipt.status -ne "signed-package-ready" -or
    [string]$signingReceipt.snapshotName -ne $SnapshotName -or
    [string]$signingReceipt.manufacturer -ne
        [string]$environment.Computer.Manufacturer -or
    [string]$signingReceipt.model -ne
        [string]$environment.Computer.Model -or
    [string]$signingReceipt.osBuildNumber -ne
        [string]$environment.OperatingSystem.BuildNumber -or
    [bool]$signingReceipt.testSigningChangedByComote -or
    -not ([uint32]$signingReceipt.activeCodeIntegrityOptions -band 0x02)) {
    throw "The Phase 2 signing receipt is not valid for this VM."
}

$thumbprint = ([string]$signingReceipt.certificateThumbprint).
    ToUpperInvariant()
$certificateSubject = [string]$signingReceipt.certificateSubject
if ($thumbprint -notmatch "^[0-9A-F]{40}$" -or
    $certificateSubject -notmatch
        "^CN=Comote Phase 2 VM Test Signing [0-9a-f]{32}$") {
    throw "The signing receipt contains an invalid certificate identity."
}
$allNamedCertificates = @(Get-ComotePhase2NamedCertificates)
if ($allNamedCertificates.Count -ne 3) {
    throw "Unexpected extra or missing Phase 2 certificate copies exist."
}
$certificateCopies = @(
    Get-ComotePhase2CertificateCopies -Thumbprint $thumbprint
)
if ($certificateCopies.Count -ne 3) {
    throw "The Phase 2 test certificate is not present in exactly three stores."
}
foreach ($certificateCopy in $certificateCopies) {
    if ([string]$certificateCopy.Subject -ne $certificateSubject) {
        throw "A Phase 2 certificate subject does not match its receipt."
    }
}
$privateCertificate = Get-Item `
    -LiteralPath "Cert:\LocalMachine\My\$thumbprint"
if (-not $privateCertificate.HasPrivateKey -or
    -not (Test-ComotePhase2CodeSigningEku `
        -Certificate $privateCertificate)) {
    throw "The private Phase 2 certificate is missing its key or code-signing EKU."
}
$expectedExpiry = [DateTime]::Parse(
    [string]$signingReceipt.certificateNotAfterUtc,
    [Globalization.CultureInfo]::InvariantCulture,
    [Globalization.DateTimeStyles]::RoundtripKind
).ToUniversalTime()
if ($privateCertificate.NotAfter.ToUniversalTime() -ne $expectedExpiry -or
    $expectedExpiry -le [DateTime]::UtcNow.AddHours(12)) {
    throw "The Phase 2 test certificate expiry is invalid or too near."
}

$signedPackagePath = [string]$signingReceipt.signedPackagePath
$expectedSignedPackagePath = Join-Path `
    $projectRoot `
    "artifacts\phase2-test-signed"
if ([IO.Path]::GetFullPath($signedPackagePath) -ne
    [IO.Path]::GetFullPath($expectedSignedPackagePath)) {
    throw "The signed package path does not match this Phase 2 project."
}

$fileDocument = $signingReceipt.files
if ($fileDocument -is [Array]) {
    $fileEntries = $fileDocument
}
else {
    $fileEntries = @($fileDocument)
}
$expectedFileNames = @(
    "comotephase2test.cer",
    "comotevirtualhidphase2.cat",
    "comotevirtualhidphase2.inf",
    "comotevirtualhidphase2.sys",
    "comotevirtualhidprobe.exe"
)
if ($fileEntries.Count -ne $expectedFileNames.Count) {
    throw "The signed package receipt has an unexpected file count."
}
foreach ($expectedFileName in $expectedFileNames) {
    $matchingEntries = @(
        $fileEntries |
            Where-Object {
                ([string]$_.file).ToLowerInvariant() -eq
                    $expectedFileName
            }
    )
    if ($matchingEntries.Count -ne 1) {
        throw "The signed package receipt is missing or duplicates: $expectedFileName"
    }
    $filePath = Join-Path `
        $signedPackagePath `
        ([string]$matchingEntries[0].file)
    if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) {
        throw "A signed package file is missing: $filePath"
    }
    $actualHash = (Get-FileHash `
        -Algorithm SHA256 `
        -LiteralPath $filePath).Hash
    if ($actualHash -ne [string]$matchingEntries[0].sha256) {
        throw "A signed package file changed after preparation: $filePath"
    }
}
$actualFileNames = @(
    Get-ChildItem -LiteralPath $signedPackagePath -File |
        ForEach-Object { $_.Name.ToLowerInvariant() }
)
if ($actualFileNames.Count -ne $expectedFileNames.Count) {
    throw "The signed package directory contains an unexpected file."
}
foreach ($actualFileName in $actualFileNames) {
    if ($expectedFileNames -notcontains $actualFileName) {
        throw "Unexpected signed package file: $actualFileName"
    }
}

$sysPath = Join-Path $signedPackagePath "ComoteVirtualHidPhase2.sys"
$catalogPath = Join-Path `
    $signedPackagePath `
    "comotevirtualhidphase2.cat"
$infPath = Join-Path $signedPackagePath "ComoteVirtualHidPhase2.inf"
$probePath = Join-Path $signedPackagePath "ComoteVirtualHidProbe.exe"
$certificatePath = Join-Path $signedPackagePath "ComotePhase2Test.cer"
foreach ($signedPath in @($sysPath, $catalogPath)) {
    $signature = Get-AuthenticodeSignature -LiteralPath $signedPath
    if ($signature.Status -ne
        [System.Management.Automation.SignatureStatus]::Valid -or
        $signature.SignerCertificate.Thumbprint -ne $thumbprint) {
        throw "The signed package failed Authenticode validation: $signedPath"
    }
}
$probeSignature = Get-AuthenticodeSignature -LiteralPath $probePath
if ($probeSignature.Status -ne
    [System.Management.Automation.SignatureStatus]::NotSigned) {
    throw "The Phase 2 probe must remain unsigned."
}
$exportedCertificate = New-Object `
    -TypeName Security.Cryptography.X509Certificates.X509Certificate2 `
    -ArgumentList $certificatePath
if ($exportedCertificate.Thumbprint.ToUpperInvariant() -ne $thumbprint) {
    throw "The exported certificate does not match the signing receipt."
}

$programFilesX86 = [Environment]::GetFolderPath(
    [Environment+SpecialFolder]::ProgramFilesX86
)
$kitRoot = Join-Path $programFilesX86 "Windows Kits\10"
$signTool = Find-ComotePhase2Tool `
    -Root (Join-Path $kitRoot "bin") `
    -Name "signtool.exe"
$devGen = Find-ComotePhase2Tool `
    -Root (Join-Path $kitRoot "Tools") `
    -Name "devgen.exe"
if (-not $signTool) {
    throw "SignTool.exe was not found in the installed Windows Kit."
}
if (-not $devGen) {
    throw "DevGen.exe was not found in the installed WDK."
}
$verifySysResult = Invoke-ComotePhase2NativeCommand `
    -FilePath $signTool `
    -Arguments @("verify", "/pa", $sysPath)
$verifySys = $verifySysResult.Output
if ($verifySysResult.ExitCode -ne 0) {
    throw "SignTool rejected the Phase 2 driver signature: $verifySys"
}
$verifyCatalogResult = Invoke-ComotePhase2NativeCommand `
    -FilePath $signTool `
    -Arguments @("verify", "/pa", $catalogPath)
$verifyCatalog = $verifyCatalogResult.Output
if ($verifyCatalogResult.ExitCode -ne 0) {
    throw "SignTool rejected the Phase 2 catalog signature: $verifyCatalog"
}
$verifyMembershipResult = Invoke-ComotePhase2NativeCommand `
    -FilePath $signTool `
    -Arguments @(
        "verify", "/pa", "/c", $catalogPath, $infPath
    )
$verifyMembership = $verifyMembershipResult.Output
if ($verifyMembershipResult.ExitCode -ne 0) {
    throw "The INF is not a member of the signed catalog: $verifyMembership"
}

if (@(Get-ComotePhase2DriverPackages).Count -ne 0) {
    throw "A Phase 2 Comote package already exists in the Driver Store."
}
if (@(Get-ComotePhase2RootDevices).Count -ne 0) {
    throw "A Phase 2 Comote root device already exists."
}
$presentDevices = @(
    Get-PnpDevice -PresentOnly -ErrorAction Stop |
        Where-Object {
            [string]$_.InstanceId -like
                "ROOT\COMOTEVIRTUALHID_PHASE2*" -or
            [string]$_.InstanceId -like
                "ROOT\DEVGEN\COMOTE_PHASE2*"
        }
)
if ($presentDevices.Count -ne 0) {
    throw "A present Phase 2 Comote PnP device already exists."
}

Write-Host ""
Write-Host "Phase 2 enumeration preflight passed." -ForegroundColor Green
Write-Host "Signed package, certificate stores, TESTSIGNING, and clean state: verified"
Write-Host "DevGen and installation prerequisites: available"
Write-Host "No package was staged and no device was created."
Write-Host "The probe was not launched and no input report was submitted."
