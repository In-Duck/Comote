using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using Newtonsoft.Json;

namespace Host
{
    internal sealed class ClientUpdateManifest
    {
        [JsonProperty("version")]
        public string Version { get; set; } = "";

        [JsonProperty("client_package_url")]
        public string ClientPackageUrl { get; set; } = "";

        [JsonProperty("client_package_sha256")]
        public string ClientPackageSha256 { get; set; } = "";
    }

    internal static class ClientAutoUpdater
    {
        private static readonly HttpClient Http = new()
        {
            Timeout = TimeSpan.FromSeconds(20),
        };

        public static async Task<bool> TryStageUpdateAsync(
            string manifestUrl,
            string[] restartArguments,
            CancellationToken cancellationToken = default)
        {
            if (!Uri.TryCreate(manifestUrl, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps)
            {
                Console.WriteLine("[Updater] Update manifest must use HTTPS.");
                return false;
            }

            try
            {
                var json = await Http.GetStringAsync(uri, cancellationToken);
                var manifest = JsonConvert.DeserializeObject<ClientUpdateManifest>(json);
                if (manifest == null ||
                    !Version.TryParse(manifest.Version, out var availableVersion) ||
                    !Uri.TryCreate(
                        manifest.ClientPackageUrl,
                        UriKind.Absolute,
                        out var packageUri) ||
                    packageUri.Scheme != Uri.UriSchemeHttps ||
                    manifest.ClientPackageSha256.Length != 64)
                {
                    Console.WriteLine("[Updater] Update manifest is invalid.");
                    return false;
                }

                var currentVersion = Assembly.GetExecutingAssembly()
                    .GetName().Version ?? new Version(0, 0, 0, 0);
                if (availableVersion <= currentVersion)
                {
                    Console.WriteLine(
                        $"[Updater] Client is current ({currentVersion}).");
                    return false;
                }

                Console.WriteLine(
                    $"[Updater] Downloading {availableVersion} from {packageUri.Host}.");
                var stageDirectory = Path.Combine(
                    Path.GetTempPath(),
                    "ComoteUpdate",
                    Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(stageDirectory);
                var archivePath = Path.Combine(stageDirectory, "package.zip");
                var bytes = await Http.GetByteArrayAsync(packageUri, cancellationToken);
                var actualHash = Convert.ToHexString(
                    SHA256.HashData(bytes));
                if (!actualHash.Equals(
                        manifest.ClientPackageSha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("[Updater] SHA-256 validation failed.");
                    Directory.Delete(stageDirectory, true);
                    return false;
                }

                await File.WriteAllBytesAsync(
                    archivePath, bytes, cancellationToken);
                var extractedDirectory = Path.Combine(stageDirectory, "files");
                ZipFile.ExtractToDirectory(archivePath, extractedDirectory);
                var replacement = Directory.GetFiles(
                    extractedDirectory,
                    "ComoteClient.exe",
                    SearchOption.AllDirectories).SingleOrDefault();
                var executablePath = Environment.ProcessPath;
                if (replacement == null ||
                    string.IsNullOrWhiteSpace(executablePath))
                {
                    Console.WriteLine("[Updater] Client executable was not found in package.");
                    Directory.Delete(stageDirectory, true);
                    return false;
                }

                var scriptPath = Path.Combine(stageDirectory, "apply-update.cmd");
                var commandLine = string.Join(
                    " ", restartArguments.Select(Quote));
                var script = string.Join(Environment.NewLine, new[]
                {
                    "@echo off",
                    "timeout /t 2 /nobreak >nul",
                    $"copy /y {Quote(replacement)} {Quote(executablePath)} >nul",
                    $"start \"\" {Quote(executablePath)} {commandLine}",
                    $"rmdir /s /q {Quote(stageDirectory)}",
                });
                await File.WriteAllTextAsync(
                    scriptPath, script, cancellationToken);
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c {Quote(scriptPath)}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(executablePath)!,
                });
                Console.WriteLine(
                    $"[Updater] Update {availableVersion} staged. Restarting client.");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Updater] Update check failed: {ex.Message}");
                return false;
            }
        }

        private static string Quote(string value) =>
            "\"" + value.Replace("\"", "\\\"") + "\"";
    }
}
