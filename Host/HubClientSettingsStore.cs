using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace Host
{
    internal sealed class HubClientSettings
    {
        public string ManagerAddress { get; set; } = "";
        public int ManagerPort { get; set; } = 45820;
        public string ClientName { get; set; } = Environment.MachineName;
        public int AdapterIndex { get; set; }
        public int OutputIndex { get; set; }
        public string EncryptedAccessKey { get; set; } = "";
    }

    internal static class HubClientSettingsStore
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes(
            "Comote.HubClient.Settings.v2");

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "Comote",
            "hub-client-settings.json");

        public static HubClientSettings? TryLoad(out string accessKey)
        {
            accessKey = "";
            try
            {
                if (!File.Exists(FilePath)) return null;
                var settings = JsonConvert.DeserializeObject<HubClientSettings>(
                    File.ReadAllText(FilePath));
                if (settings == null ||
                    string.IsNullOrWhiteSpace(settings.EncryptedAccessKey))
                    return null;

                var encrypted = Convert.FromBase64String(
                    settings.EncryptedAccessKey);
                var plaintext = ProtectedData.Unprotect(
                    encrypted,
                    Entropy,
                    DataProtectionScope.CurrentUser);
                try
                {
                    accessKey = new UTF8Encoding(false, true)
                        .GetString(plaintext);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                    CryptographicOperations.ZeroMemory(encrypted);
                }
                return settings;
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[HubSettings] Saved settings could not be read: {ex.Message}");
                accessKey = "";
                return null;
            }
        }

        public static bool Save(
            HubClientSettings settings,
            string accessKey)
        {
            byte[]? plaintext = null;
            byte[]? encrypted = null;
            string? temporaryPath = null;
            try
            {
                var directory = Path.GetDirectoryName(FilePath)!;
                Directory.CreateDirectory(directory);
                plaintext = Encoding.UTF8.GetBytes(accessKey);
                encrypted = ProtectedData.Protect(
                    plaintext,
                    Entropy,
                    DataProtectionScope.CurrentUser);
                settings.EncryptedAccessKey =
                    Convert.ToBase64String(encrypted);
                temporaryPath = Path.Combine(
                    directory,
                    $".hub-client-settings-{Guid.NewGuid():N}.tmp");
                File.WriteAllText(
                    temporaryPath,
                    JsonConvert.SerializeObject(
                        settings,
                        Formatting.Indented),
                    new UTF8Encoding(false));
                File.Move(
                    temporaryPath,
                    FilePath,
                    overwrite: true);
                temporaryPath = null;
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[HubSettings] Settings could not be saved: {ex.Message}");
                return false;
            }
            finally
            {
                if (plaintext is not null)
                    CryptographicOperations.ZeroMemory(plaintext);
                if (encrypted is not null)
                    CryptographicOperations.ZeroMemory(encrypted);
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
    }
}