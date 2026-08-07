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

. (Join-Path $PSScriptRoot "Phase1Runtime.Common.ps1")

[void](Assert-ComotePhase1RuntimeEnvironment `
    -AcknowledgeDisposableVm:$AcknowledgeDisposableVm `
    -RequiredBuildNumber $RequiredBuildNumber)
[void](Assert-ComotePhase1SigningPrerequisites)
Assert-ComotePhase1NoInstalledDevice

if (Get-ComotePhase1TestSigningState) {
    throw "TESTSIGNING is enabled; restore the clean snapshot."
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$signedPackagePath = Join-Path `
    $projectRoot `
    "artifacts\phase1-test-signed"
$receiptPath = Join-Path `
    $projectRoot `
    "artifacts\phase1-runtime-state\test-signing-preparation.json"
if (Test-Path -LiteralPath $signedPackagePath) {
    throw "The Phase 1 signed package directory already exists."
}
if (Test-Path -LiteralPath $receiptPath) {
    throw "The Phase 1 signing receipt already exists."
}

$certificateStores = @(
    "Cert:\CurrentUser\My",
    "Cert:\LocalMachine\My",
    "Cert:\LocalMachine\Root",
    "Cert:\LocalMachine\TrustedPublisher"
)
$matchingCertificates = @(
    foreach ($store in $certificateStores) {
        Get-ChildItem -LiteralPath $store -ErrorAction SilentlyContinue |
            Where-Object {
                [string]$_.Subject -like
                    "CN=Comote Phase 1 VM Test Signing *"
            }
    }
)
if ($matchingCertificates.Count -ne 0) {
    throw "A Comote Phase 1 VM test certificate is still present."
}

Write-Host ""
Write-Host "Phase 1 signing state is clean." -ForegroundColor Green
Write-Host "No test certificate, signing receipt, signed package, device, service, or TESTSIGNING state was found."
