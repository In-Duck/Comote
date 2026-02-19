using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using SIPSorceryMedia.FFmpeg;
using System.Diagnostics;

namespace Host
{
    class Program
    {
        [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int nStdHandle);
        [DllImport("kernel32.dll")] static extern bool GetConsoleMode(IntPtr h, out uint m);
        [DllImport("kernel32.dll")] static extern bool SetConsoleMode(IntPtr h, uint m);

        [STAThread]
        static async Task Main(string[] args)
        {
            bool forceNoGui = false;
            if (args.Length > 0)
            {
                if (args[0] == "--install")
                {
                    InstallService();
                    return;
                }
                if (args[0] == "--uninstall")
                {
                    UninstallService();
                    return;
                }
                if (args[0] == "--nogui")
                {
                    forceNoGui = true;
                }
            }

            bool isService = !(Environment.UserInteractive) || forceNoGui;
            
            // [무인 업데이트] 시작 시 업데이트 체크
            await AutoUpdater.CheckAndApplyUpdate(isService);
            
            // [Style] Mono Vintage Console Styling
            if (!isService)
            {
                try
                {
                    Console.Title = "KYMOTE Host";
                    Console.BackgroundColor = ConsoleColor.Black;
                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                    Console.Clear();
                    Console.WriteLine("=================================================");
                    Console.WriteLine("    KYMOTE - Premium Remote Control (v1.2.1)     ");
                    Console.WriteLine("=================================================");
                    Console.WriteLine("");
                }
                catch { } // Ignore if console is not available or locked
            }
            
            var handle = GetStdHandle(-10);
            if (GetConsoleMode(handle, out uint mode))
            {
                // Enable Quick Edit Mode (0x0040) and Extended Flags (0x0080)
                mode |= 0x0040u;
                mode |= 0x0080u;
                SetConsoleMode(handle, mode);
            }

            // [설정 로드]
            var appSettings = AppSettings.Load();

            string accessToken = null;
            string userEmail = null;
            string userId = null;

            if (isService)
            {
                Console.WriteLine("[Service] 헤드리스 모드로 실행 중. 자동 로그인을 시도합니다...");
                // Load credentials from file
                bool loginSuccess = false;

                if (File.Exists("login.dat"))
                {
                    try
                    {
                        var lines = File.ReadAllLines("login.dat");
                        // [Security Fix] Check version header
                        if (lines.Length >= 3 && lines[0] == "KYMOTE_SEC_V1") 
                        {
                            string decEmail = System.Text.Encoding.UTF8.GetString(System.Security.Cryptography.ProtectedData.Unprotect(Convert.FromBase64String(lines[1]), null, System.Security.Cryptography.DataProtectionScope.CurrentUser));
                            string decPassword = System.Text.Encoding.UTF8.GetString(System.Security.Cryptography.ProtectedData.Unprotect(Convert.FromBase64String(lines[2]), null, System.Security.Cryptography.DataProtectionScope.CurrentUser));

                            if (!string.IsNullOrEmpty(decEmail))
                            {
                                var loginResult = await AttemptAutoLogin(appSettings, decEmail, decPassword);
                                if (!string.IsNullOrEmpty(loginResult.token))
                                {
                                    accessToken = loginResult.token;
                                    userId = loginResult.userId;
                                    userEmail = decEmail;
                                    loginSuccess = true;
                                }
                            }
                        }
                        else
                        {
                            Console.WriteLine("[Service] login.dat 버전이 호환되지 않거나 손상되었습니다. 파일을 삭제합니다.");
                            File.Delete("login.dat");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Service] login.dat 로드 오류: {ex.Message}");
                        try { File.Delete("login.dat"); } catch { }
                    }
                }
                
                if (!loginSuccess)
                {
                    Console.WriteLine("[Service] 자동 로그인 실패. GUI 모드로 실행하여 로그인 정보를 저장해주세요.");
                    return;
                }
            }
            else
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                var loginForm = new LoginForm(appSettings);
                var result = loginForm.ShowDialog();
                if (result != DialogResult.OK)
                {
                    return;
                }
                accessToken = loginForm.AccessToken;
                userEmail = loginForm.UserEmail;
                userId = loginForm.UserId;
            }

            Console.WriteLine($"[Auth] 로그인 성공: {userEmail}");

            Console.WriteLine("KYMOTE 호스트 시작 중...");

            string? hostName = null;
            string? password = null;
            int adapterIndex = 0;
            int outputIndex = 0;

            if (!isService)
            {
                // GUI 모드: SetupForm 표시
                using (var setupForm = new SetupForm())
                {
                    if (setupForm.ShowDialog() != DialogResult.OK)
                    {
                        Console.WriteLine("설정이 취소되었습니다. 종료합니다.");
                        return;
                    }
                    hostName = setupForm.HostName;
                    password = setupForm.Password;
                    adapterIndex = setupForm.SelectedAdapterIndex;
                    outputIndex = setupForm.SelectedOutputIndex;
                }
            }
            else
            {
                // 서비스 모드: appsettings.json 기본값 사용
                var serviceSettings = AppSettings.Load();
                hostName = serviceSettings.DefaultHostName ?? Environment.MachineName;
                password = serviceSettings.DefaultPassword;
                adapterIndex = 0;
                outputIndex = 0;
                Console.WriteLine("[Service] 비대화형(Non-interactive) 모드로 실행 중.");
            }

            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine("KYMOTE 호스트 초기화...");

            string ffmpegPath = FFmpegExtractor.ExtractFFmpeg();
            FFmpegInit.Initialise(FfmpegLogLevelEnum.AV_LOG_WARNING, ffmpegPath);

            // 설정 로드 (이미 로드됨)
            // var appSettings = AppSettings.Load(); // Duplicate removed
            string appKey = appSettings.Pusher.AppKey;
            
            if (string.IsNullOrEmpty(appKey))
            {
                Console.WriteLine("오류: Pusher AppKey가 누락되었습니다.");
                return;
            }

            string machineHash = BitConverter.ToString(
                System.Security.Cryptography.MD5.HashData(
                    System.Text.Encoding.UTF8.GetBytes(Environment.MachineName)))
                .Replace("-", "").Substring(0, 8).ToLower();
            string hostId = "host_" + machineHash; // Use stable machine hash instead of random GUID
            if (!string.IsNullOrEmpty(appSettings.HostId))
            {
                hostId = appSettings.HostId;
            }
            else
            {
                // Try to save, but if it fails, we still use the stable machineHash
                appSettings.HostId = hostId;
                appSettings.Save();
            }

            Console.WriteLine($"Host ID 식별: {hostId}");

            var capture = new ScreenCapture(adapterIndex, outputIndex);
            string resolution = $"{capture.Width}x{capture.Height}";
            
            // Signaling Client Start
            var signaling = new SignalingClient(appSettings.Pusher.AppId, appKey, appSettings.Pusher.Cluster, appSettings.WebAuthUrl, accessToken, hostId, hostName, resolution,
                appSettings.SupabaseUrl, appSettings.SupabaseAnonKey, userId);

            var webRtc = new WebRTCManager(capture, password);

            signaling.OnSignalReceived += async (from, signal) => await webRtc.HandleSignalAsync(from, signal);
            webRtc.OnSignalReady += async (to, signal) => await signaling.SendSignalAsync(to, signal);
            
            try 
            {
                await signaling.ConnectAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Main] 시그널링 서버 연결 실패: {ex}");
                // 서비스 모드일 경우 여기서 종료되면 안 될 수도 있음 (재시도 로직 필요)
            }

            await Task.Delay(-1);
        }

        static void InstallService()
        {
            string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (string.IsNullOrEmpty(exePath)) return;

            // sc.exe의 binPath 인자 형식: binPath= "C:\path\to\Host.exe"
            string cmd = $"create KymoteHost binPath= \"{exePath}\" start= auto DisplayName= \"KYMOTE Host Service\"";
            System.Diagnostics.Process.Start("sc.exe", cmd)?.WaitForExit();
            System.Diagnostics.Process.Start("sc.exe", "description KymoteHost \"KYMOTE Remote Control Host Service\"")?.WaitForExit();
            System.Diagnostics.Process.Start("sc.exe", "start KymoteHost")?.WaitForExit();
            Console.WriteLine("서비스가 설치 및 시작되었습니다.");
        }

        static void UninstallService()
        {
            System.Diagnostics.Process.Start("sc.exe", "stop KymoteHost")?.WaitForExit();
            System.Diagnostics.Process.Start("sc.exe", "delete KymoteHost")?.WaitForExit();
            Console.WriteLine("서비스가 중지 및 삭제되었습니다.");
        }
        private static async Task<(string token, string userId)> AttemptAutoLogin(AppSettings settings, string email, string password)
        {
            using (var client = new System.Net.Http.HttpClient())
            {
                try
                {
                    var url = $"{settings.SupabaseUrl}/auth/v1/token?grant_type=password";
                    var body = new { email = email, password = password };
                    var content = new System.Net.Http.StringContent(Newtonsoft.Json.JsonConvert.SerializeObject(body), System.Text.Encoding.UTF8, "application/json");
                    client.DefaultRequestHeaders.Add("apikey", settings.SupabaseAnonKey);

                    var response = await client.PostAsync(url, content);
                    if (response.IsSuccessStatusCode)
                    {
                        var responseString = await response.Content.ReadAsStringAsync();
                        dynamic json = Newtonsoft.Json.JsonConvert.DeserializeObject(responseString);
                        string token = json.access_token;
                        string uid = json.user?.id ?? "";
                        return (token, uid);
                    }
                    else
                    {
                        Console.WriteLine($"[AutoLogin] Failed. Status: {response.StatusCode}");
                        string errorBody = await response.Content.ReadAsStringAsync();
                        Console.WriteLine($"[AutoLogin] Error details: {errorBody}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[AutoLogin] Exception: {ex.Message}");
                }
            }
            return (null, null);
        }
    }
}
