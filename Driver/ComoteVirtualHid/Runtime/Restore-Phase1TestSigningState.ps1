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
Assert-ComotePhase1NoInstalledDevice

$projectRoot = Split-Path -Parent $PSScriptRoot
$receiptPath = Join-Path `
    $projectRoot `
    "artifacts\phase1-runtime-state\test-signing-preparation.json"
if (-not (Test-Path -LiteralPath $receiptPath -PathType Leaf)) {
    throw "The Phase 1 signing receipt was not found."
}
$receipt = Get-Content -LiteralPath $receiptPath -Raw |
    ConvertFrom-Json
$thumbprint = [string]$receipt.certificateThumbprint
$subject = [string]$receipt.certificateSubject
if ($thumbprint -notmatch "^[0-9A-Fa-f]{40}$" -or
    $subject -notlike "CN=Comote Phase 1 VM Test Signing *") {
    throw "The Phase 1 signing receipt contains an invalid certificate identity."
}

$testSigningEnabled = Get-ComotePhase1TestSigningState
if ($testSigningEnabled -and
    -not [bool]$receipt.testSigningChangedByComote) {
    throw "TESTSIGNING was not enabled by this Phase 1 receipt; refusing to change it."
}

$bcdChanged = $false
if ($testSigningEnabled) {
    $bcdOutput = (& bcdedit.exe `
        /set `
        "{current}" `
        testsigning `
        off 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) {
        throw "BCDEdit rejected TESTSIGNING off: $bcdOutput"
    }
    $bcdChanged = $true
}

Remove-ComotePhase1TestCertificate `
    -Thumbprint $thumbprint `
    -Subject $subject

$restoredStatus = if ($bcdChanged) {
    "restore-reboot-required"
} else {
    "restored"
}
Set-ComotePhase1NoteProperty `
    -InputObject $receipt `
    -Name "status" `
    -Value $restoredStatus
Set-ComotePhase1NoteProperty `
    -InputObject $receipt `
    -Name "restoredUtc" `
    -Value ([DateTime]::UtcNow.ToString("o"))
$receipt | ConvertTo-Json -Depth 6 |
    Set-Content -LiteralPath $receiptPath -Encoding UTF8

Write-Host ""
Write-Host "Phase 1 test certificate copies were removed." -ForegroundColor Green
if ($bcdChanged) {
    Write-Host "TESTSIGNING was set to off for the next boot."
    Write-Host "Restart the VM manually to complete restoration."
} else {
    Write-Host "TESTSIGNING was already off; no reboot setting was changed."
}
Write-Host "No device or driver package was removed because none may be installed at this gate."
