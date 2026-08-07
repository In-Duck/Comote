using System.Reflection;
using Comote.Input;

namespace Host;

public static class FFmpegExtractor
{
    private static readonly string[] DllNames =
    [
        "avcodec-62.dll",
        "avdevice-62.dll",
        "avfilter-11.dll",
        "avformat-62.dll",
        "avutil-60.dll",
        "swresample-6.dll",
        "swscale-9.dll"
    ];

    public static string ExtractFFmpeg()
    {
        return EmbeddedNativeLibraryExtractor
            .ExtractVerifiedOrUseExplicitOverride(
                Assembly.GetExecutingAssembly(),
                "FFmpeg-8.1-LGPL",
                "ffmpeg.",
                DllNames,
                "COMOTE_FFMPEG_DIR",
                Environment.GetCommandLineArgs().Any(
                    argument => string.Equals(
                        argument,
                        "--allow-modified-ffmpeg",
                        StringComparison.Ordinal)));
    }
}