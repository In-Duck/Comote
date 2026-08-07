namespace Comote.MediaGate;

internal sealed record ExpectedNativeAsset(
    string Name,
    long Length,
    string Sha256);

internal static class ExpectedRelease
{
    internal const string ReleaseTag =
        "autobuild-2026-07-30-13-32";
    internal const string ArchiveUrl =
        "https://github.com/BtbN/FFmpeg-Builds/releases/download/" +
        "autobuild-2026-07-30-13-32/" +
        "ffmpeg-n8.1.2-32-gcfa62de001-win64-lgpl-shared-8.1.zip";
    internal const string ArchiveSha256 =
        "23429F940316EA92E376F6946C0A1F1B9043C930F3BC068228461D65AE24F8B8";
    internal const string PublisherChecksumsUrl =
        "https://github.com/BtbN/FFmpeg-Builds/releases/download/" +
        "autobuild-2026-07-30-13-32/checksums.sha256";
    internal const string PublisherChecksumsSha256 =
        "8E62B0A71BAF8E70CF9BE34AD805AAECE3A7695BC0353959F1A3B915A5A49A24";
    internal const string FfmpegCommit =
        "cfa62de001af8ffeb7e22561f246469c7b809951";
    internal const string BtbnBuildsCommit =
        "a99e8230eae00d1cee38f23076a7a1f55cd984e2";
    internal const string SipsorceryMediaFfmpegCommit =
        "dfbc856767a19108fa55bdd59e6bdb7005ca9961";
    internal const string FfmpegAutoGenCommit =
        "444925cd53d3611fd4c8c295873fb631be56ab21";

    internal static readonly IReadOnlyDictionary<string, ExpectedNativeAsset>
        NativeAssets =
            new Dictionary<string, ExpectedNativeAsset>(
                StringComparer.Ordinal)
            {
                ["avcodec-62.dll"] = new(
                    "avcodec-62.dll",
                    70_876_672,
                    "8398209557DC8557407EA6ABD6E05E4B253996F4E155CCCDD4DFCD1CEDBEAB17"),
                ["avdevice-62.dll"] = new(
                    "avdevice-62.dll",
                    3_699_712,
                    "9C12B37B517775721E72B6A4E34B24E5E3FE98750E9DB3D72C9A985DBCA535FE"),
                ["avfilter-11.dll"] = new(
                    "avfilter-11.dll",
                    29_817_856,
                    "52E212E24435396687E1C00099DFDD5229D2552BE77A3E34BD61A90D16D51406"),
                ["avformat-62.dll"] = new(
                    "avformat-62.dll",
                    22_070_784,
                    "C8A8E1CE726F21F701A61481BC3D8DBDF6F020F064291CBC333E906A10D7B4FA"),
                ["avutil-60.dll"] = new(
                    "avutil-60.dll",
                    2_937_856,
                    "F4BA434931C38D60671CD905F485DE1CB415BFC2386C286D5578A7597C1C8187"),
                ["swresample-6.dll"] = new(
                    "swresample-6.dll",
                    723_968,
                    "BEE2D7A942A67D9EBF668D8B565C2F613E5D5E3E609E00ED1C1E90EBBD6C8612"),
                ["swscale-9.dll"] = new(
                    "swscale-9.dll",
                    12_570_624,
                    "5C05FA6055B6626C21C7F7B2897EF140A91BDE898E51F86D6923282138980E6A"),
            };
}
