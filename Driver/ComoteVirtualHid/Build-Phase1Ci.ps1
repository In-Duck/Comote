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
    throw "The Phase 1 CI build requires a Windows runner."
}

if ([Environment]::GetEnvironmentVariable("GITHUB_ACTIONS") -ne "true") {
    throw "Build-Phase1Ci.ps1 is restricted to GitHub Actions."
}

$projectRoot = $PSScriptRoot
$project = Join-Path $projectRoot "ComoteVirtualHid.vcxproj"
$packagesConfig = Join-Path $projectRoot "packages.config"
$packagesDirectory = Join-Path $projectRoot "packages"
$boundaryTest = Join-Path $projectRoot "Test-Phase1Boundary.ps1"

function Find-ComoteWdkTool {
    param(
        [Parameter(Mandatory)]
        [string]$Name
    )

    $matches = Get-ChildItem `
        -LiteralPath $packagesDirectory `
        -Filter $Name `
        -File `
        -Recurse |
        Sort-Object FullName

    $preferred = $matches |
        Where-Object FullName -Match '\\(x64|amd64)\\' |
        Select-Object -First 1
    if ($null -ne $preferred) {
        return $preferred.FullName
    }

    $fallback = $matches | Select-Object -First 1
    if ($null -eq $fallback) {
        throw "WDK tool was not restored: $Name"
    }

    return $fallback.FullName
}

foreach ($file in @($project, $packagesConfig, $boundaryTest)) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        throw "Required CI build input was not found: $file"
    }
}

$nuget = Get-Command nuget.exe -ErrorAction Stop
$programFilesX86 = [Environment]::GetFolderPath(
    [Environment+SpecialFolder]::ProgramFilesX86
)
$vsWhere = Join-Path $programFilesX86 "Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path -LiteralPath $vsWhere -PathType Leaf)) {
    throw "vswhere.exe was not found on the Windows runner."
}

$msbuild = & $vsWhere -latest -products * `
    -requires Microsoft.Component.MSBuild `
    -find "MSBuild\**\Bin\MSBuild.exe" |
    Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($msbuild) -or
    -not (Test-Path -LiteralPath $msbuild -PathType Leaf)) {
    throw "MSBuild.exe was not found on the Windows runner."
}

& $boundaryTest

& $nuget.Source restore $packagesConfig `
    -PackagesDirectory $packagesDirectory `
    -NonInteractive
if ($LASTEXITCODE -ne 0) {
    throw "WDK NuGet restore failed with exit code $LASTEXITCODE."
}

$stampInf = Find-ComoteWdkTool -Name "stampinf.exe"
$wdkToolDirectory = Split-Path -Parent $stampInf
$wdkBinRoot = Split-Path -Parent $wdkToolDirectory
$wdkX86Directory = Join-Path $wdkBinRoot "x86"
if (-not (Test-Path -LiteralPath $wdkX86Directory -PathType Container)) {
    throw "The restored WDK x86 tool directory was not found: $wdkX86Directory"
}
$env:PATH = "$wdkToolDirectory;$wdkX86Directory;$env:PATH"
Write-Host "WDK versioned bin root: $wdkBinRoot"

Push-Location -LiteralPath $wdkBinRoot
try {
    & $msbuild $project `
        /t:Rebuild `
        /m:1 `
        /p:Configuration=$Configuration `
        /p:Platform=x64 `
        /p:RunCodeAnalysis=true
    $buildExitCode = $LASTEXITCODE
}
finally {
    Pop-Location
}
if ($buildExitCode -ne 0) {
    throw "Driver CI build failed with exit code $buildExitCode."
}

$buildOutput = Join-Path $projectRoot "bin\x64\$Configuration"
$sys = Join-Path $buildOutput "ComoteVirtualHid.sys"
$inf = Join-Path $projectRoot "ComoteVirtualHid.inf"
foreach ($file in @($sys, $inf)) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        throw "Expected CI build output was not found: $file"
    }
}

$infVerif = Find-ComoteWdkTool -Name "InfVerif.exe"
$inf2Cat = Find-ComoteWdkTool -Name "Inf2Cat.exe"

$stage = Join-Path $projectRoot "artifacts\phase1-ci-unsigned"
if (Test-Path -LiteralPath $stage) {
    $resolvedStage = (Resolve-Path -LiteralPath $stage).Path
    $resolvedRoot = (Resolve-Path -LiteralPath $projectRoot).Path
    if (-not $resolvedStage.StartsWith(
            $resolvedRoot,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe CI stage path: $resolvedStage"
    }
    Remove-Item -LiteralPath $stage -Recurse -Force
}
New-Item -ItemType Directory -Path $stage -Force | Out-Null
Copy-Item -LiteralPath $sys -Destination $stage
Copy-Item -LiteralPath $inf -Destination $stage

Push-Location -LiteralPath $wdkBinRoot
try {
    & $infVerif /k (Join-Path $stage "ComoteVirtualHid.inf")
    $infVerifExitCode = $LASTEXITCODE
    if ($infVerifExitCode -ne 0) {
        throw "CI InfVerif failed with exit code $infVerifExitCode."
    }

    & $inf2Cat "/driver:$stage" "/os:$Inf2CatOs" /verbose
    $inf2CatExitCode = $LASTEXITCODE
}
finally {
    Pop-Location
}
if ($inf2CatExitCode -ne 0) {
    throw "CI Inf2Cat failed with exit code $inf2CatExitCode."
}

$catalog = Join-Path $stage "ComoteVirtualHid.cat"
if (-not (Test-Path -LiteralPath $catalog -PathType Leaf)) {
    throw "Inf2Cat did not create ComoteVirtualHid.cat."
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
Write-Host "Phase 1 CI build gate passed." -ForegroundColor Green
Write-Host "No file was signed and no driver was installed or loaded."
