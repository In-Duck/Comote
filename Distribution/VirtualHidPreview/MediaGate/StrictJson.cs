using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Comote.MediaGate;

internal static class StrictJson
{
    private const int MaximumJsonBytes = 1024 * 1024;

    internal static readonly JsonSerializerOptions SerializerOptions =
        new()
        {
            AllowTrailingCommas = false,
            PropertyNameCaseInsensitive = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = true,
            DefaultIgnoreCondition =
                JsonIgnoreCondition.WhenWritingNull,
        };

    internal static T ReadFile<T>(string path)
    {
        string fullPath = GatePaths.RequireAbsoluteLocalFile(
            path,
            "JSON input");
        var fileInfo = new FileInfo(fullPath);
        if (fileInfo.Length is <= 0 or > MaximumJsonBytes)
        {
            throw new InvalidDataException(
                $"JSON input size is invalid: {fileInfo.Length}.");
        }

        return Deserialize<T>(File.ReadAllBytes(fullPath));
    }

    internal static T ReadResource<T>(
        Assembly assembly,
        string resourceName)
    {
        using var stream =
            assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidDataException(
                $"Embedded resource is missing: {resourceName}.");
        if (stream.Length is <= 0 or > MaximumJsonBytes)
        {
            throw new InvalidDataException(
                $"Embedded JSON size is invalid: {stream.Length}.");
        }

        using var memory = new MemoryStream(
            checked((int)stream.Length));
        stream.CopyTo(memory);
        return Deserialize<T>(memory.ToArray());
    }

    private static T Deserialize<T>(byte[] utf8Json)
    {
        EnsureNoDuplicateProperties(utf8Json);
        return JsonSerializer.Deserialize<T>(
                   utf8Json,
                   SerializerOptions)
               ?? throw new InvalidDataException(
                   "JSON deserialised to null.");
    }

    private static void EnsureNoDuplicateProperties(
        ReadOnlySpan<byte> utf8Json)
    {
        var reader = new Utf8JsonReader(
            utf8Json,
            new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64,
            });
        var objectProperties =
            new Stack<HashSet<string>>();
        bool sawRootObject = false;
        bool sawFirstToken = false;

        while (reader.Read())
        {
            if (!sawFirstToken)
            {
                sawFirstToken = true;
                if (reader.TokenType != JsonTokenType.StartObject)
                {
                    throw new JsonException(
                        "JSON root must be an object.");
                }
            }

            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    objectProperties.Push(
                        new HashSet<string>(StringComparer.Ordinal));
                    sawRootObject = true;
                    break;

                case JsonTokenType.EndObject:
                    if (objectProperties.Count == 0)
                    {
                        throw new JsonException(
                            "Unbalanced JSON object.");
                    }
                    objectProperties.Pop();
                    break;

                case JsonTokenType.PropertyName:
                    if (objectProperties.Count == 0)
                    {
                        throw new JsonException(
                            "JSON property is outside an object.");
                    }

                    string name = reader.GetString()
                        ?? throw new JsonException(
                            "JSON property name is null.");
                    if (!objectProperties.Peek().Add(name))
                    {
                        throw new JsonException(
                            $"Duplicate JSON property: {name}.");
                    }
                    break;
            }
        }

        if (!sawRootObject ||
            objectProperties.Count != 0)
        {
            throw new JsonException(
                "JSON root must be a complete object.");
        }
    }
}

internal static class GatePaths
{
    internal static string RequireAbsoluteLocalFile(
        string path,
        string description)
    {
        string fullPath =
            RequireAbsoluteLocalPath(path, description);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(
                $"{description} does not exist.",
                fullPath);
        }

        var attributes = File.GetAttributes(fullPath);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"{description} cannot be a reparse point.");
        }

        return fullPath;
    }

    internal static string RequireNewOutputFile(string path)
    {
        string fullPath =
            RequireAbsoluteLocalPath(path, "Evidence output");
        if (File.Exists(fullPath) ||
            Directory.Exists(fullPath))
        {
            throw new IOException(
                $"Evidence output already exists: {fullPath}");
        }

        string? parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(parent) ||
            !Directory.Exists(parent))
        {
            throw new DirectoryNotFoundException(
                "Evidence output parent directory does not exist.");
        }

        var parentAttributes = File.GetAttributes(parent);
        if ((parentAttributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                "Evidence output parent cannot be a reparse point.");
        }

        return fullPath;
    }

    internal static string RequireAbsoluteLocalPath(
        string path,
        string description)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException(
                $"{description} path must be absolute.");
        }

        string fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith(
                @"\\",
                StringComparison.Ordinal) ||
            new Uri(fullPath).IsUnc)
        {
            throw new ArgumentException(
                $"{description} path must be local.");
        }

        return fullPath;
    }
}
