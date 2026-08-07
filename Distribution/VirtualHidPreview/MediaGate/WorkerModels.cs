namespace Comote.MediaGate;

internal sealed class MediaProbeWorkerResult
{
    public int SchemaVersion { get; init; } = 1;
    public string? ChallengeSha256 { get; init; }
    public FfmpegRuntimeEvidence? Ffmpeg { get; init; }
    public MediaProbeEvidence? MediaProbe { get; init; }
}

internal sealed record WorkerArguments(
    string NativeDirectory,
    string ResultPath);
