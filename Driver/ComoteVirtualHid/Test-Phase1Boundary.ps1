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
$ciBuildPath = Join-Path $PSScriptRoot "Build-Phase1Ci.ps1"
$ciWorkflowPath = Join-Path `
    (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) `
    ".github\workflows\virtual-hid-phase1.yml"

foreach ($path in @(
    $sourcePath,
    $headerPath,
    $projectPath,
    $infPath,
    $buildPath,
    $vmBuildPath,
    $ciBuildPath,
    $ciWorkflowPath
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

$project = Get-Content -LiteralPath $projectPath -Raw
foreach ($requiredProjectText in @(
    "<SignMode>Off</SignMode>",
    "<Inf2CatUseLocalTime>true</Inf2CatUseLocalTime>"
)) {
    if ($project.IndexOf(
            $requiredProjectText,
            [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Required unsigned project boundary is missing: $requiredProjectText"
    }
}
$buildScripts = @(
    (Get-Content -LiteralPath $buildPath -Raw)
    (Get-Content -LiteralPath $vmBuildPath -Raw)
    (Get-Content -LiteralPath $ciBuildPath -Raw)
    (Get-Content -LiteralPath $ciWorkflowPath -Raw)
) -join "`n"

foreach ($token in @(
    "pnputil",
    "devcon",
    "bcdedit",
    "New-SelfSignedCertificate",
    "certutil",
    "signtool",
    "verifier.exe",
    "sc.exe",
    "dism.exe",
    "Add-WindowsDriver",
    "New-Service",
    "Start-Service"
)) {
    if ($buildScripts.IndexOf(
            $token,
            [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Phase 1 build script must not sign, install, or change boot state: $token"
    }
}

$localBuild = Get-Content -LiteralPath $buildPath -Raw
foreach ($requiredBuildText in @(
    "/p:SignMode=Off",
    "/p:Inf2CatUseLocalTime=true",
    "/uselocaltime",
    "Get-AuthenticodeSignature"
)) {
    if ($localBuild.IndexOf(
            $requiredBuildText,
            [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Required unsigned build gate is missing: $requiredBuildText"
    }
}

$ciBuild = Get-Content -LiteralPath $ciBuildPath -Raw
foreach ($requiredCiText in @(
    'GetEnvironmentVariable("GITHUB_ACTIONS")',
    "nuget",
    "Bin\amd64\MSBuild.exe",
    "stampinf.exe",
    "InfVerif.exe",
    "Inf2Cat.exe"
)) {
    if ($ciBuild.IndexOf(
            $requiredCiText,
            [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Required Phase 1 CI gate is missing: $requiredCiText"
    }
}

$ciWorkflow = Get-Content -LiteralPath $ciWorkflowPath -Raw
foreach ($requiredWorkflowText in @(
    "windows-2025-vs2026",
    "permissions:",
    "contents: read",
    "Build-Phase1Ci.ps1"
)) {
    if ($ciWorkflow.IndexOf(
            $requiredWorkflowText,
            [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Required Phase 1 workflow boundary is missing: $requiredWorkflowText"
    }
}

$inf = Get-Content -LiteralPath $infPath -Raw
foreach ($requiredInfText in @(
    'ROOT\COMOTEVIRTUALHID',
    '"LowerFilters",0x00010000,"vhf"',
    "DefaultDestDir=13",
    "ComoteDriverCopy=13",
    "ServiceBinary=%13%\ComoteVirtualHid.sys",
    "NTamd64.10.0...19045",
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
