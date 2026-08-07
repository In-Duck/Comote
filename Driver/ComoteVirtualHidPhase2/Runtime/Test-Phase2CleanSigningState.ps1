#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [switch]$AcknowledgeDisposableVm,

    [ValidatePattern("^19045$")]
    [string]$RequiredBuildNumber = "19045"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot "Phase2Runtime.Common.ps1")
. (Join-Path $PSScriptRoot "Phase2Enumeration.Common.ps1")

[void](Assert-ComotePhase2RuntimeEnvironment `
    -AcknowledgeDisposableVm:$AcknowledgeDisposableVm `
    -RequiredBuildNumber $RequiredBuildNumber)
[void](Assert-ComotePhase2SigningPrerequisites)
Assert-ComotePhase2NoInstalledDevice
if (@(Get-ComotePhase2DriverPackages).Count -ne 0) {
    throw "A Phase 2 Driver Store package is still present."
}


$projectRoot = Split-Path -Parent $PSScriptRoot
$signedPackagePath = Join-Path `
    $projectRoot `
    "artifacts\phase2-test-signed"
$receiptPath = Join-Path `
    $projectRoot `
    "artifacts\phase2-runtime-state\test-signing-preparation.json"
if (Test-Path -LiteralPath $signedPackagePath) {
    throw "The Phase 2 signed package directory already exists."
}
if (Test-Path -LiteralPath $receiptPath) {
    throw "The Phase 2 signing receipt already exists."
}
$enumerationReceiptPath = Join-Path `
    $projectRoot `
    "artifacts\phase2-runtime-state\enumeration-installation.json"
$enumerationTransactionPath = Join-Path `
    $projectRoot `
    "artifacts\phase2-runtime-state\enumeration-transaction.json"
if (Test-Path -LiteralPath $enumerationReceiptPath) {
    throw "An active Phase 2 enumeration receipt still exists."
}
if (Test-Path -LiteralPath $enumerationTransactionPath) {
    throw "An active Phase 2 enumeration transaction still exists."
}


$matchingCertificates = @(Get-ComotePhase2NamedCertificates)
if ($matchingCertificates.Count -ne 0) {
    throw "A Comote Phase 2 VM test certificate is still present."
}

Write-Host ""
Write-Host "Phase 2 signing state is clean." -ForegroundColor Green
Write-Host "No Phase 2 test certificate, signing receipt, signed package, device, or service was found.`nTESTSIGNING was not changed or evaluated by this clean-state audit."
