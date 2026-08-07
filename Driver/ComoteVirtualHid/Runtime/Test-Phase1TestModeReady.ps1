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

. (Join-Path $PSScriptRoot "Phase1Runtime.Common.ps1")

$environment = Assert-ComotePhase1RuntimeEnvironment `
    -AcknowledgeDisposableVm:$AcknowledgeDisposableVm `
    -RequiredBuildNumber $RequiredBuildNumber
$prerequisites = Assert-ComotePhase1SigningPrerequisites
Assert-ComotePhase1NoInstalledDevice

if (-not (Get-ComotePhase1TestSigningState)) {
    throw "TESTSIGNING is not active after reboot."
}

$codeIntegrityState = Get-ComotePhase1ActiveCodeIntegrityState
if (-not $codeIntegrityState.TestSigningActive) {
    throw "TESTSIGNING is configured in BCD but is not active in the current kernel. Restart the VM before continuing."
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
$eligibleReceiptStatuses = @(
    "test-mode-reboot-required",
    "test-mode-ready"
)
if ($eligibleReceiptStatuses -notcontains [string]$receipt.status -or
    -not [bool]$receipt.testSigningChangedByComote -or
    [bool]$receipt.testSigningInitiallyEnabled) {
    throw "The Phase 1 signing receipt is not eligible for test-mode verification."
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

$memoryIntegrityRunning = $false
$deviceGuard = Get-CimInstance `
    -Namespace "root\Microsoft\Windows\DeviceGuard" `
    -ClassName Win32_DeviceGuard `
    -ErrorAction SilentlyContinue
if ($null -ne $deviceGuard) {
    $memoryIntegrityRunning =
        @($deviceGuard.SecurityServicesRunning) -contains 2
}

$currentVersion = Get-ItemProperty `
    -LiteralPath "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion"
Set-ComotePhase1NoteProperty `
    -InputObject $receipt `
    -Name "status" `
    -Value "test-mode-ready"
Set-ComotePhase1NoteProperty `
    -InputObject $receipt `
    -Name "testModeVerifiedUtc" `
    -Value ([DateTime]::UtcNow.ToString("o"))
Set-ComotePhase1NoteProperty `
    -InputObject $receipt `
    -Name "testModeSnapshotName" `
    -Value $SnapshotName
Set-ComotePhase1NoteProperty `
    -InputObject $receipt `
    -Name "memoryIntegrityRunning" `
    -Value $memoryIntegrityRunning
Set-ComotePhase1NoteProperty `
    -InputObject $receipt `
    -Name "activeCodeIntegrityOptions" `
    -Value ([uint32]$codeIntegrityState.Options)
Set-ComotePhase1NoteProperty `
    -InputObject $receipt `
    -Name "activeTestSigning" `
    -Value $true
$receipt | ConvertTo-Json -Depth 6 |
    Set-Content -LiteralPath $receiptPath -Encoding UTF8

$reportDirectory = Join-Path `
    $projectRoot `
    "artifacts\phase1-runtime-preflight"
$reportPath = Join-Path `
    $reportDirectory `
    ("test-mode-ready-{0}.json" -f (Get-Date -Format "yyyyMMdd-HHmmss"))
$report = [ordered]@{
    completedUtc = [DateTime]::UtcNow.ToString("o")
    status = "passed"
    note = "TESTSIGNING and package readiness verified. No device was created and no driver was installed."
    snapshotName = $SnapshotName
    manufacturer = [string]$environment.Computer.Manufacturer
    model = [string]$environment.Computer.Model
    osBuildNumber = [string]$environment.OperatingSystem.BuildNumber
    osUbr = [int]$currentVersion.UBR
    secureBootEnabled = $prerequisites.SecureBootEnabled
    bitLockerProtection = $prerequisites.BitLockerProtection
    testSigningEnabled = $true
    activeTestSigning = $true
    activeCodeIntegrityOptions = [uint32]$codeIntegrityState.Options
    hvciKernelModeActive = $codeIntegrityState.HvciKernelModeActive
    memoryIntegrityRunning = $memoryIntegrityRunning
    certificateThumbprint = $thumbprint
    signedPackagePath = $signedPackagePath
}
$report | ConvertTo-Json -Depth 4 |
    Set-Content -LiteralPath $reportPath -Encoding UTF8

Write-Host ""
Write-Host "Phase 1 test mode is ready." -ForegroundColor Green
Write-Host "No device was created and no driver was installed."
Write-Host "Active Code Integrity TESTSIGN bit: True"
Write-Host ("Code Integrity options: 0x{0:X}" -f
    [uint32]$codeIntegrityState.Options)
Write-Host "Memory integrity running: $memoryIntegrityRunning"
Write-Host "Report: $reportPath"
