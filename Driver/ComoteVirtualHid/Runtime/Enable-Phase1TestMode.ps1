#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [switch]$AcknowledgeDisposableVm,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$SnapshotName,

    [ValidatePattern("^19045$")]
    [string]$RequiredBuildNumber = "19045",

    [switch]$ValidateOnly
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot "Phase1Runtime.Common.ps1")

[void](Assert-ComotePhase1RuntimeEnvironment `
    -AcknowledgeDisposableVm:$AcknowledgeDisposableVm `
    -RequiredBuildNumber $RequiredBuildNumber)
[void](Assert-ComotePhase1SigningPrerequisites)
Assert-ComotePhase1NoInstalledDevice

if (Get-ComotePhase1TestSigningState) {
    throw "TESTSIGNING is already enabled; no BCD change was made."
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$receiptPath = Join-Path `
    $projectRoot `
    "artifacts\phase1-runtime-state\test-signing-preparation.json"
if (-not (Test-Path -LiteralPath $receiptPath -PathType Leaf)) {
    throw "The Phase 1 signing receipt was not found."
}
$receipt = Get-Content -LiteralPath $receiptPath -Raw |
    ConvertFrom-Json
if ([string]$receipt.status -ne "signed-package-ready" -or
    [string]$receipt.snapshotName -ne $SnapshotName -or
    [bool]$receipt.testSigningInitiallyEnabled -or
    [bool]$receipt.testSigningChangedByComote) {
    throw "The Phase 1 signing receipt is not eligible for TESTSIGNING."
}

$thumbprint = [string]$receipt.certificateThumbprint
if ($thumbprint -notmatch "^[0-9A-Fa-f]{40}$" -or
    [string]$receipt.certificateSubject -notlike
        "CN=Comote Phase 1 VM Test Signing *") {
    throw "The Phase 1 signing receipt contains an invalid certificate identity."
}
foreach ($requiredCertificatePath in @(
    "Cert:\LocalMachine\My\$thumbprint",
    "Cert:\LocalMachine\Root\$thumbprint",
    "Cert:\LocalMachine\TrustedPublisher\$thumbprint"
)) {
    if (-not (Test-Path -LiteralPath $requiredCertificatePath)) {
        throw "A required Phase 1 certificate copy is missing: $requiredCertificatePath"
    }
    $copy = Get-Item -LiteralPath $requiredCertificatePath
    if ([string]$copy.Subject -ne [string]$receipt.certificateSubject) {
        throw "A Phase 1 certificate subject does not match its receipt."
    }
}

$signedPackagePath = [string]$receipt.signedPackagePath
$expectedSignedPackagePath = Join-Path `
    $projectRoot `
    "artifacts\phase1-test-signed"
if ([IO.Path]::GetFullPath($signedPackagePath) -ne
    [IO.Path]::GetFullPath($expectedSignedPackagePath)) {
    throw "The signed package path does not match this project."
}
foreach ($entry in @($receipt.files)) {
    $filePath = Join-Path $signedPackagePath ([string]$entry.file)
    if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) {
        throw "A signed package file is missing: $filePath"
    }
    $actualHash = (Get-FileHash `
        -Algorithm SHA256 `
        -LiteralPath $filePath).Hash
    if ($actualHash -ne [string]$entry.sha256) {
        throw "The signed package changed after preparation: $filePath"
    }
}
foreach ($signedFileName in @(
    "ComoteVirtualHid.sys",
    "ComoteVirtualHid.cat"
)) {
    $signedFile = Join-Path $signedPackagePath $signedFileName
    $signature = Get-AuthenticodeSignature -LiteralPath $signedFile
    if ($signature.Status -ne
        [System.Management.Automation.SignatureStatus]::Valid -or
        $signature.SignerCertificate.Thumbprint -ne $thumbprint) {
        throw "The signed package failed Authenticode validation: $signedFile"
    }
}

if ($ValidateOnly.IsPresent) {
    Write-Host ""
    Write-Host "Phase 1 signed-package and BCD rollback state is ready." -ForegroundColor Green
    Write-Host "TESTSIGNING is off; no BCD change was made."
    Write-Host "No device was created and no driver was installed."
    return
}

$bcdChanged = $false
try {
    $bcdSetOutput = (& bcdedit.exe `
        /set `
        "{current}" `
        testsigning `
        on 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) {
        throw "BCDEdit rejected TESTSIGNING: $bcdSetOutput"
    }
    $bcdChanged = $true
    if (-not (Get-ComotePhase1TestSigningState)) {
        throw "BCDEdit returned success but TESTSIGNING was not recorded."
    }

    Set-ComotePhase1NoteProperty `
        -InputObject $receipt `
        -Name "status" `
        -Value "test-mode-reboot-required"
    Set-ComotePhase1NoteProperty `
        -InputObject $receipt `
        -Name "testSigningChangedByComote" `
        -Value $true
    Set-ComotePhase1NoteProperty `
        -InputObject $receipt `
        -Name "testSigningRequestedUtc" `
        -Value ([DateTime]::UtcNow.ToString("o"))
    $receipt | ConvertTo-Json -Depth 6 |
        Set-Content -LiteralPath $receiptPath -Encoding UTF8
}
catch {
    if ($bcdChanged) {
        & bcdedit.exe /set "{current}" testsigning off | Out-Null
    }
    throw
}

Write-Host ""
Write-Host "TESTSIGNING was recorded for the next boot." -ForegroundColor Yellow
Write-Host "No device was created and no driver was installed."
Write-Host "Restart the VM manually, then create a new VMware snapshot."
