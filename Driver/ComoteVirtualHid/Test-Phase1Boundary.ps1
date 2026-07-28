#Requires -Version 5.1
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$sourcePath = Join-Path $PSScriptRoot "ComoteVirtualHid.c"
$headerPath = Join-Path $PSScriptRoot "ComoteVirtualHid.h"
$projectPath = Join-Path $PSScriptRoot "ComoteVirtualHid.vcxproj"
$infPath = Join-Path $PSScriptRoot "ComoteVirtualHid.inf"
$buildPath = Join-Path $PSScriptRoot "Build-Phase1.ps1"
$vmBuildPath = Join-Path $PSScriptRoot "Invoke-Phase1VmBuild.ps1"

foreach ($path in @(
    $sourcePath,
    $headerPath,
    $projectPath,
    $infPath,
    $buildPath,
    $vmBuildPath
)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required Phase 1 source file is missing: $path"
    }
}

$combinedSource = @(
    (Get-Content -LiteralPath $sourcePath -Raw)
    (Get-Content -LiteralPath $headerPath -Raw)
) -join "`n"

$forbiddenTokens = @(
    "VhfReadReportSubmit",
    "WdfIoQueueCreate",
    "WdfDeviceCreateDeviceInterface",
    "WdfDeviceInitAssignName",
    "WdfRequestRetrieveInputBuffer",
    "WdfRequestRetrieveOutputBuffer",
    "METHOD_NEITHER",
    "MmMapIoSpace",
    "MmMapLockedPages",
    "ZwCreateFile",
    "PsCreateSystemThread"
)

foreach ($token in $forbiddenTokens) {
    if ($combinedSource.IndexOf(
            $token,
            [StringComparison]::Ordinal) -ge 0) {
        throw "Phase 1 safety boundary violation: $token"
    }
}

$requiredTokens = @(
    "VHF_CONFIG_INIT",
    "VhfCreate",
    "VhfStart",
    "VhfDelete",
    "ComoteEvtDeviceCleanup",
    "WdfDeviceInitSetExclusive",
    "WdfExecutionLevelPassive"
)
foreach ($token in $requiredTokens) {
    if ($combinedSource.IndexOf(
            $token,
            [StringComparison]::Ordinal) -lt 0) {
        throw "Required Phase 1 safety primitive is missing: $token"
    }
}

$buildScripts = @(
    (Get-Content -LiteralPath $buildPath -Raw)
    (Get-Content -LiteralPath $vmBuildPath -Raw)
) -join "`n"

foreach ($token in @(
    "pnputil",
    "devcon",
    "bcdedit",
    "New-SelfSignedCertificate",
    "certutil",
    "signtool",
    "verifier.exe"
)) {
    if ($buildScripts.IndexOf(
            $token,
            [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Phase 1 build script must not sign, install, or change boot state: $token"
    }
}

$inf = Get-Content -LiteralPath $infPath -Raw
foreach ($requiredInfText in @(
    'ROOT\COMOTEVIRTUALHID',
    '"LowerFilters",0x00010000,"vhf"',
    "StartType=3",
    "PnpLockdown=1"
)) {
    if ($inf.IndexOf(
            $requiredInfText,
            [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Required INF safety setting is missing: $requiredInfText"
    }
}

Write-Host "Phase 1 source boundary verified." -ForegroundColor Green
