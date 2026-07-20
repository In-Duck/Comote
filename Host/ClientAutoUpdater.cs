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

        [JsonProperty("minimum_version")]
        public string MinimumVersion { get; set; } = "";

        [JsonProperty("release_notes")]
        public string ReleaseNotes { get; set; } = "";
    }

    internal static class ClientAutoUpdater
    {
        private static readonly HttpClient Http = new()
        {
            Timeout = TimeSpan.FromMinutes(5),
        };

        public static async Task<bool> TryStageUpdateAsync(
            string manifestUrl,
            string[] restartArguments,
            CancellationToken cancellationToken = default)
        {
            if (!TryGetHttpsUri(manifestUrl, out var manifestUri))
            {
                Console.WriteLine("[Updater] Update manifest must use HTTPS.");
                return false;
            }

            string? stageDirectory = null;
            try
            {
                using var manifestResponse = await Http.GetAsync(
                    manifestUri,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                manifestResponse.EnsureSuccessStatusCode();
                var json = await manifestResponse.Content.ReadAsStringAsync(cancellationToken);
                var manifest = JsonConvert.DeserializeObject<ClientUpdateManifest>(json);

                if (!TryValidateManifest(manifest, out var availableVersion, out var packageUri))
                {
                    Console.WriteLine("[Updater] Update manifest is invalid.");
                    return false;
                }

                var currentVersion = Assembly.GetExecutingAssembly()
                    .GetName().Version ?? new Version(0, 0, 0, 0);
                if (availableVersion <= currentVersion)
                {
                    Console.WriteLine($"[Updater] Client is current ({currentVersion}).");
                    return false;
                }

                stageDirectory = Path.Combine(
                    Path.GetTempPath(),
                    "ComoteUpdate",
                    Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(stageDirectory);
                var archivePath = Path.Combine(stageDirectory, "package.zip");
                var extractedDirectory = Path.Combine(stageDirectory, "files");

                Console.WriteLine($"[Updater] Downloading {availableVersion} from {packageUri.Host}.");
                await DownloadFileAsync(packageUri, archivePath, cancellationToken);
                var actualHash = await ComputeSha256Async(archivePath, cancellationToken);
                if (!actualHash.Equals(
                        manifest!.ClientPackageSha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("[Updater] SHA-256 validation failed.");
                    Directory.Delete(stageDirectory, true);
                    return false;
                }

                ZipFile.ExtractToDirectory(archivePath, extractedDirectory);
                var replacement = Directory.GetFiles(
                    extractedDirectory,
                    "ComoteClient.exe",
                    SearchOption.AllDirectories).SingleOrDefault();
                var executablePath = Environment.ProcessPath;
                if (replacement == null || string.IsNullOrWhiteSpace(executablePath))
                {
                    Console.WriteLine("[Updater] ComoteClient.exe was not found in package.");
                    Directory.Delete(stageDirectory, true);
                    return false;
                }

                var packageRoot = Path.GetDirectoryName(replacement)!;
                var installDirectory = Path.GetDirectoryName(executablePath)!;
                var scriptPath = Path.Combine(
                    Path.GetTempPath(),
                    $"comote-apply-update-{Guid.NewGuid():N}.cmd");
                var restartCommandLine = string.Join(" ", restartArguments.Select(Quote));
                var script = string.Join(Environment.NewLine, new[]
                {
                    "@echo off",
                    "setlocal",
                    "timeout /t 2 /nobreak >nul",
                    $"robocopy {Quote(packageRoot)} {Quote(installDirectory)} /E /R:10 /W:1 /NFL /NDL /NJH /NJS /NP >nul",
                    "if errorlevel 8 exit /b %errorlevel%",
                    $"start \"\" {Quote(executablePath)} {restartCommandLine}",
                    $"rmdir /s /q {Quote(stageDirectory)}",
                    "del /q \"%~f0\"",
                });
                await File.WriteAllTextAsync(scriptPath, script, cancellationToken);
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/d /c {Quote(scriptPath)}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = installDirectory,
                });

                Console.WriteLine($"[Updater] Update {availableVersion} staged. Restarting client.");
                return true;
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("[Updater] Update was cancelled.");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Updater] Update check failed: {ex.Message}");
                if (!string.IsNullOrWhiteSpace(stageDirectory) && Directory.Exists(stageDirectory))
                {
                    try { Directory.Delete(stageDirectory, true); } catch { }
                }
                return false;
            }
        }

        private static bool TryValidateManifest(
            ClientUpdateManifest? manifest,
            out Version availableVersion,
            out Uri packageUri)
        {
            availableVersion = new Version(0, 0);
            packageUri = null!;
            return manifest != null &&
                   Version.TryParse(manifest.Version, out availableVersion) &&
                   TryGetHttpsUri(manifest.ClientPackageUrl, out packageUri) &&
                   manifest.ClientPackageSha256.Length == 64 &&
                   manifest.ClientPackageSha256.All(Uri.IsHexDigit);
        }

        private static bool TryGetHttpsUri(string value, out Uri uri)
        {
            return Uri.TryCreate(value, UriKind.Absolute, out uri!) &&
                   uri.Scheme == Uri.UriSchemeHttps;
        }

        private static async Task DownloadFileAsync(
            Uri uri,
            string destination,
            CancellationToken cancellationToken)
        {
            using var response = await Http.GetAsync(
                uri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var target = new FileStream(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(target, cancellationToken);
        }

        private static async Task<string> ComputeSha256Async(
            string path,
            CancellationToken cancellationToken)
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken);
            return Convert.ToHexString(hash);
        }

        private static string Quote(string value) =>
            "\"" + value.Replace("\"", "\\\"") + "\"";
    }
}
