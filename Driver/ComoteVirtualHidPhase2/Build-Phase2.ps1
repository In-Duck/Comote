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
    throw "Comote Virtual HID Phase 2 must be built on Windows with Visual Studio and the WDK."
}

$projectRoot = $PSScriptRoot
$project = Join-Path $projectRoot "ComoteVirtualHidPhase2.vcxproj"
$probeProject = Join-Path `
    $projectRoot `
    "Probe\ComoteVirtualHidProbe.vcxproj"
$boundaryTest = Join-Path $projectRoot "Test-Phase2Boundary.ps1"

function Find-ComoteWdkTool {
    param(
        [Parameter(Mandatory)]
        [System.IO.DirectoryInfo]$VersionDirectory,

        [Parameter(Mandatory)]
        [string]$Name,

        [switch]$PreferX64
    )

    $matches = @(
        Get-ChildItem `
            -LiteralPath $VersionDirectory.FullName `
            -Filter $Name `
            -File `
            -Recurse `
            -ErrorAction SilentlyContinue |
        Sort-Object FullName
    )

    if ($PreferX64.IsPresent) {
        $preferred = $matches |
            Where-Object FullName -Match '\\(x64|amd64)\\' |
            Select-Object -First 1
        if ($null -ne $preferred) {
            return $preferred.FullName
        }
    }

    $fallback = $matches | Select-Object -First 1
    if ($null -ne $fallback) {
        return $fallback.FullName
    }

    return $null
}

$programFilesX86 = [Environment]::GetFolderPath(
    [Environment+SpecialFolder]::ProgramFilesX86
)
$vsWhere = Join-Path $programFilesX86 "Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path -LiteralPath $vsWhere -PathType Leaf)) {
    throw "vswhere.exe was not found. Install Visual Studio 2022 with C++ tools."
}

$msbuild = & $vsWhere -latest -products * `
    -requires Microsoft.Component.MSBuild `
    -find "MSBuild\**\Bin\amd64\MSBuild.exe" |
    Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($msbuild) -or
    -not (Test-Path -LiteralPath $msbuild -PathType Leaf)) {
    throw "64-bit MSBuild.exe was not found."
}

$kitRoot = Join-Path $programFilesX86 "Windows Kits\10"
$kitBinRoot = Join-Path $kitRoot "bin"
$kitToolsRoot = Join-Path $kitRoot "Tools"
if (-not (Test-Path -LiteralPath $kitBinRoot -PathType Container)) {
    throw "Windows Driver Kit tools were not found."
}

$kitVersions = Get-ChildItem -LiteralPath $kitBinRoot -Directory |
    Where-Object Name -Match '^\d+\.\d+\.\d+\.\d+$' |
    Sort-Object { [Version]$_.Name } -Descending
if (@($kitVersions).Count -eq 0) {
    throw "A versioned WDK tools directory was not found."
}

& $boundaryTest

$selectedKitVersion = $null
$stampInf = $null
$infVerif = $null
$inf2Cat = $null
foreach ($kitVersion in $kitVersions) {
    $toolsVersionPath = Join-Path $kitToolsRoot $kitVersion.Name
    $toolsVersionDirectory = $null
    if (Test-Path -LiteralPath $toolsVersionPath -PathType Container) {
        $toolsVersionDirectory = Get-Item -LiteralPath $toolsVersionPath
    }

    $candidateStampInf = Find-ComoteWdkTool `
        -VersionDirectory $kitVersion `
        -Name "stampinf.exe" `
        -PreferX64
    $candidateInfVerif = Find-ComoteWdkTool `
        -VersionDirectory $kitVersion `
        -Name "InfVerif.exe" `
        -PreferX64
    if (-not $candidateInfVerif -and $null -ne $toolsVersionDirectory) {
        $candidateInfVerif = Find-ComoteWdkTool `
            -VersionDirectory $toolsVersionDirectory `
            -Name "InfVerif.exe" `
            -PreferX64
    }
    $candidateInf2Cat = Find-ComoteWdkTool `
        -VersionDirectory $kitVersion `
        -Name "Inf2Cat.exe"

    if ($candidateStampInf -and $candidateInfVerif -and $candidateInf2Cat) {
        $selectedKitVersion = $kitVersion
        $stampInf = $candidateStampInf
        $infVerif = $candidateInfVerif
        $inf2Cat = $candidateInf2Cat
        break
    }
}

if ($null -eq $selectedKitVersion) {
    throw (("No complete WDK installation was found under {0}. " +
        "Install the Windows Driver Kit component; the Windows SDK alone is not enough.") -f
        $kitBinRoot)
}

$wdkToolDirectory = Split-Path -Parent $stampInf
$wdkX86Directory = Join-Path $selectedKitVersion.FullName "x86"
$infVerifDirectory = Split-Path -Parent $infVerif
$targetKitVersion = $selectedKitVersion.Name
$requiredWdkFiles = @(
    (Join-Path $kitRoot "Include\$targetKitVersion\km\ntddk.h"),
    (Join-Path $kitRoot "Lib\$targetKitVersion\km\x64\VhfKm.lib")
)
foreach ($requiredWdkFile in $requiredWdkFiles) {
    if (-not (Test-Path -LiteralPath $requiredWdkFile -PathType Leaf)) {
        throw ("The selected WDK {0} is incomplete or its matching SDK is missing: {1}" -f
            $targetKitVersion,
            $requiredWdkFile)
    }
}

$env:PATH = "$wdkToolDirectory;$wdkX86Directory;$infVerifDirectory;$env:PATH"
Write-Host "MSBuild host: $msbuild"
Write-Host "WDK tools root: $($selectedKitVersion.FullName)"
Write-Host "Windows target platform version: $targetKitVersion"

Push-Location -LiteralPath $selectedKitVersion.FullName
try {
    & $msbuild $project `
        /t:Rebuild `
        /m:1 `
        /p:Configuration=$Configuration `
        /p:Platform=x64 `
        /p:WindowsTargetPlatformVersion=$targetKitVersion `
        /p:SignMode=Off `
        /p:Inf2CatUseLocalTime=true `
        /p:RunCodeAnalysis=true
    $buildExitCode = $LASTEXITCODE
}
finally {
    Pop-Location
}
if ($buildExitCode -ne 0) {
    throw "Driver build failed with exit code $buildExitCode."
}
if (-not (Test-Path -LiteralPath $probeProject -PathType Leaf)) {
    throw "Phase 2 probe project was not found: $probeProject"
}
& $msbuild $probeProject `
    /t:Rebuild `
    /m:1 `
    /p:Configuration=$Configuration `
    /p:Platform=x64 `
    /p:PlatformToolset=v143 `
    /p:WindowsTargetPlatformVersion=$targetKitVersion `
    /p:RunCodeAnalysis=true
$probeBuildExitCode = $LASTEXITCODE
if ($probeBuildExitCode -ne 0) {
    throw "Phase 2 probe build failed with exit code $probeBuildExitCode."
}

$buildOutput = Join-Path $projectRoot "bin\driver\x64\$Configuration"
$sys = Join-Path $buildOutput "ComoteVirtualHidPhase2.sys"
$inf = Join-Path $projectRoot "ComoteVirtualHidPhase2.inf"
$probe = Join-Path `
    $projectRoot `
    "bin\probe\x64\$Configuration\ComoteVirtualHidProbe.exe"
foreach ($file in @($sys, $inf, $probe)) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        throw "Expected build output was not found: $file"
    }
}

$driverSignature = Get-AuthenticodeSignature -LiteralPath $sys
if ($driverSignature.Status -ne [System.Management.Automation.SignatureStatus]::NotSigned) {
    throw ("Phase 2 requires an unsigned SYS, but the build produced signature status {0}." -f
        $driverSignature.Status)
}

$generatedCertificates = @(
    Get-ChildItem -LiteralPath $buildOutput -Filter "*.cer" -File -Recurse
)
if ($generatedCertificates.Count -gt 0) {
    throw "Phase 2 must not generate or export a test certificate."
}

$stage = Join-Path $projectRoot "artifacts\phase2-unsigned"
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

Push-Location -LiteralPath $infVerifDirectory
try {
    & $infVerif /k (Join-Path $stage "ComoteVirtualHidPhase2.inf")
    $infVerifExitCode = $LASTEXITCODE
    if ($infVerifExitCode -ne 0) {
        throw "InfVerif failed with exit code $infVerifExitCode."
    }
}
finally {
    Pop-Location
}

Push-Location -LiteralPath $selectedKitVersion.FullName
try {
    & $inf2Cat "/driver:$stage" "/os:$Inf2CatOs" /uselocaltime /verbose
    $inf2CatExitCode = $LASTEXITCODE
}
finally {
    Pop-Location
}
if ($inf2CatExitCode -ne 0) {
    throw "Inf2Cat failed with exit code $inf2CatExitCode."
}

$cat = Join-Path $stage "ComoteVirtualHidPhase2.cat"
if (-not (Test-Path -LiteralPath $cat -PathType Leaf)) {
    throw "Inf2Cat did not create the expected catalog: $cat"
}
$catalogSignature = Get-AuthenticodeSignature -LiteralPath $cat
if ($catalogSignature.Status -ne
    [System.Management.Automation.SignatureStatus]::NotSigned) {
    throw ("Phase 2 requires an unsigned CAT, but its signature status is {0}." -f
        $catalogSignature.Status)
}

Copy-Item -LiteralPath $probe -Destination $stage

$probeSignature = Get-AuthenticodeSignature -LiteralPath $probe
if ($probeSignature.Status -ne
    [System.Management.Automation.SignatureStatus]::NotSigned) {
    throw ("Phase 2 requires an unsigned probe, but its signature status is {0}." -f
        $probeSignature.Status)
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
Write-Host "Phase 2 unsigned package created:" -ForegroundColor Green
Write-Host $stage
Write-Host "No certificate was created, no file was signed, and no driver was installed."
