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
        public string UpdateManifestUrl { get; set; } = "";
        public string EncryptedPassword { get; set; } = "";
    }

    internal static class HubClientSettingsStore
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes(
            "Comote.HubClient.Settings.v1");

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "Comote",
            "hub-client-settings.json");

        public static HubClientSettings? TryLoad(out string password)
        {
            password = "";
            try
            {
                if (!File.Exists(FilePath)) return null;
                var settings = JsonConvert.DeserializeObject<HubClientSettings>(
                    File.ReadAllText(FilePath));
                if (settings == null ||
                    string.IsNullOrWhiteSpace(settings.EncryptedPassword))
                    return null;

                var encrypted = Convert.FromBase64String(
                    settings.EncryptedPassword);
                password = Encoding.UTF8.GetString(ProtectedData.Unprotect(
                    encrypted,
                    Entropy,
                    DataProtectionScope.CurrentUser));
                return settings;
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[HubSettings] Saved settings could not be read: {ex.Message}");
                password = "";
                return null;
            }
        }

        public static void Save(HubClientSettings settings, string password)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                settings.EncryptedPassword = Convert.ToBase64String(
                    ProtectedData.Protect(
                        Encoding.UTF8.GetBytes(password),
                        Entropy,
                        DataProtectionScope.CurrentUser));
                File.WriteAllText(
                    FilePath,
                    JsonConvert.SerializeObject(settings, Formatting.Indented));
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[HubSettings] Settings could not be saved: {ex.Message}");
            }
        }
    }
}
