using System.Text.Json.Serialization;

namespace Comote.MediaGate;

internal sealed class FfmpegReceipt
{
    public int SchemaVersion { get; init; }
    public string? Component { get; init; }
    public string? Build { get; init; }
    public string? License { get; init; }
    public string? Architecture { get; init; }
    public bool Redistributable { get; init; }
    public bool GplEnabled { get; init; }
    public string? SoftwareH264Fallback { get; init; }
    public Dictionary<string, string> EncoderOptions { get; init; } = [];
    public ArchiveMetadata Archive { get; init; } = new();
    public ArchiveMetadata PublisherChecksums { get; init; } = new();
    public SourceMetadata Source { get; init; } = new();
    public List<ManagedComponent> ManagedComponents { get; init; } = [];
    public Dictionary<string, int> AbiMajors { get; init; } = [];
    public List<AssetFile> Files { get; init; } = [];
}

internal sealed class ArchiveMetadata
{
    public string? ReleaseTag { get; init; }
    public string? Url { get; init; }
    public string? Sha256 { get; init; }
}

internal sealed class SourceMetadata
{
    public string? FfmpegCommit { get; init; }
    public string? BtbnBuildsCommit { get; init; }
    public string? SipsorceryMediaFfmpegCommit { get; init; }
    public string? FfmpegAutoGenCommit { get; init; }
}

internal sealed class ManagedComponent
{
    public string? Name { get; init; }
    public string? Version { get; init; }
    public string? License { get; init; }
}

internal sealed class AssetFile
{
    public string? Name { get; init; }
    public long Length { get; init; }
    public string? Sha256 { get; init; }
}

internal sealed class MediaGateEvidence
{
    public int SchemaVersion { get; init; } = 1;
    public string Status { get; set; } = "failed";
    public string StartedUtc { get; init; } =
        DateTime.UtcNow.ToString("O");
    public string? CompletedUtc { get; set; }
    public string GateAssemblyVersion { get; init; } =
        typeof(MediaGateEvidence).Assembly.GetName().Version?.ToString()
        ?? "unknown";
    public EnvironmentEvidence? Environment { get; set; }
    public ReceiptEvidence? Receipt { get; set; }
    public FfmpegRuntimeEvidence? Ffmpeg { get; set; }
    public MediaProbeEvidence? MediaProbe { get; set; }
    public GateError? Error { get; set; }
}

internal sealed class EnvironmentEvidence
{
    public string? OsDescription { get; init; }
    public string? FrameworkDescription { get; init; }
    public string? RuntimeVersion { get; init; }
    public int OsBuild { get; init; }
    public int Ubr { get; init; }
    public string? OsArchitecture { get; init; }
    public string? ProcessArchitecture { get; init; }
    public string? Manufacturer { get; init; }
    public string? Model { get; init; }
}

internal sealed class ReceiptEvidence
{
    public string? Path { get; init; }
    public string? Sha256 { get; init; }
    public string? Build { get; init; }
    public string? ArchiveSha256 { get; init; }
    public string? PublisherChecksumsSha256 { get; init; }
}

internal sealed class FfmpegRuntimeEvidence
{
    public string? VersionInfo { get; init; }
    public string? ConfigurationSha256 { get; init; }
    public Dictionary<string, RuntimeLibraryVersion> Libraries { get; init; } =
        [];
    public List<NativeFileEvidence> NativeFiles { get; init; } = [];
}

internal sealed class RuntimeLibraryVersion
{
    public uint Raw { get; init; }
    public int Major { get; init; }
    public int Minor { get; init; }
    public int Micro { get; init; }
    public string? Display { get; init; }
}

internal sealed class NativeFileEvidence
{
    public string? Name { get; init; }
    public long Length { get; init; }
    public string? Sha256 { get; init; }
    public string? ProductVersion { get; init; }
    public string? FileVersion { get; init; }
    public string? PeMachine { get; init; }
}

internal sealed class MediaProbeEvidence
{
    public string Encoder { get; init; } = GateConstants.EncoderName;
    public Dictionary<string, string> RequestedOptions { get; init; } = [];
    public Dictionary<string, long> AppliedOptionValues { get; init; } = [];
    public int Width { get; init; }
    public int Height { get; init; }
    public int Fps { get; init; }
    public int SubmittedFrames { get; init; }
    public int EncodedPackets { get; init; }
    public long EncodedBytes { get; init; }
    public int DecodedFrames { get; init; }
    public uint FirstDecodedWidth { get; init; }
    public uint FirstDecodedHeight { get; init; }
    public int FirstDecodedBytes { get; init; }
    public string? FirstEncodedPacketSha256 { get; init; }
    public string? FirstDecodedFrameSha256 { get; init; }
    public bool UsedSyntheticFramesOnly { get; init; }
}

internal sealed class GateError
{
    public string? Type { get; init; }
    public string? Message { get; init; }
}

internal sealed record GateArguments(
    string ReceiptPath,
    string OutputPath);

internal static class GateConstants
{
    internal const string RuntimeVersion = "10.0.10";
    internal const string CanonicalManifestResource =
        "Comote.MediaGate.ffmpeg.manifest.json";
    internal const string NativeResourcePrefix =
        "Comote.MediaGate.Native.";
    internal const string Component = "FFmpeg";
    internal const string Build =
        "n8.1.2-32-gcfa62de001-20260730";
    internal const string VersionToken =
        "8.1.2-32-gcfa62de001";
    internal const string License = "LGPL-3.0-or-later";
    internal const string Architecture = "x86_64";
    internal const string EncoderName = "h264_mf";
    internal const int WindowsBuild = 19045;
    internal const int Width = 640;
    internal const int Height = 360;
    internal const int Fps = 20;
    internal const int SubmittedFrames = 60;
    internal const long Bitrate = 6_000_000;

    internal static readonly IReadOnlyDictionary<string, int> AbiMajors =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["avcodec"] = 62,
            ["avdevice"] = 62,
            ["avfilter"] = 11,
            ["avformat"] = 62,
            ["avutil"] = 60,
            ["swresample"] = 6,
            ["swscale"] = 9,
        };

    internal static readonly IReadOnlyDictionary<string, string>
        EncoderOptions =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["rate_control"] = "cbr",
                ["scenario"] = "display_remoting",
                ["hw_encoding"] = "0",
            };

    internal static readonly IReadOnlyDictionary<string, long>
        AppliedEncoderOptionValues =
            new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["rate_control"] = 0,
                ["scenario"] = 1,
                ["hw_encoding"] = 0,
            };
}
