using System.Reflection;
using System.Security.Cryptography;

namespace Comote.MediaGate;

internal sealed class NativeWorkspace : IDisposable
{
    private const string RootDirectoryName = "Comote-MediaGate";
    private const string ChallengeFileName = "worker-challenge.txt";
    internal const string WorkerResultFileName = "worker-result.json";
    private readonly HashSet<string> _allowedFileNames;
    private bool _disposed;

    private NativeWorkspace(
        string rootDirectory,
        string directory,
        string challenge)
    {
        RootDirectory = rootDirectory;
        Directory = directory;
        Challenge = challenge;
        ResultPath = Path.Combine(
            directory,
            WorkerResultFileName);
        _allowedFileNames = new HashSet<string>(
            ExpectedRelease.NativeAssets.Keys,
            StringComparer.Ordinal)
        {
            ChallengeFileName,
            WorkerResultFileName,
        };
    }

    internal string RootDirectory { get; }
    internal string Directory { get; }
    internal string Challenge { get; }
    internal string ResultPath { get; }

    internal static NativeWorkspace CreateAndExtract()
    {
        string root = Path.GetFullPath(
            Path.Combine(
                Path.GetTempPath(),
                RootDirectoryName));
        if (System.IO.Directory.Exists(root))
        {
            RejectReparsePoint(root, "MediaGate temp root");
            DeleteEmptyStaleDirectories(root);
        }
        else
        {
            System.IO.Directory.CreateDirectory(root);
            RejectReparsePoint(root, "MediaGate temp root");
        }

        string directory = Path.Combine(
            root,
            Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        RejectReparsePoint(directory, "MediaGate workspace");

        string challenge = Convert.ToBase64String(
            RandomNumberGenerator.GetBytes(32));
        var workspace = new NativeWorkspace(
            root,
            directory,
            challenge);
        try
        {
            workspace.ExtractNativeResources();
            string challengePath = Path.Combine(
                directory,
                ChallengeFileName);
            using (var stream = new FileStream(
                       challengePath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            using (var writer = new StreamWriter(
                       stream,
                       new System.Text.UTF8Encoding(false)))
            {
                writer.Write(challenge);
            }

            return workspace;
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    internal static WorkerArguments ValidateWorkerWorkspace(
        string nativeDirectory,
        string resultPath,
        string challenge)
    {
        string root = Path.GetFullPath(
            Path.Combine(
                Path.GetTempPath(),
                RootDirectoryName));
        string directory = GatePaths.RequireAbsoluteLocalPath(
            nativeDirectory,
            "Worker native directory");
        string result = GatePaths.RequireAbsoluteLocalPath(
            resultPath,
            "Worker result");
        string expectedPrefix =
            root.TrimEnd(Path.DirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        if (!directory.StartsWith(
                expectedPrefix,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                Path.GetDirectoryName(result),
                directory,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                Path.GetFileName(result),
                WorkerResultFileName,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Worker workspace path is outside the MediaGate temp root.");
        }

        RejectReparsePoint(root, "MediaGate temp root");
        RejectReparsePoint(directory, "MediaGate workspace");
        string challengePath = Path.Combine(
            directory,
            ChallengeFileName);
        GatePaths.RequireAbsoluteLocalFile(
            challengePath,
            "Worker challenge");
        string storedChallenge =
            File.ReadAllText(challengePath).Trim();
        if (!FixedTimeEquals(storedChallenge, challenge))
        {
            throw new UnauthorizedAccessException(
                "Worker challenge did not match.");
        }
        if (File.Exists(result) ||
            System.IO.Directory.Exists(result))
        {
            throw new IOException(
                "Worker result path already exists.");
        }

        ValidateExtractedFiles(directory);
        return new WorkerArguments(directory, result);
    }

    private void ExtractNativeResources()
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        var nativeResources = assembly
            .GetManifestResourceNames()
            .Where(
                name => name.StartsWith(
                    GateConstants.NativeResourcePrefix,
                    StringComparison.Ordinal))
            .ToArray();
        var expectedResources = ExpectedRelease.NativeAssets.Keys
            .Select(
                name => GateConstants.NativeResourcePrefix + name)
            .ToHashSet(StringComparer.Ordinal);
        if (!nativeResources.ToHashSet(
                StringComparer.Ordinal).SetEquals(expectedResources))
        {
            throw new InvalidDataException(
                "Embedded native resource inventory is invalid.");
        }

        foreach (ExpectedNativeAsset expected in
                 ExpectedRelease.NativeAssets.Values)
        {
            string resourceName =
                GateConstants.NativeResourcePrefix + expected.Name;
            string destination = Path.Combine(
                Directory,
                expected.Name);
            using Stream resource =
                assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidDataException(
                    $"Embedded native resource is missing: " +
                    $"{expected.Name}.");
            using (var output = new FileStream(
                       destination,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                resource.CopyTo(output);
                output.Flush(flushToDisk: true);
            }

            var info = new FileInfo(destination);
            if (info.Length != expected.Length ||
                !string.Equals(
                    GateHash.Sha256File(destination),
                    expected.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Embedded native resource failed integrity: " +
                    $"{expected.Name}.");
            }
        }
    }

    private static void ValidateExtractedFiles(string directory)
    {
        var actualFiles = System.IO.Directory
            .EnumerateFiles(
                directory,
                "*",
                SearchOption.TopDirectoryOnly)
            .Where(
                path => !string.Equals(
                    Path.GetFileName(path),
                    ChallengeFileName,
                    StringComparison.Ordinal))
            .Select(
                path => Path.GetFileName(path)
                    ?? throw new InvalidDataException(
                        "Worker native file name is missing."))
            .ToHashSet(StringComparer.Ordinal);
        if (!actualFiles.SetEquals(
                ExpectedRelease.NativeAssets.Keys))
        {
            throw new InvalidDataException(
                "Worker native file inventory is invalid.");
        }

        foreach (ExpectedNativeAsset expected in
                 ExpectedRelease.NativeAssets.Values)
        {
            string path = Path.Combine(directory, expected.Name);
            GatePaths.RequireAbsoluteLocalFile(
                path,
                $"Worker native file {expected.Name}");
            var info = new FileInfo(path);
            if (info.Length != expected.Length ||
                !string.Equals(
                    GateHash.Sha256File(path),
                    expected.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Worker native file failed integrity: " +
                    $"{expected.Name}.");
            }
        }
    }

    private static void DeleteEmptyStaleDirectories(string root)
    {
        foreach (string directory in System.IO.Directory
                     .EnumerateDirectories(
                         root,
                         "*",
                         SearchOption.TopDirectoryOnly))
        {
            RejectReparsePoint(
                directory,
                "Stale MediaGate workspace");
            if (!System.IO.Directory.EnumerateFileSystemEntries(
                    directory).Any())
            {
                System.IO.Directory.Delete(
                    directory,
                    recursive: false);
            }
        }
    }

    private static void RejectReparsePoint(
        string path,
        string description)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"{description} cannot be a reparse point.");
        }
    }

    private static bool FixedTimeEquals(
        string left,
        string right)
    {
        byte[] leftBytes =
            System.Text.Encoding.UTF8.GetBytes(left);
        byte[] rightBytes =
            System.Text.Encoding.UTF8.GetBytes(right);
        try
        {
            return leftBytes.Length == rightBytes.Length &&
                   CryptographicOperations.FixedTimeEquals(
                       leftBytes,
                       rightBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(leftBytes);
            CryptographicOperations.ZeroMemory(rightBytes);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        if (!System.IO.Directory.Exists(Directory))
        {
            return;
        }
        RejectReparsePoint(Directory, "MediaGate workspace");

        foreach (string entry in System.IO.Directory
                     .EnumerateFileSystemEntries(
                         Directory,
                         "*",
                         SearchOption.TopDirectoryOnly))
        {
            if (System.IO.Directory.Exists(entry) ||
                (File.GetAttributes(entry) &
                 FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException(
                    "Unexpected entry prevented MediaGate cleanup.");
            }

            string name = Path.GetFileName(entry);
            if (!_allowedFileNames.Contains(name))
            {
                throw new IOException(
                    "Unexpected file prevented MediaGate cleanup.");
            }
            File.Delete(entry);
        }

        System.IO.Directory.Delete(
            Directory,
            recursive: false);
        if (!System.IO.Directory.EnumerateFileSystemEntries(
                RootDirectory).Any())
        {
            System.IO.Directory.Delete(
                RootDirectory,
                recursive: false);
        }
    }
}
