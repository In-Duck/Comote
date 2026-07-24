using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using Newtonsoft.Json;

namespace Viewer;

internal sealed class ManagerUpdateManifest
{
    [JsonProperty("version")]
    public string Version { get; set; } = "";

    [JsonProperty("manager_setup_url")]
    public string SetupUrl { get; set; } = "";

    [JsonProperty("manager_setup_sha256")]
    public string Sha256 { get; set; } = "";

    [JsonProperty("release_notes")]
    public string ReleaseNotes { get; set; } = "";
}

internal sealed record ManagerUpdateProgress(int? Percent, string Status);

internal static class ManagerAutoUpdater
{
    private const string ManifestUrl =
        "https://comote-remote.dopum54.chatgpt.site/api/downloads/manager-update";
    private const long MaximumInstallerBytes = 5L * 1024 * 1024;
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(10),
    };

    internal static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ??
        new Version(0, 0);

    internal static async Task CheckAndApplyAsync(
        bool showCurrentStatus = false,
        IProgress<ManagerUpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            progress?.Report(new ManagerUpdateProgress(
                5, "Manager 업데이트 확인 중"));
            using var response = await Http.GetAsync(
                ManifestUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(
                cancellationToken);
            var manifest =
                JsonConvert.DeserializeObject<ManagerUpdateManifest>(json);
            if (manifest == null ||
                !Version.TryParse(manifest.Version, out var latest) ||
                !Uri.TryCreate(
                    manifest.SetupUrl,
                    UriKind.Absolute,
                    out var installerUri) ||
                installerUri.Scheme != Uri.UriSchemeHttps ||
                !installerUri.Host.Equals(
                    "github.com",
                    StringComparison.OrdinalIgnoreCase) ||
                manifest.Sha256.Length != 64 ||
                !manifest.Sha256.All(Uri.IsHexDigit))
            {
                throw new InvalidDataException(
                    "Manager 업데이트 정보가 올바르지 않습니다.");
            }

            if (latest <= CurrentVersion)
            {
                progress?.Report(new ManagerUpdateProgress(
                    100, $"최신 버전 · {CurrentVersion}"));
                if (showCurrentStatus)
                {
                    MessageBox.Show(
                        $"현재 Manager {CurrentVersion}는 최신 버전입니다.",
                        "Comote Manager 업데이트",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                return;
            }

            var notes = string.IsNullOrWhiteSpace(manifest.ReleaseNotes)
                ? ""
                : $"\n\n변경 사항\n{manifest.ReleaseNotes}";
            var answer = MessageBox.Show(
                $"Manager {latest} 업데이트를 설치할까요?" +
                $"\n\n현재 버전: {CurrentVersion}{notes}",
                "Comote Manager 업데이트",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);
            if (answer != MessageBoxResult.Yes)
            {
                progress?.Report(new ManagerUpdateProgress(
                    null, "Manager 업데이트 취소"));
                return;
            }

            var installerPath = Path.Combine(
                Path.GetTempPath(),
                $"ComoteManager_Setup_{latest}.exe");
            await DownloadAsync(
                installerUri,
                installerPath,
                progress,
                cancellationToken);
            progress?.Report(new ManagerUpdateProgress(
                92, "설치 파일 검증 중"));
            var actualHash = await ComputeSha256Async(
                installerPath,
                cancellationToken);
            if (!actualHash.Equals(
                    manifest.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "Manager 설치 파일의 무결성 검증에 실패했습니다.");
            }

            progress?.Report(new ManagerUpdateProgress(
                100, "설치 프로그램 시작"));
            Process.Start(new ProcessStartInfo
            {
                FileName = installerPath,
                UseShellExecute = true,
                Verb = "runas",
            });
            Application.Current.Shutdown();
        }
        catch (OperationCanceledException)
        {
            progress?.Report(new ManagerUpdateProgress(
                null, "Manager 업데이트 취소"));
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"[ManagerUpdater] Check failed: {ex.Message}");
            progress?.Report(new ManagerUpdateProgress(
                null, "Manager 업데이트 확인 실패"));
            if (showCurrentStatus)
            {
                MessageBox.Show(
                    "업데이트를 확인하지 못했습니다.\n" + ex.Message,
                    "Comote Manager 업데이트",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
    }

    private static async Task DownloadAsync(
        Uri uri,
        string destination,
        IProgress<ManagerUpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var expectedBytes = response.Content.Headers.ContentLength;
        if (expectedBytes is > MaximumInstallerBytes)
            throw new InvalidDataException(
                "Manager 설치 파일이 허용 크기를 초과했습니다.");

        await using var source =
            await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(
            destination,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(
                   buffer,
                   cancellationToken)) > 0)
        {
            total += read;
            if (total > MaximumInstallerBytes)
                throw new InvalidDataException(
                    "Manager 설치 파일이 허용 크기를 초과했습니다.");
            await target.WriteAsync(
                buffer.AsMemory(0, read),
                cancellationToken);
            var percent = expectedBytes is > 0
                ? 10 + (int)Math.Min(
                    78,
                    total * 78 / expectedBytes.Value)
                : (int?)null;
            progress?.Report(new ManagerUpdateProgress(
                percent, "Manager 업데이트 다운로드 중"));
        }
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
        using var sha256 = SHA256.Create();
        var hash = await sha256.ComputeHashAsync(
            stream,
            cancellationToken);
        return Convert.ToHexString(hash);
    }
}
