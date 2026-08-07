using System.Text.Json;

namespace Comote.MediaGate;

internal static class Program
{
    private const string AcknowledgeArgument =
        "--acknowledge-release-vm";

    private static int Main(string[] args)
    {
        if (args.Contains(
                "--internal-worker",
                StringComparer.Ordinal))
        {
            return RunWorker(args);
        }

        return RunController(args);
    }

    private static int RunController(string[] args)
    {
        string? outputPath = null;
        var evidence = new MediaGateEvidence();
        try
        {
            GateArguments arguments =
                ParseControllerArguments(args);
            outputPath = arguments.OutputPath;
            evidence.Environment =
                GateEnvironment.Validate();

            var receipt = ReceiptValidator.Validate(
                arguments.ReceiptPath);
            evidence.Receipt = receipt.Evidence;

            var probe = MediaProbeController.Run();
            evidence.Ffmpeg = probe.Ffmpeg;
            evidence.MediaProbe = probe.MediaProbe;
            evidence.Status = "passed";
            evidence.CompletedUtc =
                DateTime.UtcNow.ToString("O");
            WriteEvidence(outputPath, evidence);

            Console.WriteLine(
                "Comote MediaGate passed.");
            Console.WriteLine(
                $"Evidence: {outputPath}");
            return 0;
        }
        catch (Exception exception)
        {
            evidence.Status = "failed";
            evidence.CompletedUtc =
                DateTime.UtcNow.ToString("O");
            evidence.Error = new GateError
            {
                Type = exception.GetType().FullName,
                Message = exception.Message,
            };
            if (outputPath is not null)
            {
                try
                {
                    WriteEvidence(outputPath, evidence);
                    Console.Error.WriteLine(
                        $"Failure evidence: {outputPath}");
                }
                catch (Exception writeException)
                {
                    Console.Error.WriteLine(
                        $"Evidence write failed: " +
                        $"{writeException.GetType().Name}: " +
                        $"{writeException.Message}");
                }
            }

            Console.Error.WriteLine(
                $"Comote MediaGate failed: " +
                $"{exception.GetType().Name}: " +
                $"{exception.Message}");
            return 1;
        }
    }

    private static int RunWorker(string[] args)
    {
        try
        {
            WorkerArguments arguments =
                ParseWorkerArguments(args);
            return MediaProbeController.RunWorker(arguments);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"Comote MediaGate worker failed: " +
                $"{exception.GetType().Name}: " +
                $"{exception.Message}");
            return 1;
        }
    }

    private static GateArguments ParseControllerArguments(
        IReadOnlyList<string> args)
    {
        bool acknowledged = false;
        string? receiptPath = null;
        string? outputPath = null;

        for (int index = 0; index < args.Count; index++)
        {
            string argument = args[index];
            switch (argument)
            {
                case AcknowledgeArgument:
                    if (acknowledged)
                    {
                        throw new ArgumentException(
                            "Release VM acknowledgement was repeated.");
                    }
                    acknowledged = true;
                    break;

                case "--receipt":
                    receiptPath = ReadUniqueValue(
                        args,
                        ref index,
                        receiptPath,
                        "--receipt");
                    break;

                case "--output":
                    outputPath = ReadUniqueValue(
                        args,
                        ref index,
                        outputPath,
                        "--output");
                    break;

                default:
                    throw new ArgumentException(
                        $"Unknown MediaGate argument: {argument}.");
            }
        }

        if (!acknowledged)
        {
            throw new ArgumentException(
                $"{AcknowledgeArgument} is required.");
        }
        string receipt = GatePaths.RequireAbsoluteLocalFile(
            receiptPath
            ?? throw new ArgumentException(
                "--receipt is required."),
            "FFmpeg receipt");
        string output = GatePaths.RequireNewOutputFile(
            outputPath
            ?? throw new ArgumentException(
                "--output is required."));
        if (string.Equals(
                receipt,
                output,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Receipt and evidence paths must differ.");
        }

        return new GateArguments(receipt, output);
    }

    private static WorkerArguments ParseWorkerArguments(
        IReadOnlyList<string> args)
    {
        bool internalWorker = false;
        string? nativeDirectory = null;
        string? resultPath = null;

        for (int index = 0; index < args.Count; index++)
        {
            string argument = args[index];
            switch (argument)
            {
                case "--internal-worker":
                    if (internalWorker)
                    {
                        throw new ArgumentException(
                            "Internal worker argument was repeated.");
                    }
                    internalWorker = true;
                    break;

                case "--native-dir":
                    nativeDirectory = ReadUniqueValue(
                        args,
                        ref index,
                        nativeDirectory,
                        "--native-dir");
                    break;

                case "--result":
                    resultPath = ReadUniqueValue(
                        args,
                        ref index,
                        resultPath,
                        "--result");
                    break;

                default:
                    throw new ArgumentException(
                        $"Unknown worker argument: {argument}.");
            }
        }

        if (!internalWorker ||
            nativeDirectory is null ||
            resultPath is null)
        {
            throw new ArgumentException(
                "Internal worker arguments are incomplete.");
        }

        return new WorkerArguments(
            nativeDirectory,
            resultPath);
    }

    private static string ReadUniqueValue(
        IReadOnlyList<string> args,
        ref int index,
        string? currentValue,
        string argumentName)
    {
        if (currentValue is not null)
        {
            throw new ArgumentException(
                $"{argumentName} was repeated.");
        }
        if (index + 1 >= args.Count ||
            string.IsNullOrWhiteSpace(args[index + 1]))
        {
            throw new ArgumentException(
                $"{argumentName} requires a value.");
        }

        index++;
        return args[index];
    }

    private static void WriteEvidence(
        string outputPath,
        MediaGateEvidence evidence)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            evidence,
            StrictJson.SerializerOptions);
        using var output = new FileStream(
            outputPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        output.Write(json);
        output.Flush(flushToDisk: true);
    }
}
