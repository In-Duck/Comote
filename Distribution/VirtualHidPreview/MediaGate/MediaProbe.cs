using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using FFmpeg.AutoGen;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.FFmpeg;

namespace Comote.MediaGate;

internal static class MediaProbeController
{
    private const string WorkerChallengeEnvironmentVariable =
        "COMOTE_MEDIA_GATE_WORKER_CHALLENGE";
    private static readonly TimeSpan WorkerTimeout =
        TimeSpan.FromMinutes(3);

    internal static (
        FfmpegRuntimeEvidence Ffmpeg,
        MediaProbeEvidence MediaProbe) Run()
    {
        using var workspace =
            NativeWorkspace.CreateAndExtract();
        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException(
                "MediaGate process path is unavailable.");
        string executableName =
            Path.GetFileNameWithoutExtension(executable);
        if (!string.Equals(
                executableName,
                "Comote.MediaGate",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "MediaGate must be run from its published executable.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workspace.Directory,
        };
        startInfo.ArgumentList.Add("--internal-worker");
        startInfo.ArgumentList.Add("--native-dir");
        startInfo.ArgumentList.Add(workspace.Directory);
        startInfo.ArgumentList.Add("--result");
        startInfo.ArgumentList.Add(workspace.ResultPath);
        startInfo.Environment[
            WorkerChallengeEnvironmentVariable] =
                workspace.Challenge;

        using var worker = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "MediaGate worker could not be started.");
        if (!worker.WaitForExit(
                checked((int)WorkerTimeout.TotalMilliseconds)))
        {
            worker.Kill(entireProcessTree: true);
            worker.WaitForExit();
            throw new TimeoutException(
                "MediaGate worker exceeded its time limit.");
        }
        if (worker.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"MediaGate worker failed with exit code " +
                $"{worker.ExitCode}.");
        }

        var result =
            StrictJson.ReadFile<MediaProbeWorkerResult>(
                workspace.ResultPath);
        string expectedChallengeHash =
            GateHash.Sha256Bytes(
                Encoding.UTF8.GetBytes(workspace.Challenge));
        if (!string.Equals(
                result.ChallengeSha256,
                expectedChallengeHash,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "MediaGate worker challenge evidence is invalid.");
        }

        return (
            result.Ffmpeg
            ?? throw new InvalidDataException(
                "MediaGate worker omitted FFmpeg evidence."),
            result.MediaProbe
            ?? throw new InvalidDataException(
                "MediaGate worker omitted media evidence."));
    }

    internal static int RunWorker(WorkerArguments arguments)
    {
        string challenge =
            Environment.GetEnvironmentVariable(
                WorkerChallengeEnvironmentVariable)
            ?? throw new UnauthorizedAccessException(
                "MediaGate worker challenge is missing.");
        arguments = NativeWorkspace.ValidateWorkerWorkspace(
            arguments.NativeDirectory,
            arguments.ResultPath,
            challenge);
        _ = GateEnvironment.Validate();

        var (ffmpegEvidence, mediaEvidence) =
            NativeMediaProbe.Run(arguments.NativeDirectory);
        var result = new MediaProbeWorkerResult
        {
            ChallengeSha256 =
                GateHash.Sha256Bytes(
                    Encoding.UTF8.GetBytes(challenge)),
            Ffmpeg = ffmpegEvidence,
            MediaProbe = mediaEvidence,
        };
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            result,
            StrictJson.SerializerOptions);
        using var output = new FileStream(
            arguments.ResultPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        output.Write(json);
        output.Flush(flushToDisk: true);
        return 0;
    }
}

internal static class NativeMediaProbe
{
    internal static unsafe (
        FfmpegRuntimeEvidence Ffmpeg,
        MediaProbeEvidence MediaProbe) Run(
            string nativeDirectory)
    {
        var nativeEvidence =
            InspectNativeFiles(nativeDirectory);
        FFmpegInit.Initialise(
            FfmpegLogLevelEnum.AV_LOG_WARNING,
            nativeDirectory);

        string versionInfo = ffmpeg.av_version_info()
            ?? throw new InvalidDataException(
                "FFmpeg version info is unavailable.");
        if (!versionInfo.Contains(
                GateConstants.VersionToken,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Loaded FFmpeg version is invalid: {versionInfo}.");
        }

        string configuration =
            ffmpeg.avcodec_configuration()
            ?? throw new InvalidDataException(
                "FFmpeg configuration is unavailable.");
        if (!configuration.Contains(
                "--disable-gpl",
                StringComparison.Ordinal) ||
            configuration.Contains(
                "--enable-gpl",
                StringComparison.Ordinal) ||
            !configuration.Contains(
                "--disable-libx264",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Loaded FFmpeg configuration crossed the LGPL boundary.");
        }

        var runtimeVersions = ReadRuntimeVersions();
        ValidateRuntimeAbi(runtimeVersions);
        if (ffmpeg.avcodec_find_encoder_by_name(
                GateConstants.EncoderName) == null)
        {
            throw new InvalidDataException(
                "The h264_mf encoder is unavailable.");
        }

        var mediaEvidence = RunEncodeDecodeProbe();
        return (
            new FfmpegRuntimeEvidence
            {
                VersionInfo = versionInfo,
                ConfigurationSha256 =
                    GateHash.Sha256Bytes(
                        Encoding.UTF8.GetBytes(configuration)),
                Libraries = runtimeVersions,
                NativeFiles = nativeEvidence,
            },
            mediaEvidence);
    }

    private static unsafe MediaProbeEvidence RunEncodeDecodeProbe()
    {
        var options = new Dictionary<string, string>(
            GateConstants.EncoderOptions,
            StringComparer.Ordinal);
        using var encoder = new FFmpegVideoEncoder(options);
        if (!encoder.SetCodec(
                AVCodecID.AV_CODEC_ID_H264,
                GateConstants.EncoderName))
        {
            throw new InvalidDataException(
                "The h264_mf encoder could not be selected.");
        }
        encoder.SetBitrate(
            GateConstants.Bitrate,
            null,
            null,
            null);
        encoder.InitialiseEncoder(
            AVCodecID.AV_CODEC_ID_H264,
            GateConstants.Width,
            GateConstants.Height,
            GateConstants.Fps);

        Dictionary<string, long> appliedOptions =
            ReadAppliedEncoderOptions(encoder);
        ValidateAppliedEncoderOptions(appliedOptions);

        using var decoder = new FFmpegVideoEncoder();
        int encodedPackets = 0;
        long encodedBytes = 0;
        int decodedFrames = 0;
        byte[]? firstEncodedPacket = null;
        byte[]? firstDecodedFrame = null;
        uint firstDecodedWidth = 0;
        uint firstDecodedHeight = 0;

        for (int frameIndex = 0;
             frameIndex < GateConstants.SubmittedFrames;
             frameIndex++)
        {
            byte[] frame = CreateSyntheticBgraFrame(frameIndex);
            if (frameIndex is 0 or 30)
            {
                encoder.ForceKeyFrame();
            }

            byte[]? packet = encoder.EncodeVideo(
                GateConstants.Width,
                GateConstants.Height,
                frame,
                VideoPixelFormatsEnum.Bgra,
                VideoCodecsEnum.H264);
            if (packet is not { Length: > 0 })
            {
                continue;
            }

            encodedPackets++;
            encodedBytes += packet.Length;
            firstEncodedPacket ??= packet.ToArray();

            foreach (VideoSample decoded in
                     decoder.DecodeVideo(
                         packet,
                         VideoPixelFormatsEnum.Bgra,
                         VideoCodecsEnum.H264))
            {
                byte[] sample = decoded.Sample
                    ?? throw new InvalidDataException(
                        "Decoded frame sample is null.");
                if (decoded.Width != GateConstants.Width ||
                    decoded.Height != GateConstants.Height ||
                    sample.Length <
                        GateConstants.Width *
                        GateConstants.Height * 4)
                {
                    throw new InvalidDataException(
                        "Decoded frame dimensions or buffer size are invalid.");
                }

                decodedFrames++;
                if (firstDecodedFrame is null)
                {
                    firstDecodedFrame = sample.ToArray();
                    firstDecodedWidth = decoded.Width;
                    firstDecodedHeight = decoded.Height;
                }
            }
        }

        if (encodedPackets == 0 ||
            encodedBytes == 0 ||
            firstEncodedPacket is null ||
            decodedFrames == 0 ||
            firstDecodedFrame is null)
        {
            throw new InvalidDataException(
                "Synthetic H.264 encode/decode produced no usable output.");
        }

        return new MediaProbeEvidence
        {
            RequestedOptions = options,
            AppliedOptionValues = appliedOptions,
            Width = GateConstants.Width,
            Height = GateConstants.Height,
            Fps = GateConstants.Fps,
            SubmittedFrames = GateConstants.SubmittedFrames,
            EncodedPackets = encodedPackets,
            EncodedBytes = encodedBytes,
            DecodedFrames = decodedFrames,
            FirstDecodedWidth = firstDecodedWidth,
            FirstDecodedHeight = firstDecodedHeight,
            FirstDecodedBytes = firstDecodedFrame.Length,
            FirstEncodedPacketSha256 =
                GateHash.Sha256Bytes(firstEncodedPacket),
            FirstDecodedFrameSha256 =
                GateHash.Sha256Bytes(firstDecodedFrame),
            UsedSyntheticFramesOnly = true,
        };
    }

    private static byte[] CreateSyntheticBgraFrame(int frameIndex)
    {
        var frame = new byte[
            GateConstants.Width *
            GateConstants.Height * 4];
        int offset = 0;
        for (int y = 0; y < GateConstants.Height; y++)
        {
            for (int x = 0; x < GateConstants.Width; x++)
            {
                int checker =
                    ((x / 32) + (y / 24) + frameIndex) & 1;
                frame[offset++] = checked((byte)(
                    (x + frameIndex * 3) & 0xFF));
                frame[offset++] = checked((byte)(
                    (y * 2 + frameIndex * 5) & 0xFF));
                frame[offset++] = checker == 0
                    ? checked((byte)(
                        (x + y + frameIndex * 7) & 0xFF))
                    : checked((byte)0xE0);
                frame[offset++] = 0xFF;
            }
        }

        return frame;
    }

    private static unsafe Dictionary<string, long>
        ReadAppliedEncoderOptions(FFmpegVideoEncoder encoder)
    {
        FieldInfo contextField =
            typeof(FFmpegVideoEncoder).GetField(
                "_encoderContext",
                BindingFlags.Instance |
                BindingFlags.NonPublic)
            ?? throw new MissingFieldException(
                typeof(FFmpegVideoEncoder).FullName,
                "_encoderContext");
        object boxedPointer =
            contextField.GetValue(encoder)
            ?? throw new InvalidDataException(
                "Encoder context pointer is null.");
        void* rawPointer = Pointer.Unbox(boxedPointer);
        var context = (AVCodecContext*)rawPointer;
        if (context == null ||
            context->priv_data == null)
        {
            throw new InvalidDataException(
                "Encoder private options are unavailable.");
        }

        var values = new Dictionary<string, long>(
            StringComparer.Ordinal);
        foreach (string name in
                 GateConstants.AppliedEncoderOptionValues.Keys)
        {
            long value = 0;
            int result = ffmpeg.av_opt_get_int(
                context->priv_data,
                name,
                1,
                &value);
            if (result < 0)
            {
                throw new InvalidDataException(
                    $"Encoder option could not be read: {name}.");
            }
            values.Add(name, value);
        }

        return values;
    }

    private static void ValidateAppliedEncoderOptions(
        IReadOnlyDictionary<string, long> actual)
    {
        if (actual.Count !=
            GateConstants.AppliedEncoderOptionValues.Count)
        {
            throw new InvalidDataException(
                "Applied encoder option count is invalid.");
        }
        foreach (var expected in
                 GateConstants.AppliedEncoderOptionValues)
        {
            if (!actual.TryGetValue(
                    expected.Key,
                    out long value) ||
                value != expected.Value)
            {
                throw new InvalidDataException(
                    $"Applied encoder option is invalid: " +
                    $"{expected.Key}.");
            }
        }
    }

    private static Dictionary<string, RuntimeLibraryVersion>
        ReadRuntimeVersions() =>
            new(StringComparer.Ordinal)
            {
                ["avcodec"] = ToVersion(
                    ffmpeg.avcodec_version()),
                ["avdevice"] = ToVersion(
                    ffmpeg.avdevice_version()),
                ["avfilter"] = ToVersion(
                    ffmpeg.avfilter_version()),
                ["avformat"] = ToVersion(
                    ffmpeg.avformat_version()),
                ["avutil"] = ToVersion(
                    ffmpeg.avutil_version()),
                ["swresample"] = ToVersion(
                    ffmpeg.swresample_version()),
                ["swscale"] = ToVersion(
                    ffmpeg.swscale_version()),
            };

    private static RuntimeLibraryVersion ToVersion(uint raw)
    {
        int major = checked((int)(raw >> 16));
        int minor = checked((int)((raw >> 8) & 0xFF));
        int micro = checked((int)(raw & 0xFF));
        return new RuntimeLibraryVersion
        {
            Raw = raw,
            Major = major,
            Minor = minor,
            Micro = micro,
            Display = $"{major}.{minor}.{micro}",
        };
    }

    private static void ValidateRuntimeAbi(
        IReadOnlyDictionary<string, RuntimeLibraryVersion> versions)
    {
        if (versions.Count != GateConstants.AbiMajors.Count)
        {
            throw new InvalidDataException(
                "Loaded FFmpeg ABI inventory is invalid.");
        }
        foreach (var expected in GateConstants.AbiMajors)
        {
            if (!versions.TryGetValue(
                    expected.Key,
                    out var actual) ||
                actual.Major != expected.Value)
            {
                throw new InvalidDataException(
                    $"Loaded FFmpeg ABI is invalid: " +
                    $"{expected.Key}.");
            }
        }
    }

    private static List<NativeFileEvidence> InspectNativeFiles(
        string directory)
    {
        var evidence = new List<NativeFileEvidence>();
        foreach (ExpectedNativeAsset expected in
                 ExpectedRelease.NativeAssets.Values
                     .OrderBy(asset => asset.Name, StringComparer.Ordinal))
        {
            string path = Path.Combine(directory, expected.Name);
            var info = new FileInfo(path);
            string hash = GateHash.Sha256File(path);
            if (info.Length != expected.Length ||
                !string.Equals(
                    hash,
                    expected.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Native FFmpeg file changed before load: " +
                    $"{expected.Name}.");
            }

            string peMachine = ReadPeMachine(path);
            if (!string.Equals(
                    peMachine,
                    "AMD64",
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Native FFmpeg file is not x64: " +
                    $"{expected.Name}.");
            }
            FileVersionInfo versionInfo =
                FileVersionInfo.GetVersionInfo(path);
            if (versionInfo.ProductVersion is null ||
                !versionInfo.ProductVersion.Contains(
                    GateConstants.VersionToken,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Native FFmpeg product version is invalid: " +
                    $"{expected.Name}.");
            }

            evidence.Add(new NativeFileEvidence
            {
                Name = expected.Name,
                Length = info.Length,
                Sha256 = hash,
                ProductVersion = versionInfo.ProductVersion,
                FileVersion = versionInfo.FileVersion,
                PeMachine = peMachine,
            });
        }

        return evidence;
    }

    private static string ReadPeMachine(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        using var reader = new BinaryReader(stream);
        if (reader.ReadUInt16() != 0x5A4D)
        {
            throw new InvalidDataException(
                "Native FFmpeg file is not a PE image.");
        }
        stream.Position = 0x3C;
        int peOffset = reader.ReadInt32();
        if (peOffset < 0x40 ||
            peOffset > stream.Length - 6)
        {
            throw new InvalidDataException(
                "Native FFmpeg PE offset is invalid.");
        }
        stream.Position = peOffset;
        if (reader.ReadUInt32() != 0x00004550)
        {
            throw new InvalidDataException(
                "Native FFmpeg PE signature is invalid.");
        }

        return reader.ReadUInt16() switch
        {
            0x8664 => "AMD64",
            0x014C => "I386",
            0xAA64 => "ARM64",
            ushort machine => $"0x{machine:X4}",
        };
    }
}
