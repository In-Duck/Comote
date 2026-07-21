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

    internal sealed record ClientUpdateInfo(
        Version Version,
        Uri PackageUri,
        string Sha256,
        string ReleaseNotes);

    internal static class ClientAutoUpdater
    {
        private const long MaximumPackageBytes = 750L * 1024 * 1024;
        private const long MaximumExpandedBytes = 2L * 1024 * 1024 * 1024;
        private static readonly HttpClient Http = new()
        {
            Timeout = TimeSpan.FromMinutes(10),
        };

        public static Version CurrentVersion =>
            Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);

        public static async Task<ClientUpdateInfo?> CheckForUpdateAsync(
            string manifestUrl,
            CancellationToken cancellationToken = default)
        {
            if (!TryGetHttpsUri(manifestUrl, out var manifestUri))
                throw new InvalidOperationException("업데이트 정보 주소는 HTTPS여야 합니다.");

            using var response = await Http.GetAsync(
                manifestUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > 128 * 1024)
                throw new InvalidDataException("업데이트 정보가 허용 크기를 초과했습니다.");

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var manifest = JsonConvert.DeserializeObject<ClientUpdateManifest>(json);
            if (!TryValidateManifest(manifest, out var availableVersion, out var packageUri))
                throw new InvalidDataException("업데이트 정보가 올바르지 않습니다.");

            if (availableVersion <= CurrentVersion) return null;
            return new ClientUpdateInfo(
                availableVersion,
                packageUri,
                manifest!.ClientPackageSha256.ToUpperInvariant(),
                manifest.ReleaseNotes);
        }

        public static async Task<bool> TryStageUpdateAsync(
            string manifestUrl,
            string[] restartArguments,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var update = await CheckForUpdateAsync(manifestUrl, cancellationToken);
                if (update == null)
                {
                    Console.WriteLine($"[Updater] Client is current ({CurrentVersion}).");
                    return false;
                }
                return await StageUpdateAsync(update, restartArguments, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("[Updater] Update was cancelled.");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Updater] Update check failed: {ex.Message}");
                return false;
            }
        }

        public static async Task<bool> StageUpdateAsync(
            ClientUpdateInfo update,
            string[] restartArguments,
            CancellationToken cancellationToken = default)
        {
            string? stageDirectory = null;
            try
            {
                stageDirectory = Path.Combine(
                    Path.GetTempPath(), "ComoteUpdate", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(stageDirectory);
                var archivePath = Path.Combine(stageDirectory, "package.zip");
                var extractedDirectory = Path.Combine(stageDirectory, "files");

                Console.WriteLine($"[Updater] Downloading {update.Version} from {update.PackageUri.Host}.");
                await DownloadFileAsync(update.PackageUri, archivePath, cancellationToken);
                var actualHash = await ComputeSha256Async(archivePath, cancellationToken);
                if (!actualHash.Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("다운로드한 파일의 SHA-256 값이 일치하지 않습니다.");

                ExtractValidatedArchive(archivePath, extractedDirectory);
                var replacements = Directory.GetFiles(
                    extractedDirectory, "ComoteClient.exe", SearchOption.AllDirectories);
                var executablePath = Environment.ProcessPath;
                if (replacements.Length != 1 || string.IsNullOrWhiteSpace(executablePath))
                    throw new InvalidDataException("패키지에서 ComoteClient.exe 하나를 찾지 못했습니다.");

                var replacement = replacements[0];
                var replacementVersionText = FileVersionInfo.GetVersionInfo(replacement).FileVersion;
                if (!Version.TryParse(replacementVersionText, out var replacementVersion) ||
                    replacementVersion != update.Version)
                    throw new InvalidDataException("패키지 실행 파일 버전이 업데이트 정보와 다릅니다.");

                var packageRoot = Path.GetDirectoryName(replacement)!;
                var installDirectory = Path.GetDirectoryName(executablePath)!;
                if (SecureDesktopService.IsSystemAgent() && SecureDesktopService.IsInstalled())
                    ScheduleServiceUpdate(stageDirectory, packageRoot, installDirectory, executablePath);
                else
                    ScheduleInteractiveUpdate(
                        stageDirectory, packageRoot, installDirectory,
                        executablePath, restartArguments);

                Console.WriteLine($"[Updater] Update {update.Version} verified and staged.");
                stageDirectory = null;
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Updater] Update staging failed: {ex.Message}");
                return false;
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(stageDirectory) && Directory.Exists(stageDirectory))
                {
                    try { Directory.Delete(stageDirectory, true); } catch { }
                }
            }
        }

        private static void ScheduleInteractiveUpdate(
            string stageDirectory,
            string packageRoot,
            string installDirectory,
            string executablePath,
            string[] restartArguments)
        {
            var scriptPath = Path.Combine(
                Path.GetTempPath(), $"comote-apply-update-{Guid.NewGuid():N}.cmd");
            var restartCommandLine = string.Join(" ", restartArguments.Select(Quote));
            var script = string.Join(Environment.NewLine, new[]
            {
                "@echo off", "setlocal", "timeout /t 2 /nobreak >nul",
                $"robocopy {Quote(packageRoot)} {Quote(installDirectory)} /E /R:10 /W:1 /NFL /NDL /NJH /NJS /NP >nul",
                "if errorlevel 8 exit /b %errorlevel%",
                $"start \"\" {Quote(executablePath)} {restartCommandLine}",
                $"rmdir /s /q {Quote(stageDirectory)}", "del /q \"%~f0\"",
            });
            File.WriteAllText(scriptPath, script);
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/d /c {Quote(scriptPath)}",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = installDirectory,
            });
        }

        private static void ScheduleServiceUpdate(
            string stageDirectory,
            string packageRoot,
            string installDirectory,
            string executablePath)
        {
            var taskName = $"ComoteUpdate-{Guid.NewGuid():N}";
            var scriptPath = Path.Combine(stageDirectory, "apply-service-update.cmd");
            var backupPath = executablePath + ".update-backup";
            var script = string.Join(Environment.NewLine, new[]
            {
                "@echo off", "setlocal",
                $"sc.exe stop {SecureDesktopService.ServiceName} >nul 2>&1",
                $":waitstop", $"sc.exe query {SecureDesktopService.ServiceName} | find \"STOPPED\" >nul",
                "if errorlevel 1 (timeout /t 1 /nobreak >nul & goto waitstop)",
                $"copy /y {Quote(executablePath)} {Quote(backupPath)} >nul",
                $"robocopy {Quote(packageRoot)} {Quote(installDirectory)} /E /R:10 /W:1 /NFL /NDL /NJH /NJS /NP >nul",
                "if errorlevel 8 goto rollback", $"sc.exe start {SecureDesktopService.ServiceName} >nul 2>&1",
                $"del /q {Quote(backupPath)} >nul 2>&1", "goto cleanup",
                ":rollback", $"copy /y {Quote(backupPath)} {Quote(executablePath)} >nul",
                $"sc.exe start {SecureDesktopService.ServiceName} >nul 2>&1",
                ":cleanup", $"schtasks.exe /Delete /TN {Quote(taskName)} /F >nul 2>&1",
                $"rmdir /s /q {Quote(stageDirectory)}", "exit /b 0",
            });
            File.WriteAllText(scriptPath, script);

            var taskCommand = $"cmd.exe /d /c {Quote(scriptPath)}";
            RunScheduleCommand($"/Create /TN {Quote(taskName)} /SC ONLOGON /RU SYSTEM /RL HIGHEST /TR {Quote(taskCommand)} /F");
            RunScheduleCommand($"/Run /TN {Quote(taskName)}");
        }

        private static void RunScheduleCommand(string arguments)
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe", Arguments = arguments,
                UseShellExecute = false, CreateNoWindow = true,
            }) ?? throw new InvalidOperationException("업데이트 작업을 시작하지 못했습니다.");
            process.WaitForExit(15000);
            if (!process.HasExited || process.ExitCode != 0)
                throw new InvalidOperationException("업데이트 작업 등록에 실패했습니다.");
        }

        private static bool TryValidateManifest(
            ClientUpdateManifest? manifest,
            out Version availableVersion,
            out Uri packageUri)
        {
            availableVersion = new Version(0, 0);
            packageUri = null!;
            if (manifest == null ||
                !Version.TryParse(manifest.Version, out var parsedVersion) ||
                parsedVersion == null ||
                !TryGetHttpsUri(manifest.ClientPackageUrl, out packageUri) ||
                !IsOfficialPackageUri(packageUri) ||
                manifest.ClientPackageSha256.Length != 64 ||
                !manifest.ClientPackageSha256.All(Uri.IsHexDigit))
                return false;
            availableVersion = parsedVersion;
            return true;
        }

        private static bool IsOfficialPackageUri(Uri uri) =>
            uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
            uri.AbsolutePath.StartsWith(
                "/In-Duck/Comote/releases/download/", StringComparison.OrdinalIgnoreCase) &&
            uri.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

        private static bool TryGetHttpsUri(string value, out Uri uri) =>
            Uri.TryCreate(value, UriKind.Absolute, out uri!) &&
            uri.Scheme == Uri.UriSchemeHttps;

        private static async Task DownloadFileAsync(
            Uri uri, string destination, CancellationToken cancellationToken)
        {
            using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long size && size > MaximumPackageBytes)
                throw new InvalidDataException("업데이트 패키지가 허용 크기를 초과했습니다.");
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var target = new FileStream(
                destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                total += read;
                if (total > MaximumPackageBytes)
                    throw new InvalidDataException("업데이트 패키지가 허용 크기를 초과했습니다.");
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }

        private static void ExtractValidatedArchive(string archivePath, string destination)
        {
            using (var archive = ZipFile.OpenRead(archivePath))
            {
                if (archive.Entries.Count > 2000 ||
                    archive.Entries.Sum(entry => entry.Length) > MaximumExpandedBytes)
                    throw new InvalidDataException("업데이트 압축 파일이 허용 범위를 초과했습니다.");
            }
            ZipFile.ExtractToDirectory(archivePath, destination);
        }

        private static async Task<string> ComputeSha256Async(
            string path, CancellationToken cancellationToken)
        {
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        }

        private static string Quote(string value) =>
            "\"" + value.Replace("\"", "\\\"") + "\"";
    }
}