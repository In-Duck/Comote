using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Viewer;

internal sealed record ManagerHubSettings(
    int Port,
    IReadOnlyList<ManagerHubDeviceCredential> Devices);

internal static class ManagerHubSettingsStore
{
    private const int SchemaVersion = 2;
    private const int MaximumDevices = 256;

    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("Comote.ManagerHub.Settings.v2");

    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData),
        "Comote");

    private static readonly string FilePath = Path.Combine(
        DirectoryPath,
        "manager-hub-settings.dat");

    public static bool TryLoad(out ManagerHubSettings settings)
    {
        settings = new ManagerHubSettings(
            45820,
            Array.Empty<ManagerHubDeviceCredential>());
        byte[]? protectedBytes = null;
        byte[]? plaintext = null;
        try
        {
            if (!File.Exists(FilePath))
            {
                return false;
            }

            protectedBytes = File.ReadAllBytes(FilePath);
            plaintext = ProtectedData.Unprotect(
                protectedBytes,
                Entropy,
                DataProtectionScope.CurrentUser);
            var root = StrictJsonObject.Parse(plaintext);
            if (!HasOnlyProperties(
                    root,
                    "schemaVersion",
                    "port",
                    "devices") ||
                root["schemaVersion"]?.Type != JTokenType.Integer ||
                root.Value<int>("schemaVersion") != SchemaVersion ||
                root["port"]?.Type != JTokenType.Integer ||
                root["devices"] is not JArray devices ||
                devices.Count is <= 0 or > MaximumDevices)
            {
                return false;
            }

            var port = root.Value<int>("port");
            if (port is < 1024 or > 65535)
            {
                return false;
            }

            var credentials =
                new List<ManagerHubDeviceCredential>(devices.Count);
            var routingIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var token in devices)
            {
                if (token is not JObject device ||
                    !HasOnlyProperties(device, "name", "accessKey") ||
                    device["name"]?.Type != JTokenType.String ||
                    device["accessKey"]?.Type != JTokenType.String)
                {
                    return false;
                }

                ManagerHubDeviceCredential credential;
                try
                {
                    credential = new ManagerHubDeviceCredential(
                        device.Value<string>("name") ?? "",
                        device.Value<string>("accessKey") ?? "");
                }
                catch (ArgumentException)
                {
                    return false;
                }

                if (!routingIds.Add(credential.RoutingId))
                {
                    return false;
                }
                credentials.Add(credential);
            }

            settings = new ManagerHubSettings(port, credentials);
            return true;
        }
        catch (Exception ex) when (
            ex is IOException or
                UnauthorizedAccessException or
                CryptographicException or
                JsonException or
                FormatException)
        {
            return false;
        }
        finally
        {
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
            if (protectedBytes is not null)
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }
        }
    }

    public static void Save(
        int port,
        IEnumerable<ManagerHubDeviceCredential> devices)
    {
        if (port is < 1024 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }
        ArgumentNullException.ThrowIfNull(devices);

        var materialized = devices.ToArray();
        if (materialized.Length is <= 0 or > MaximumDevices)
        {
            throw new ArgumentOutOfRangeException(nameof(devices));
        }
        if (materialized.Any(static credential => credential is null) ||
            materialized.Select(static credential => credential.RoutingId)
                .Distinct(StringComparer.Ordinal)
                .Count() != materialized.Length)
        {
            throw new ArgumentException(
                "Manager Hub device credentials must be non-null and unique.",
                nameof(devices));
        }

        byte[]? plaintext = null;
        byte[]? protectedBytes = null;
        string? temporaryPath = null;
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var root = new JObject
            {
                ["schemaVersion"] = SchemaVersion,
                ["port"] = port,
                ["devices"] = new JArray(
                    materialized.Select(credential =>
                        new JObject
                        {
                            ["name"] = credential.DisplayName,
                            ["accessKey"] = credential.AccessKey,
                        })),
            };
            plaintext = Encoding.UTF8.GetBytes(
                root.ToString(Formatting.None));
            protectedBytes = ProtectedData.Protect(
                plaintext,
                Entropy,
                DataProtectionScope.CurrentUser);
            temporaryPath = Path.Combine(
                DirectoryPath,
                $".manager-hub-settings-{Guid.NewGuid():N}.tmp");
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(protectedBytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, FilePath, overwrite: true);
            temporaryPath = null;
        }
        finally
        {
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
            if (protectedBytes is not null)
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch
                {
                }
            }
        }
    }

    /// <summary>
    /// Removes the persisted Manager Hub settings. Used when the last device is
    /// deregistered, since the store cannot persist an empty device set; without
    /// this the removed device would reload on the next start.
    /// </summary>
    public static void Clear()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                File.Delete(FilePath);
            }
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool HasOnlyProperties(
        JObject value,
        params string[] names)
    {
        var allowed = new HashSet<string>(names, StringComparer.Ordinal);
        return value.Properties().All(
                property => allowed.Remove(property.Name)) &&
            allowed.Count == 0;
    }
}
