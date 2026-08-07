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

if (-not (Get-ComotePhase1TestSigningState)) {
    throw "TESTSIGNING is not configured in the current BCD entry."
}

$codeIntegrityState = Get-ComotePhase1ActiveCodeIntegrityState
if (-not $codeIntegrityState.TestSigningActive) {
    throw "TESTSIGNING is configured in BCD but is not active in the current kernel. Restart the VM before continuing."
}

Write-Host ""
Write-Host "Active Phase 1 test mode check passed." -ForegroundColor Green
Write-Host "No system state was changed."
Write-Host ("Code Integrity options: 0x{0:X}" -f
    [uint32]$codeIntegrityState.Options)
Write-Host ("HVCI kernel-mode enforcement active: {0}" -f
    [bool]$codeIntegrityState.HvciKernelModeActive)