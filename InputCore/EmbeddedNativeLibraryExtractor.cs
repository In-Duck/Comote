using System.Reflection;
using System.Security.Cryptography;

namespace Comote.Input;

public static class EmbeddedNativeLibraryExtractor
{
    private static readonly object RetainedLibrariesLock = new();
    private static readonly Dictionary<string, FileStream> RetainedLibraries =
        new(StringComparer.OrdinalIgnoreCase);

    public static string ExtractVerifiedOrUseExplicitOverride(
        Assembly assembly,
        string cacheName,
        string resourcePrefix,
        IReadOnlyList<string> fileNames,
        string overrideEnvironmentVariable,
        bool allowExplicitOverride)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            overrideEnvironmentVariable);

        var overrideDirectory =
            Environment.GetEnvironmentVariable(
                overrideEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(overrideDirectory))
        {
            return ExtractVerified(
                assembly,
                cacheName,
                resourcePrefix,
                fileNames);
        }

        if (!allowExplicitOverride)
        {
            throw new InvalidOperationException(
                $"{overrideEnvironmentVariable} was set, but modified " +
                "native libraries were not explicitly enabled.");
        }

        return ValidateExplicitOverride(
            overrideDirectory,
            fileNames,
            overrideEnvironmentVariable);
    }
    public static string ExtractVerified(
        Assembly assembly,
        string cacheName,
        string resourcePrefix,
        IReadOnlyList<string> fileNames)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheName);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourcePrefix);
        ArgumentNullException.ThrowIfNull(fileNames);

        if (fileNames.Count == 0)
        {
            throw new ArgumentException(
                "At least one native library is required.",
                nameof(fileNames));
        }

        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException(
                "The current account has no LocalApplicationData directory.");
        }

        ValidatePathSegment(cacheName, nameof(cacheName));
        var assemblyName =
            assembly.GetName().Name ??
            throw new InvalidOperationException("The assembly has no name.");
        ValidatePathSegment(assemblyName, nameof(assembly));

        var version =
            assembly.GetName().Version?.ToString() ??
            throw new InvalidOperationException("The assembly has no version.");
        ValidatePathSegment(version, nameof(assembly));

        var cacheRoot = Path.Combine(
            localAppData,
            "Comote",
            "NativeCache",
            cacheName,
            assemblyName,
            version);
        CreateDirectoryWithoutReparsePoints(localAppData, cacheRoot);

        foreach (var fileName in fileNames)
        {
            ValidateFileName(fileName);
            ExtractOne(
                assembly,
                resourcePrefix + fileName,
                Path.Combine(cacheRoot, fileName));
        }

        return cacheRoot;
    }

    private static void ExtractOne(
        Assembly assembly,
        string resourceName,
        string targetPath)
    {
        byte[] expectedHash;
        using (var hashSource =
               assembly.GetManifestResourceStream(resourceName))
        {
            if (hashSource is null)
            {
                throw new InvalidOperationException(
                    $"Required embedded native library is missing: " +
                    resourceName);
            }

            expectedHash = SHA256.HashData(hashSource);
        }

        var temporaryPath =
            targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (File.Exists(targetPath) &&
                TryRetainVerified(targetPath, expectedHash))
            {
                return;
            }

            using (var resource =
                   assembly.GetManifestResourceStream(resourceName))
            using (var incrementalHash =
                   IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            using (var destination = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       1024 * 1024,
                       FileOptions.WriteThrough))
            {
                if (resource is null)
                {
                    throw new InvalidOperationException(
                        $"Required embedded native library disappeared: " +
                        resourceName);
                }

                var buffer = new byte[1024 * 1024];
                try
                {
                    int read;
                    while ((read = resource.Read(
                               buffer,
                               0,
                               buffer.Length)) > 0)
                    {
                        destination.Write(buffer, 0, read);
                        incrementalHash.AppendData(buffer, 0, read);
                    }

                    destination.Flush(flushToDisk: true);
                    var writtenHash = incrementalHash.GetHashAndReset();
                    try
                    {
                        if (!CryptographicOperations.FixedTimeEquals(
                                writtenHash,
                                expectedHash))
                        {
                            throw new CryptographicException(
                                $"Embedded native library changed while " +
                                $"being extracted: {resourceName}");
                        }
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(writtenHash);
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(buffer);
                }
            }

            try
            {
                File.Move(temporaryPath, targetPath, overwrite: true);
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException)
            {
                // Another Comote instance may have published an identical
                // library first and may still be holding it open.
                if (!TryRetainVerified(targetPath, expectedHash))
                {
                    throw;
                }
                return;
            }

            if (!TryRetainVerified(targetPath, expectedHash))
            {
                throw new CryptographicException(
                    $"Native library verification failed: " +
                    Path.GetFileName(targetPath));
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedHash);
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    /// <summary>
    /// Verifies an extracted library through a handle that denies writes and
    /// deletes, and keeps that handle open for the lifetime of the process.
    /// </summary>
    /// <remarks>
    /// The cache lives under LocalApplicationData, so another process running
    /// as the same user could swap a verified library between the hash check
    /// and the native load that follows. Holding the handle removes that
    /// window; the loader still maps the image because FILE_SHARE_READ
    /// permits it.
    /// </remarks>
    private static bool TryRetainVerified(
        string path,
        ReadOnlySpan<byte> expected)
    {
        var fullPath = Path.GetFullPath(path);
        lock (RetainedLibrariesLock)
        {
            if (RetainedLibraries.ContainsKey(fullPath))
            {
                // Already pinned, so the bytes cannot have changed since.
                return true;
            }
        }

        FileStream stream;
        try
        {
            stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        var retained = false;
        try
        {
            var actual = SHA256.HashData(stream);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(actual, expected))
                {
                    return false;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(actual);
            }

            lock (RetainedLibrariesLock)
            {
                retained = RetainedLibraries.TryAdd(fullPath, stream);
            }
            return true;
        }
        finally
        {
            if (!retained)
            {
                stream.Dispose();
            }
        }
    }

    private static string ValidateExplicitOverride(
        string overrideDirectory,
        IReadOnlyList<string> fileNames,
        string environmentVariable)
    {
        ArgumentNullException.ThrowIfNull(fileNames);
        if (fileNames.Count == 0)
        {
            throw new ArgumentException(
                "At least one native library is required.",
                nameof(fileNames));
        }

        if (!Path.IsPathFullyQualified(overrideDirectory))
        {
            throw new InvalidOperationException(
                $"{environmentVariable} must contain an absolute path.");
        }

        var fullPath = Path.GetFullPath(overrideDirectory);
        if (new Uri(fullPath).IsUnc)
        {
            throw new InvalidOperationException(
                $"{environmentVariable} cannot reference a network path.");
        }

        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException(
                $"{environmentVariable} has no local filesystem root.");
        }

        var current = root;
        foreach (var segment in Path.GetRelativePath(root, fullPath).Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            var directory = new DirectoryInfo(current);
            if (!directory.Exists)
            {
                throw new DirectoryNotFoundException(
                    $"Native-library override directory is missing: " +
                    current);
            }

            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    $"Native-library override path is a reparse point: " +
                    current);
            }
        }

        foreach (var fileName in fileNames)
        {
            ValidateFileName(fileName);
            var file = new FileInfo(Path.Combine(fullPath, fileName));
            if (!file.Exists || file.Length == 0)
            {
                throw new FileNotFoundException(
                    $"Required override library is missing or empty: " +
                    fileName,
                    file.FullName);
            }

            if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    $"Override library is a reparse point: " +
                    file.FullName);
            }
        }

        return fullPath;
    }
    private static void CreateDirectoryWithoutReparsePoints(
        string trustedRoot,
        string target)
    {
        var normalizedRoot =
            Path.GetFullPath(trustedRoot)
                .TrimEnd(Path.DirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        var normalizedTarget = Path.GetFullPath(target);
        if (!normalizedTarget.StartsWith(
                normalizedRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The native cache path escaped LocalApplicationData.");
        }

        var relative = Path.GetRelativePath(trustedRoot, normalizedTarget);
        var current = Path.GetFullPath(trustedRoot);
        foreach (var segment in relative.Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current))
            {
                if ((File.GetAttributes(current) &
                     FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException(
                        $"Native cache path is a reparse point: {current}");
                }
            }
            else
            {
                Directory.CreateDirectory(current);
            }
        }
    }

    private static void ValidateFileName(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (!string.Equals(
                Path.GetFileName(fileName),
                fileName,
                StringComparison.Ordinal) ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException(
                $"Invalid native library file name: {fileName}",
                nameof(fileName));
        }
    }

    private static void ValidatePathSegment(string segment, string paramName)
    {
        if (!string.Equals(
                Path.GetFileName(segment),
                segment,
                StringComparison.Ordinal) ||
            segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException(
                "The value must be one safe path segment.",
                paramName);
        }
    }
}
