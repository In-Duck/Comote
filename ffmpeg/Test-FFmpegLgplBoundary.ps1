[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$ffmpegRoot = Split-Path -Parent $PSCommandPath
$repositoryRoot = Split-Path -Parent $ffmpegRoot
$manifestPath = Join-Path $ffmpegRoot "manifest.json"
$manifest = Get-Content -LiteralPath $manifestPath -Raw |
    ConvertFrom-Json

$expectedBuild = "n8.1.2-32-gcfa62de001-20260730"
$expectedArchiveHash =
    "23429F940316EA92E376F6946C0A1F1B9043C930F3BC068228461D65AE24F8B8"
$expectedArchiveUrl =
    "https://github.com/BtbN/FFmpeg-Builds/releases/download/" +
    "autobuild-2026-07-30-13-32/" +
    "ffmpeg-n8.1.2-32-gcfa62de001-win64-lgpl-shared-8.1.zip"
$expectedNames = @(
    "avcodec-62.dll",
    "avdevice-62.dll",
    "avfilter-11.dll",
    "avformat-62.dll",
    "avutil-60.dll",
    "swresample-6.dll",
    "swscale-9.dll"
)

if ($manifest.schemaVersion -ne 2 -or
    $manifest.build -ne $expectedBuild -or
    $manifest.license -ne "LGPL-3.0-or-later" -or
    $manifest.architecture -ne "x86_64" -or
    $manifest.archive.url -ne $expectedArchiveUrl -or
    $manifest.archive.sha256 -ne $expectedArchiveHash -or
    $manifest.source.ffmpegCommit -ne
        "cfa62de001af8ffeb7e22561f246469c7b809951" -or
    $manifest.source.btbnBuildsCommit -ne
        "a99e8230eae00d1cee38f23076a7a1f55cd984e2") {
    throw "The FFmpeg manifest identity is not the approved pinned build."
}

$expectedAbi = @{
    avcodec = 62
    avdevice = 62
    avfilter = 11
    avformat = 62
    avutil = 60
    swresample = 6
    swscale = 9
}
foreach ($library in $expectedAbi.Keys) {
    if ([int]$manifest.abiMajors.$library -ne $expectedAbi[$library]) {
        throw "Unexpected FFmpeg ABI major: $library"
    }
}

$actualNames = @(
    Get-ChildItem -LiteralPath $ffmpegRoot -Filter "*.dll" -File |
        Sort-Object Name |
        ForEach-Object Name
)
if (($actualNames -join "`n") -ne
    (($expectedNames | Sort-Object) -join "`n")) {
    throw "The FFmpeg DLL inventory is not the approved LGPL set."
}

$manifestNames = @(
    $manifest.files |
        Sort-Object name |
        ForEach-Object name
)
if (($manifestNames -join "`n") -ne
    (($expectedNames | Sort-Object) -join "`n")) {
    throw "The manifest DLL inventory does not match the approved set."
}

foreach ($entry in $manifest.files) {
    $filePath = Join-Path $ffmpegRoot ([string]$entry.name)
    $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $filePath).Hash
    if ($actualHash -ne [string]$entry.sha256) {
        throw "FFmpeg hash mismatch: $($entry.name)"
    }

    $bytes = [IO.File]::ReadAllBytes($filePath)
    try {
        if ($bytes.Length -lt 0x40 -or
            $bytes[0] -ne 0x4D -or
            $bytes[1] -ne 0x5A) {
            throw "Not a valid PE image: $($entry.name)"
        }

        $peOffset = [BitConverter]::ToInt32($bytes, 0x3C)
        if ($peOffset -lt 0 -or
            $peOffset + 6 -gt $bytes.Length -or
            $bytes[$peOffset] -ne 0x50 -or
            $bytes[$peOffset + 1] -ne 0x45 -or
            [BitConverter]::ToUInt16($bytes, $peOffset + 4) -ne 0x8664) {
            throw "FFmpeg library is not an x64 PE image: $($entry.name)"
        }
    }
    finally {
        [Array]::Clear($bytes, 0, $bytes.Length)
    }

    $versionInfo =
        [Diagnostics.FileVersionInfo]::GetVersionInfo($filePath)
    if ($versionInfo.ProductVersion -notlike
        "n8.1.2-32-gcfa62de001-20260730*") {
        throw "Unexpected FFmpeg build version: $($entry.name)"
    }
}

foreach ($requiredNotice in @(
    "LICENSE.LGPLv3.txt",
    "LICENSE.SIPSorceryMedia.FFmpeg.LGPL-2.1.txt",
    "LICENSE.FFmpeg.AutoGen.MIT.txt",
    "NOTICE.md",
    "SOURCE_OFFER.md",
    "manifest.json"
)) {
    $noticePath = Join-Path $ffmpegRoot $requiredNotice
    if (-not (Test-Path -LiteralPath $noticePath -PathType Leaf)) {
        throw "Required multimedia distribution notice is missing: $requiredNotice"
    }
}

$avcodecText = [Text.Encoding]::ASCII.GetString(
    [IO.File]::ReadAllBytes((Join-Path $ffmpegRoot "avcodec-62.dll")))
if (-not $avcodecText.Contains("--disable-libx264") -or
    -not $avcodecText.Contains("h264_mf") -or
    -not $avcodecText.Contains("H264 via MediaFoundation")) {
    throw "The approved LGPL codec boundary or h264_mf fallback is missing."
}

$sourceFiles = @(
    Join-Path $repositoryRoot "Host\FFmpegExtractor.cs"
    Join-Path $repositoryRoot "Host\Host.csproj"
    Join-Path $repositoryRoot "Host\WebRTCManager.cs"
    Join-Path $repositoryRoot "Viewer\FFmpegExtractor.cs"
    Join-Path $repositoryRoot "Viewer\Viewer.csproj"
    Join-Path $repositoryRoot "InputCore\EmbeddedNativeLibraryExtractor.cs"
)
$sourceText = $sourceFiles |
    ForEach-Object { Get-Content -LiteralPath $_ -Raw } |
    Out-String
if ($sourceText -match "postproc-58" -or
    $sourceText -match '"libx264"' -or
    $sourceText -match "avcodec-61" -or
    $sourceText -match "avformat-61") {
    throw "Comote still references an obsolete or GPL-only FFmpeg component."
}
if ($sourceText -notmatch "SIPSorceryMedia.FFmpeg.*10.0.12" -or
    $sourceText -notmatch "h264_mf" -or
    $sourceText -notmatch "--allow-modified-ffmpeg" -or
    $sourceText -notmatch "allowExplicitOverride") {
    throw "The FFmpeg 8.1 wrapper or explicit relinking boundary is missing."
}

Write-Host "FFmpeg LGPL and ABI boundary verified."
Write-Host "Build: $($manifest.build)"
Write-Host "Libraries: $($expectedNames.Count)"
Write-Host "AutoGen ABI: FFmpeg 8.1"
Write-Host "Software H.264 fallback: h264_mf"
