#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [switch]$AcknowledgeDisposableVm,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$SnapshotName,

    [switch]$ValidateOnly,

    [ValidatePattern("^19045$")]
    [string]$RequiredBuildNumber = "19045"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot "Phase2Runtime.Common.ps1")
. (Join-Path $PSScriptRoot "Phase2Enumeration.Common.ps1")

$runtimeLock = Enter-ComotePhase2RuntimeLock
try {

[void](Assert-ComotePhase2RuntimeEnvironment `
    -AcknowledgeDisposableVm:$AcknowledgeDisposableVm `
    -RequiredBuildNumber $RequiredBuildNumber)
Assert-ComotePhase2NoInstalledDevice
if (@(Get-ComotePhase2DriverPackages).Count -ne 0) {
    throw "A Phase 2 Driver Store package still exists; remove it first."
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$artifactsRoot = Join-Path $projectRoot "artifacts"
$signedPackagePath = Join-Path $artifactsRoot "phase2-test-signed"
$receiptPath = Join-Path `
    $artifactsRoot `
    "phase2-runtime-state\test-signing-preparation.json"
$receipt = Read-ComotePhase2JsonDocument `
    -LiteralPath $receiptPath `
    -Description "Phase 2 signing receipt"

$thumbprint = ([string]$receipt.certificateThumbprint).ToUpperInvariant()
$subject = [string]$receipt.certificateSubject
$status = [string]$receipt.status
if ([int]$receipt.schemaVersion -ne 1 -or
    $status -notin @("signed-package-ready", "signing-removal") -or
    $thumbprint -notmatch "^[0-9A-F]{40}$" -or
    $subject -notmatch
        "^CN=Comote Phase 2 VM Test Signing [0-9a-f]{32}$") {
    throw "The Phase 2 signing receipt identity is invalid."
}
if ([bool]$receipt.testSigningChangedByComote) {
    throw "This Phase 2 receipt must not own the VM TESTSIGNING state."
}
if ([string]$receipt.snapshotName -ne $SnapshotName) {
    throw "The supplied snapshot name does not match the Phase 2 signing receipt."
}
$expectedSignedPath = [IO.Path]::GetFullPath($signedPackagePath)
$recordedSignedPath = [IO.Path]::GetFullPath(
    [string]$receipt.signedPackagePath
)
if (-not $recordedSignedPath.Equals(
        $expectedSignedPath,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "The Phase 2 signing receipt points outside the expected package path."
}

$allNamedCertificates = @(Get-ComotePhase2NamedCertificates)
$certificateCopies = @(
    Get-ComotePhase2CertificateCopies -Thumbprint $thumbprint
)
foreach ($namedCertificate in $allNamedCertificates) {
    if ($namedCertificate.Thumbprint.ToUpperInvariant() -ne $thumbprint -or
        [string]$namedCertificate.Subject -ne $subject) {
        throw "An additional Phase 2 test certificate prevents exact rollback."
    }
}
foreach ($copy in $certificateCopies) {
    if ([string]$copy.Subject -ne $subject) {
        throw "A certificate thumbprint matched but its subject did not."
    }
}
if ($status -eq "signed-package-ready" -and
    $certificateCopies.Count -ne 3) {
    throw "The ready signing state does not contain exactly three certificate copies."
}
if ($status -eq "signing-removal" -and
    $certificateCopies.Count -gt 3) {
    throw "The resumable signing removal contains unexpected certificate copies."
}

$packageExists = Test-Path `
    -LiteralPath $signedPackagePath `
    -PathType Container
if ($status -eq "signed-package-ready" -and -not $packageExists) {
    throw "The ready Phase 2 signed package directory is missing."
}
if ($packageExists) {
    $fileDocument = $receipt.files
    $fileEntries = if ($fileDocument -is [Array]) {
        $fileDocument
    }
    else {
        @($fileDocument)
    }
    $expectedFileNames = @(
        "comotephase2test.cer",
        "comotevirtualhidphase2.cat",
        "comotevirtualhidphase2.inf",
        "comotevirtualhidphase2.sys",
        "comotevirtualhidprobe.exe"
    )
    $actualFiles = @(
        Get-ChildItem -LiteralPath $signedPackagePath -File -ErrorAction Stop
    )
    if ($fileEntries.Count -ne $expectedFileNames.Count -or
        $actualFiles.Count -ne $expectedFileNames.Count) {
        throw "The signed package file inventory is invalid."
    }
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
            throw "The signed package is missing or duplicates: $expectedName"
        }
        $actualHash = (Get-FileHash `
            -Algorithm SHA256 `
            -LiteralPath $file[0].FullName).Hash
        if ($actualHash -ne [string]$entry[0].sha256) {
            throw "The signed package hash changed: $expectedName"
        }
    }

    $certificatePath = Join-Path $signedPackagePath "ComotePhase2Test.cer"
    $exportedCertificate = New-Object `
        -TypeName Security.Cryptography.X509Certificates.X509Certificate2 `
        -ArgumentList $certificatePath
    if ($exportedCertificate.Thumbprint.ToUpperInvariant() -ne $thumbprint -or
        [string]$exportedCertificate.Subject -ne $subject) {
        throw "The exported certificate does not match the rollback receipt."
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
            throw "A signed package signature does not match the rollback receipt."
        }
    }
}

if ($ValidateOnly.IsPresent) {
    Write-Host ""
    Write-Host "Phase 2 signing rollback validation passed." `
        -ForegroundColor Green
    Write-Host "The exact certificate, package, and receipt are eligible for removal."
    Write-Host "TESTSIGNING will remain unchanged."
    Write-Host "No system state was changed."
    return
}

Set-ComotePhase2NoteProperty `
    -InputObject $receipt `
    -Name "status" `
    -Value "signing-removal"
Set-ComotePhase2NoteProperty `
    -InputObject $receipt `
    -Name "signingRemovalStartedUtc" `
    -Value ([DateTime]::UtcNow.ToString("o"))
Write-ComotePhase2JsonAtomically `
    -LiteralPath $receiptPath `
    -InputObject $receipt

if (Test-Path -LiteralPath $signedPackagePath) {
    Remove-Item `
        -LiteralPath $signedPackagePath `
        -Recurse `
        -Force `
        -ErrorAction Stop
}
if (@(Get-ComotePhase2CertificateCopies -Thumbprint $thumbprint).Count -ne 0) {
    Remove-ComotePhase2TestCertificate `
        -Thumbprint $thumbprint `
        -Subject $subject
}
Remove-Item -LiteralPath $receiptPath -Force -ErrorAction Stop

if (@(Get-ComotePhase2CertificateCopies -Thumbprint $thumbprint).Count -ne 0 -or
    (Test-Path -LiteralPath $signedPackagePath) -or
    (Test-Path -LiteralPath $receiptPath)) {
    throw "Phase 2 signing rollback audit failed."
}

Write-Host ""
Write-Host "Phase 2 test-signing state was removed safely." -ForegroundColor Green
Write-Host "The test certificate, signed package, and receipt were removed."
Write-Host "TESTSIGNING was not changed."
Write-Host "No device or driver package was removed because none was installed."
}
finally {
    Exit-ComotePhase2RuntimeLock -Mutex $runtimeLock
}
