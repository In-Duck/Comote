using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Host
{
    internal static class ServiceCredentialStore
    {
        private const string Header = "COMOTE_SERVICE_REFRESH_V1";
        private static readonly byte[] Entropy =
            Encoding.UTF8.GetBytes("Comote.Host.Service.RefreshToken.v1");

        private static string FilePath =>
            Path.Combine(AppSettings.DataDirectory, "service-refresh.dat");

        public static bool TryLoad(out string refreshToken)
        {
            refreshToken = "";
            try
            {
                if (!File.Exists(FilePath)) return false;
                var lines = File.ReadAllLines(FilePath);
                if (lines.Length != 2 || lines[0] != Header)
                {
                    Delete();
                    return false;
                }

                var encrypted = Convert.FromBase64String(lines[1]);
                var bytes = ProtectedData.Unprotect(
                    encrypted,
                    Entropy,
                    DataProtectionScope.LocalMachine);
                refreshToken = Encoding.UTF8.GetString(bytes);
                return !string.IsNullOrWhiteSpace(refreshToken);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ServiceAuth] Failed to load refresh token: {ex.Message}");
                refreshToken = "";
                return false;
            }
        }

        public static bool Save(string refreshToken)
        {
            try
            {
                Directory.CreateDirectory(AppSettings.DataDirectory);
                var bytes = Encoding.UTF8.GetBytes(refreshToken);
                var encrypted = ProtectedData.Protect(
                    bytes,
                    Entropy,
                    DataProtectionScope.LocalMachine);
                File.WriteAllLines(
                    FilePath,
                    new[] { Header, Convert.ToBase64String(encrypted) });
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[ServiceAuth] Unattended access was not configured. " +
                    $"Run Host as administrator once. {ex.Message}");
                return false;
            }
        }

        public static void Delete()
        {
            try
            {
                if (File.Exists(FilePath)) File.Delete(FilePath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ServiceAuth] Failed to delete refresh token: {ex.Message}");
            }
        }
    }
}
