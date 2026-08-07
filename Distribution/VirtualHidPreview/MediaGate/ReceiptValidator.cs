using System.Reflection;
using System.Security.Cryptography;

namespace Comote.MediaGate;

internal static class ReceiptValidator
{
    internal static (
        FfmpegReceipt Canonical,
        FfmpegReceipt External,
        ReceiptEvidence Evidence) Validate(string receiptPath)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var canonical = StrictJson.ReadResource<FfmpegReceipt>(
            assembly,
            GateConstants.CanonicalManifestResource);
        var external =
            StrictJson.ReadFile<FfmpegReceipt>(receiptPath);

        ValidateCanonical(canonical);
        ValidateExternal(external);

        return (
            canonical,
            external,
            new ReceiptEvidence
            {
                Path = receiptPath,
                Sha256 = GateHash.Sha256File(receiptPath),
                Build = external.Build,
                ArchiveSha256 = external.Archive.Sha256,
                PublisherChecksumsSha256 =
                    external.PublisherChecksums.Sha256,
            });
    }

    private static void ValidateCanonical(FfmpegReceipt receipt)
    {
        ValidateCommonIdentity(receipt, allowMissingComponent: true);
        ValidateReleaseMetadata(receipt);
        ValidateAbiMajors(receipt.AbiMajors);
        ValidateEncoderOptions(receipt.EncoderOptions);
        ValidateManagedComponents(receipt.ManagedComponents);
        ValidateNativeFileInventory(receipt.Files);
    }

    private static void ValidateExternal(FfmpegReceipt receipt)
    {
        ValidateCommonIdentity(receipt, allowMissingComponent: false);
        ValidateReleaseMetadata(receipt);
        ValidateAbiMajors(receipt.AbiMajors);
        ValidateEncoderOptions(receipt.EncoderOptions);
        ValidateManagedComponents(receipt.ManagedComponents);
        ValidateNativeFileInventory(receipt.Files);
    }

    private static void ValidateCommonIdentity(
        FfmpegReceipt receipt,
        bool allowMissingComponent)
    {
        if (receipt.SchemaVersion != 2)
        {
            throw new InvalidDataException(
                "FFmpeg receipt schemaVersion must be 2.");
        }
        if (!allowMissingComponent &&
            !string.Equals(
                receipt.Component,
                GateConstants.Component,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "FFmpeg receipt component is invalid.");
        }
        if (allowMissingComponent &&
            receipt.Component is not null)
        {
            throw new InvalidDataException(
                "Canonical FFmpeg manifest must not set component.");
        }
        RequireEqual(
            receipt.Build,
            GateConstants.Build,
            "build");
        RequireEqual(
            receipt.License,
            GateConstants.License,
            "license");
        RequireEqual(
            receipt.Architecture,
            GateConstants.Architecture,
            "architecture");
        if (!receipt.Redistributable ||
            receipt.GplEnabled)
        {
            throw new InvalidDataException(
                "FFmpeg redistribution or GPL boundary is invalid.");
        }
        RequireEqual(
            receipt.SoftwareH264Fallback,
            GateConstants.EncoderName,
            "softwareH264Fallback");
    }

    private static void ValidateReleaseMetadata(
        FfmpegReceipt receipt)
    {
        RequireEqual(
            receipt.Archive.ReleaseTag,
            ExpectedRelease.ReleaseTag,
            "archive.releaseTag");
        RequireEqual(
            receipt.Archive.Url,
            ExpectedRelease.ArchiveUrl,
            "archive.url");
        RequireHash(
            receipt.Archive.Sha256,
            ExpectedRelease.ArchiveSha256,
            "archive.sha256");
        RequireEqual(
            receipt.PublisherChecksums.Url,
            ExpectedRelease.PublisherChecksumsUrl,
            "publisherChecksums.url");
        RequireHash(
            receipt.PublisherChecksums.Sha256,
            ExpectedRelease.PublisherChecksumsSha256,
            "publisherChecksums.sha256");
        RequireHash(
            receipt.Source.FfmpegCommit,
            ExpectedRelease.FfmpegCommit,
            "source.ffmpegCommit");
        RequireHash(
            receipt.Source.BtbnBuildsCommit,
            ExpectedRelease.BtbnBuildsCommit,
            "source.btbnBuildsCommit");
        RequireHash(
            receipt.Source.SipsorceryMediaFfmpegCommit,
            ExpectedRelease.SipsorceryMediaFfmpegCommit,
            "source.sipsorceryMediaFfmpegCommit");
        RequireHash(
            receipt.Source.FfmpegAutoGenCommit,
            ExpectedRelease.FfmpegAutoGenCommit,
            "source.ffmpegAutoGenCommit");
    }

    private static void ValidateAbiMajors(
        IReadOnlyDictionary<string, int> actual)
    {
        RequireDictionaryKeys(
            actual.Keys,
            GateConstants.AbiMajors.Keys,
            "ABI major");
        foreach (var expected in GateConstants.AbiMajors)
        {
            if (!actual.TryGetValue(
                    expected.Key,
                    out int value) ||
                value != expected.Value)
            {
                throw new InvalidDataException(
                    $"FFmpeg ABI major is invalid: " +
                    $"{expected.Key}.");
            }
        }
    }

    private static void ValidateEncoderOptions(
        IReadOnlyDictionary<string, string> actual)
    {
        RequireDictionaryKeys(
            actual.Keys,
            GateConstants.EncoderOptions.Keys,
            "encoder option");
        foreach (var expected in GateConstants.EncoderOptions)
        {
            if (!actual.TryGetValue(
                    expected.Key,
                    out string? value) ||
                !string.Equals(
                    value,
                    expected.Value,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"FFmpeg encoder option is invalid: " +
                    $"{expected.Key}.");
            }
        }
    }

    private static void ValidateManagedComponents(
        IReadOnlyCollection<ManagedComponent> components)
    {
        if (components.Count != 2)
        {
            throw new InvalidDataException(
                "Managed FFmpeg component count is invalid.");
        }

        var byName = components.ToDictionary(
            component => component.Name
                ?? throw new InvalidDataException(
                    "Managed component name is missing."),
            StringComparer.Ordinal);
        ValidateManagedComponent(
            byName,
            "SIPSorceryMedia.FFmpeg",
            "10.0.12",
            "LGPL-2.1-only");
        ValidateManagedComponent(
            byName,
            "FFmpeg.AutoGen",
            "8.1.0",
            "MIT");
    }

    private static void ValidateManagedComponent(
        IReadOnlyDictionary<string, ManagedComponent> byName,
        string name,
        string version,
        string license)
    {
        if (!byName.TryGetValue(name, out var component))
        {
            throw new InvalidDataException(
                $"Managed component is missing: {name}.");
        }
        RequireEqual(
            component.Version,
            version,
            $"{name}.version");
        RequireEqual(
            component.License,
            license,
            $"{name}.license");
    }

    private static void ValidateNativeFileInventory(
        IReadOnlyCollection<AssetFile> files)
    {
        var names = new HashSet<string>(
            StringComparer.Ordinal);
        foreach (var file in files)
        {
            string name = file.Name
                ?? throw new InvalidDataException(
                    "FFmpeg receipt file name is missing.");
            if (!names.Add(name) ||
                name != Path.GetFileName(name) ||
                name.Contains(Path.DirectorySeparatorChar) ||
                name.Contains(Path.AltDirectorySeparatorChar))
            {
                throw new InvalidDataException(
                    $"FFmpeg receipt file name is invalid: {name}.");
            }
            if (file.Length <= 0 ||
                file.Sha256 is null ||
                !GateHash.IsSha256(file.Sha256))
            {
                throw new InvalidDataException(
                    $"FFmpeg receipt file metadata is invalid: {name}.");
            }
        }

        var nativeFiles = files
            .Where(
                file => file.Name?.EndsWith(
                    ".dll",
                    StringComparison.Ordinal) == true)
            .ToDictionary(
                file => file.Name!,
                StringComparer.Ordinal);
        RequireDictionaryKeys(
            nativeFiles.Keys,
            ExpectedRelease.NativeAssets.Keys,
            "native FFmpeg file");

        foreach (var expected in ExpectedRelease.NativeAssets.Values)
        {
            AssetFile actual = nativeFiles[expected.Name];
            if (actual.Length != expected.Length)
            {
                throw new InvalidDataException(
                    $"FFmpeg file length is invalid: " +
                    $"{expected.Name}.");
            }
            RequireHash(
                actual.Sha256,
                expected.Sha256,
                $"files[{expected.Name}].sha256");
        }
    }

    private static void RequireDictionaryKeys(
        IEnumerable<string> actualKeys,
        IEnumerable<string> expectedKeys,
        string description)
    {
        var actual = new HashSet<string>(
            actualKeys,
            StringComparer.Ordinal);
        var expected = new HashSet<string>(
            expectedKeys,
            StringComparer.Ordinal);
        if (!actual.SetEquals(expected))
        {
            throw new InvalidDataException(
                $"{description} key set is invalid.");
        }
    }

    private static void RequireEqual(
        string? actual,
        string expected,
        string description)
    {
        if (!string.Equals(
                actual,
                expected,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"FFmpeg {description} is invalid.");
        }
    }

    private static void RequireHash(
        string? actual,
        string expected,
        string description)
    {
        if (actual is null ||
            !GateHash.IsSha256(actual) ||
            !string.Equals(
                actual,
                expected,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"FFmpeg {description} is invalid.");
        }
    }
}

internal static class GateHash
{
    internal static string Sha256File(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        return Convert.ToHexString(
            SHA256.HashData(stream));
    }

    internal static string Sha256Bytes(
        ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    internal static bool IsSha256(string value)
    {
        if (value.Length != 64)
        {
            return false;
        }
        foreach (char character in value)
        {
            bool isHex =
                character is >= '0' and <= '9' or
                >= 'A' and <= 'F' or
                >= 'a' and <= 'f';
            if (!isHex)
            {
                return false;
            }
        }

        return true;
    }
}
