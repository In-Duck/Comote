using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Viewer
{
    internal static class UserCredentialStore
    {
        private const string Header = "COMOTE_USER_CREDENTIAL_V1";

        private static string DirectoryPath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Comote",
                "Viewer");

        private static string FilePath => Path.Combine(DirectoryPath, "login.dat");

        public static bool TryLoad(out string email, out string password)
        {
            email = "";
            password = "";

            try
            {
                if (!File.Exists(FilePath)) return false;
                var lines = File.ReadAllLines(FilePath);
                if (lines.Length != 3 || lines[0] != Header)
                {
                    Delete();
                    return false;
                }

                email = Unprotect(lines[1]);
                password = Unprotect(lines[2]);
                return !string.IsNullOrWhiteSpace(email);
            }
            catch
            {
                Delete();
                email = "";
                password = "";
                return false;
            }
        }

        public static void Save(string email, string password)
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllLines(
                FilePath,
                new[] { Header, Protect(email), Protect(password) });
        }

        public static void Delete()
        {
            try
            {
                if (File.Exists(FilePath)) File.Delete(FilePath);
            }
            catch
            {
                // A stale credential can be ignored and overwritten later.
            }
        }

        private static string Protect(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            var encrypted = ProtectedData.Protect(
                bytes,
                null,
                DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(encrypted);
        }

        private static string Unprotect(string value)
        {
            var encrypted = Convert.FromBase64String(value);
            var bytes = ProtectedData.Unprotect(
                encrypted,
                null,
                DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
    }
}
