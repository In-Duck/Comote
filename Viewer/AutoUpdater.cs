using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using Newtonsoft.Json;

namespace Viewer
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
            // 네트워크 문제 시 앱 시작 지연 방지 (5초 타임아웃)
            Timeout = TimeSpan.FromSeconds(5)
        };
        
        
        // Github 저장소의 version.json 주소
        private const string VersionUrl = "https://raw.githubusercontent.com/In-Duck/Comote/main/Distribution/version.json";

        public static async Task CheckAndApplyUpdate()
        {
            try
            {
                var response = await _httpClient.GetStringAsync(VersionUrl);
                var updateInfo = JsonConvert.DeserializeObject<UpdateInfo>(response);

                if (updateInfo == null) return;

                Version currentVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
                Version newVersion = new Version(updateInfo.Version);

                if (newVersion > currentVersion)
                {
                    var result = MessageBox.Show(
                        $"새 버전({newVersion})이 발견되었습니다!\n\n현재 버전: {currentVersion}\n내용: {updateInfo.ReleaseNotes}\n\n지금 업데이트하시겠습니까?",
                        "업데이트 알림",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Information);

                    if (result == MessageBoxResult.Yes)
                    {
                        await DownloadAndInstall(updateInfo.SetupUrl);
                    }
                }
            }
            catch
            {
                // 업데이트 체크 실패 시 무시하고 진행
            }
        }

        private static async Task DownloadAndInstall(string url)
        {
            try
            {
                string tempPath = Path.Combine(Path.GetTempPath(), "ComoteViewer_Setup.exe");
                
                var data = await _httpClient.GetByteArrayAsync(url);
                await File.WriteAllBytesAsync(tempPath, data);

                // Viewer는 사용자 인터랙션용이므로 기본 설정을 보여주는 것이 나을 수도 있음. 
                // 하지만 흐름상 /VERYSILENT 도 가능. 여기서는 /SILENT (진행바만 보임) 사용.
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = tempPath,
                    Arguments = "/SILENT /NORESTART",
                    UseShellExecute = true,
                    Verb = "runas"
                };

                Process.Start(psi);
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"업데이트 다운로드 중 오류가 발생했습니다: {ex.Message}", "에러", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
