namespace Host
{
    internal static class DeviceIdentityStore
    {
        private static string DirectoryPath =>
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "Comote");

        private static string FilePath =>
            Path.Combine(DirectoryPath, "device-id.txt");

        public static void Reset()
        {
            try
            {
                if (File.Exists(FilePath)) File.Delete(FilePath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Identity] Device ID reset failed: {ex.Message}");
            }
        }
        public static string GetOrCreate()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var existing = File.ReadAllText(FilePath).Trim();
                    if (Guid.TryParseExact(existing, "N", out _))
                        return "client-" + existing;
                }

                Directory.CreateDirectory(DirectoryPath);
                var generated = Guid.NewGuid().ToString("N");
                File.WriteAllText(FilePath, generated);
                return "client-" + generated;
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[Identity] Persistent device ID unavailable: {ex.Message}");
                return "client-" + Guid.NewGuid().ToString("N");
            }
        }
    }
}
