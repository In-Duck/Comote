#Requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$Inf2CatOs = "10_X64"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if ($env:OS -ne "Windows_NT") {
    throw "Comote Virtual HID must be built on Windows with Visual Studio and the WDK."
}

$projectRoot = $PSScriptRoot
$project = Join-Path $projectRoot "ComoteVirtualHid.vcxproj"
$boundaryTest = Join-Path $projectRoot "Test-Phase1Boundary.ps1"
$programFilesX86 = [Environment]::GetFolderPath(
    [Environment+SpecialFolder]::ProgramFilesX86
)
$vsWhere = Join-Path $programFilesX86 "Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path -LiteralPath $vsWhere -PathType Leaf)) {
    throw "vswhere.exe was not found. Install Visual Studio 2022 with C++ tools."
}

$msbuild = & $vsWhere -latest -products * `
    -requires Microsoft.Component.MSBuild `
    -find "MSBuild\**\Bin\MSBuild.exe" |
    Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($msbuild) -or
    -not (Test-Path -LiteralPath $msbuild -PathType Leaf)) {
    throw "MSBuild.exe was not found."
}

$kitBinRoot = Join-Path $programFilesX86 "Windows Kits\10\bin"
if (-not (Test-Path -LiteralPath $kitBinRoot -PathType Container)) {
    throw "Windows Driver Kit tools were not found."
}

$kitVersion = Get-ChildItem -LiteralPath $kitBinRoot -Directory |
    Where-Object Name -Match '^\d+\.\d+\.\d+\.\d+$' |
    Sort-Object { [Version]$_.Name } -Descending |
    Select-Object -First 1
if ($null -eq $kitVersion) {
    throw "A versioned WDK tools directory was not found."
}

& $boundaryTest

$infVerif = Join-Path $kitVersion.FullName "x64\InfVerif.exe"
$inf2Cat = Join-Path $kitVersion.FullName "x86\Inf2Cat.exe"
foreach ($tool in @($infVerif, $inf2Cat)) {
    if (-not (Test-Path -LiteralPath $tool -PathType Leaf)) {
        throw "Required WDK tool was not found: $tool"
    }
}

& $msbuild $project `
    /t:Rebuild `
    /m:1 `
    /p:Configuration=$Configuration `
    /p:Platform=x64 `
    /p:RunCodeAnalysis=true
if ($LASTEXITCODE -ne 0) {
    throw "Driver build failed with exit code $LASTEXITCODE."
}

$buildOutput = Join-Path $projectRoot "bin\x64\$Configuration"
$sys = Join-Path $buildOutput "ComoteVirtualHid.sys"
$inf = Join-Path $projectRoot "ComoteVirtualHid.inf"
foreach ($file in @($sys, $inf)) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        throw "Expected build output was not found: $file"
    }
}

$stage = Join-Path $projectRoot "artifacts\phase1-unsigned"
if (Test-Path -LiteralPath $stage) {
    $resolvedStage = (Resolve-Path -LiteralPath $stage).Path
    $resolvedRoot = (Resolve-Path -LiteralPath $projectRoot).Path
    if (-not $resolvedStage.StartsWith(
            $resolvedRoot,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe stage path: $resolvedStage"
    }
    Remove-Item -LiteralPath $stage -Recurse -Force
}
New-Item -ItemType Directory -Path $stage -Force | Out-Null
Copy-Item -LiteralPath $sys -Destination $stage
Copy-Item -LiteralPath $inf -Destination $stage

& $infVerif /k (Join-Path $stage "ComoteVirtualHid.inf")
if ($LASTEXITCODE -ne 0) {
    throw "InfVerif failed with exit code $LASTEXITCODE."
}

& $inf2Cat "/driver:$stage" "/os:$Inf2CatOs" /verbose
if ($LASTEXITCODE -ne 0) {
    throw "Inf2Cat failed with exit code $LASTEXITCODE."
}

$manifest = Get-ChildItem -LiteralPath $stage -File |
    Sort-Object Name |
    ForEach-Object {
        [pscustomobject][ordered]@{
            file = $_.Name
            bytes = $_.Length
            sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash
        }
    }
$manifest |
    ConvertTo-Json |
    Set-Content -LiteralPath (Join-Path $stage "SHA256.json") -Encoding UTF8

Write-Host ""
Write-Host "Phase 1 unsigned package created:" -ForegroundColor Green
Write-Host $stage
Write-Host "No certificate was created, no file was signed, and no driver was installed."
