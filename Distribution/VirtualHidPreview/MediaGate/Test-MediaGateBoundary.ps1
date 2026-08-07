[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$mediaGateRoot = $PSScriptRoot
$repositoryRoot = (
    Resolve-Path -LiteralPath (
        Join-Path $mediaGateRoot "..\..\.."
    )
).Path

$requiredFiles = @(
    "Comote.MediaGate.csproj",
    "ExpectedRelease.cs",
    "GateEnvironment.cs",
    "GateModels.cs",
    "MediaProbe.cs",
    "NativeWorkspace.cs",
    "packages.lock.json",
    "Program.cs",
    "README.md",
    "ReceiptValidator.cs",
    "StrictJson.cs",
    "Test-MediaGateBoundary.ps1",
    "WorkerModels.cs"
)

$actualFiles = @(
    Get-ChildItem `
        -LiteralPath $mediaGateRoot `
        -File |
    ForEach-Object Name |
    Sort-Object
)

if (
    [string]::Join(
        "`n",
        $actualFiles
    ) -cne [string]::Join(
        "`n",
        ($requiredFiles | Sort-Object)
    )
) {
    throw "MediaGate source inventory is not exact."
}

$projectPath = Join-Path `
    $mediaGateRoot `
    "Comote.MediaGate.csproj"

[xml]$project = Get-Content `
    -LiteralPath $projectPath `
    -Raw

$properties = $project.Project.PropertyGroup

if (
    $properties.TargetFramework -cne "net10.0-windows" -or
    $properties.RuntimeIdentifiers -cne "win-x64" -or
    $properties.AssemblyName -cne "Comote.MediaGate" -or
    $properties.RuntimeFrameworkVersion -cne "10.0.10" -or
    $properties.TargetLatestRuntimePatch -cne "false" -or
    $properties.RollForward -cne "Disable" -or
    $properties.PublishTrimmed -cne "false" -or
    $properties.AllowUnsafeBlocks -cne "true" -or
    $properties.TreatWarningsAsErrors -cne "true"
) {
    throw "MediaGate project security properties are invalid."
}
if (@($project.SelectNodes("//RuntimeIdentifiers")).Count -ne 1 -or
    @($project.SelectNodes("//RuntimeIdentifier")).Count -ne 0) {
    throw "MediaGate must declare only plural RuntimeIdentifiers win-x64."
}

$packageReferences = @{}

foreach (
    $reference in @(
        $project.SelectNodes(
            "/Project/ItemGroup/PackageReference"
        )
    )
) {
    if ($null -ne $reference) {
        $packageReferences[
            [string]$reference.Include
        ] = [string]$reference.Version
    }
}

if (
    $packageReferences.Count -ne 4 -or
    $packageReferences["FFmpeg.AutoGen"] -cne "8.1.0" -or
    $packageReferences[
        "Microsoft.Extensions.DependencyInjection.Abstractions"
    ] -cne "10.0.10" -or
    $packageReferences[
        "Microsoft.Extensions.Logging.Abstractions"
    ] -cne "10.0.10" -or
    $packageReferences[
        "SIPSorceryMedia.FFmpeg"
    ] -cne "10.0.12"
) {
    throw "MediaGate managed dependency pins are invalid."
}

$embeddedResources = @(
    $project.SelectNodes(
        "/Project/ItemGroup/EmbeddedResource"
    ) |
    ForEach-Object {
        [PSCustomObject]@{
            Include = [string]$_.Include
            LogicalName = [string]$_.LogicalName
        }
    }
)

$nativeResources = @(
    $embeddedResources |
    Where-Object {
        $_.LogicalName -clike `
            "Comote.MediaGate.Native.*"
    }
)

if (
    $embeddedResources.Count -ne 8 -or
    $nativeResources.Count -ne 7
) {
    throw "MediaGate must embed one manifest and seven native DLLs."
}

$manifestPath = Join-Path `
    $repositoryRoot `
    "ffmpeg\manifest.json"

$manifest = Get-Content `
    -LiteralPath $manifestPath `
    -Raw |
    ConvertFrom-Json

$expectedAbi = [ordered]@{
    avcodec = 62
    avdevice = 62
    avfilter = 11
    avformat = 62
    avutil = 60
    swresample = 6
    swscale = 9
}

$expectedNames = @(
    "avcodec-62.dll",
    "avdevice-62.dll",
    "avfilter-11.dll",
    "avformat-62.dll",
    "avutil-60.dll",
    "swresample-6.dll",
    "swscale-9.dll"
)

if (
    [int]$manifest.schemaVersion -ne 2 -or
    [string]$manifest.build -cne `
        "n8.1.2-32-gcfa62de001-20260730" -or
    [string]$manifest.license -cne `
        "LGPL-3.0-or-later" -or
    [bool]$manifest.redistributable -ne $true -or
    [bool]$manifest.gplEnabled -ne $false -or
    [string]$manifest.softwareH264Fallback -cne `
        "h264_mf" -or
    [string]$manifest.encoderOptions.rate_control -cne `
        "cbr" -or
    [string]$manifest.encoderOptions.scenario -cne `
        "display_remoting" -or
    [string]$manifest.encoderOptions.hw_encoding -cne `
        "0"
) {
    throw "Canonical FFmpeg manifest identity is invalid."
}

foreach ($entry in $expectedAbi.GetEnumerator()) {
    if (
        [int]$manifest.abiMajors.(
            $entry.Key
        ) -ne $entry.Value
    ) {
        throw "Canonical FFmpeg ABI is invalid: $($entry.Key)."
    }
}

$manifestNames = @(
    $manifest.files |
    ForEach-Object {
        [string]$_.name
    } |
    Sort-Object
)

if (
    [string]::Join(
        "`n",
        $manifestNames
    ) -cne [string]::Join(
        "`n",
        ($expectedNames | Sort-Object)
    )
) {
    throw "Canonical FFmpeg file inventory is invalid."
}

foreach ($file in $manifest.files) {
    $path = Join-Path `
        $repositoryRoot `
        ("ffmpeg\" + [string]$file.name)
    $info = Get-Item -LiteralPath $path
    $hash = (
        Get-FileHash `
            -LiteralPath $path `
            -Algorithm SHA256
    ).Hash

    if (
        [long]$file.length -ne $info.Length -or
        [string]$file.sha256 -cne $hash
    ) {
        throw "FFmpeg asset integrity failed: $($file.name)."
    }
}

$source = [string]::Join(
    "`n",
    @(
        Get-ChildItem `
            -LiteralPath $mediaGateRoot `
            -Filter "*.cs" `
            -File |
        Sort-Object Name |
        Get-Content
    )
)

$requiredSourceFragments = @(
    'internal const string RuntimeVersion = "10.0.10";',
    'internal const int WindowsBuild = 19045;',
    'internal const string EncoderName = "h264_mf";',
    '["rate_control"] = "cbr"',
    '["scenario"] = "display_remoting"',
    '["hw_encoding"] = "0"',
    '["rate_control"] = 0',
    '["scenario"] = 1',
    '["hw_encoding"] = 0',
    'UsedSyntheticFramesOnly = true',
    'FFmpegVideoEncoder(options)',
    'av_opt_get_int(',
    'Process.Start(startInfo)',
    'Kill(entireProcessTree: true)',
    'MediaGate must run inside a recognised release VM.',
    'FileMode.CreateNew'
)

foreach ($fragment in $requiredSourceFragments) {
    if (-not $source.Contains($fragment)) {
        throw "MediaGate source boundary is missing: $fragment"
    }
}

$forbiddenSourceFragments = @(
    "System.Net",
    "HttpClient",
    "WebClient",
    "TcpClient",
    "UdpClient",
    "ScreenCapture",
    "BitBlt",
    "pnputil",
    "bcdedit",
    "verifier.exe",
    '"libx264"',
    "AV_CODEC_ID_HEVC",
    "AV_CODEC_ID_AV1"
)

foreach ($fragment in $forbiddenSourceFragments) {
    if ($source.Contains($fragment)) {
        throw "MediaGate source contains a forbidden capability: $fragment"
    }
}

$hostSource = Get-Content `
    -LiteralPath (
        Join-Path $repositoryRoot "Host\WebRTCManager.cs"
    ) `
    -Raw

if (
    $hostSource.Contains("AV_CODEC_ID_HEVC") -or
    $hostSource.Contains("AV_CODEC_ID_AV1") -or
    $hostSource.Contains("libx264") -or
    -not $hostSource.Contains(
        "new FFmpegVideoEncoder("
    ) -or
    -not $hostSource.Contains(
        "CreateMediaFoundationFallbackOptions()"
    ) -or
    -not $hostSource.Contains(
        "Signalling and packetisation are intentionally H.264-only."
    )
) {
    throw "Host H.264-only encoder boundary is invalid."
}

Write-Host "Comote MediaGate static boundary verified." `
    -ForegroundColor Green
Write-Host "Native FFmpeg libraries: 7"
Write-Host "Runtime floor: .NET 10.0.10"
Write-Host "Target: Windows 10 build 19045 VM"
Write-Host "Encoder: h264_mf"
