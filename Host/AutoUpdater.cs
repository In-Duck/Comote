using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Reflection;
using Newtonsoft.Json;

namespace Host
{
    public class UpdateInfo
    {
        [JsonProperty("version")]
        public string Version { get; set; } = "1.0.0";

        [JsonProperty("setup_url")]
        public string SetupUrl { get; set; } = "";

        [JsonProperty("release_notes")]
        public string ReleaseNotes { get; set; } = "";
    }

    public static class AutoUpdater
    {
        private static readonly HttpClient _httpClient = new HttpClient
        {
            // 네트워크 문제 시 무한 대기 방지 (5초 타임아웃)
            Timeout = TimeSpan.FromSeconds(5)
        };
        
        // TODO: 사용자 Github 저장소 주소로 변경 필요
        private const string VersionUrl = "https://raw.githubusercontent.com/rkddl/comote/main/version.json";

        public static async Task CheckAndApplyUpdate(bool isService)
        {
            try
            {
                Console.WriteLine("[Updater] Checking for updates...");
                
                var response = await _httpClient.GetStringAsync(VersionUrl);
                var updateInfo = JsonConvert.DeserializeObject<UpdateInfo>(response);

                if (updateInfo == null) return;

                Version currentVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
                Version newVersion = new Version(updateInfo.Version);

                if (newVersion > currentVersion)
                {
                    Console.WriteLine($"[Updater] New version available: {newVersion} (Current: {currentVersion})");
                    await DownloadAndInstall(updateInfo.SetupUrl, isService);
                }
                else
                {
                    Console.WriteLine("[Updater] Already up to date.");
                }
            }
            catch (Exception ex)
            {
                // 업데이트 체크 실패 시 프로그램 자체는 정상 실행되도록 유지
                Console.WriteLine($"[Updater] Check failed (ignored): {ex.Message}");
            }
        }

        private static async Task DownloadAndInstall(string url, bool isService)
        {
            try
            {
                string tempPath = Path.Combine(Path.GetTempPath(), "Comote_Setup.exe");
                Console.WriteLine($"[Updater] Downloading update to {tempPath}...");

                var data = await _httpClient.GetByteArrayAsync(url);
                await File.WriteAllBytesAsync(tempPath, data);

                Console.WriteLine("[Updater] Download complete. Executing installer...");

                // Inno Setup Silent Install options:
                // /VERYSILENT: UI 아예 안 보임
                // /SUPPRESSMSGBOXES: 메시지 박스 무시
                // /NORESTART: 재부팅 방지
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = tempPath,
                    Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
                    UseShellExecute = !isService, // 서비스 모드에서는 ShellExecute 사용 불가 (세션 0)
                };

                // 일반 모드(사용자 세션)에서만 관리자 권한 요청
                if (!isService) psi.Verb = "runas";

                Process.Start(psi);

                // 현재 프로세스 종료 (설치 프로그램이 파일을 교체할 수 있도록)
                Console.WriteLine("[Updater] Closing current process for update...");
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Updater] Update failed: {ex.Message}");
            }
        }
    }
}
