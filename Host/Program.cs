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
            
            var handle = GetStdHandle(-10);
            if (GetConsoleMode(handle, out uint mode))
            {
                mode &= ~0x0040u;
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
                Console.WriteLine("[Service] Running in headless mode. Attempting auto-login...");
                // Load credentials from file
                if (File.Exists("login.dat"))
                {
                    var lines = File.ReadAllLines("login.dat");
                    if (lines.Length >= 2)
                    {
                         // Simple auto-login logic (re-using LoginForm logic or duplicating it)
                         // For simplicity, let's instantiate LoginForm but not show it? 
                         // No, better to extract login logic. But for now, let's just do a quick manual request here or use a helper.
                         // Or just use LoginForm hidden? No, WinForms specific.
                         // Let's implement a simple non-GUI login helper here or inside Program.
                         var loginResult = await AttemptAutoLogin(appSettings, lines[0], lines[1]);
                         accessToken = loginResult.token;
                         userId = loginResult.userId;
                         userEmail = lines[0];
                    }
                }
                
                if (string.IsNullOrEmpty(accessToken))
                {
                    Console.WriteLine("[Service] Auto-login failed. Please run with GUI once to save credentials.");
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

            Console.WriteLine($"[Auth] Logged in as: {userEmail}");

            Console.WriteLine("Comote Host Starting...");

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
                        Console.WriteLine("Setup cancelled. Exiting.");
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
                Console.WriteLine("[Service] Running in non-interactive mode.");
            }

            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine("Comote Host Starting...");

            string ffmpegPath = FFmpegExtractor.ExtractFFmpeg();
            FFmpegInit.Initialise(FfmpegLogLevelEnum.AV_LOG_WARNING, ffmpegPath);

            // 설정 로드 (이미 로드됨)
            // var appSettings = AppSettings.Load(); // Duplicate removed
            string appKey = appSettings.Pusher.AppKey;
            
            if (string.IsNullOrEmpty(appKey))
            {
                Console.WriteLine("Error: Pusher AppKey is missing.");
                return;
            }

            string machineHash = BitConverter.ToString(
                System.Security.Cryptography.MD5.HashData(
                    System.Text.Encoding.UTF8.GetBytes(Environment.MachineName)))
                .Replace("-", "").Substring(0, 8).ToLower();
            string hostId = "host_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            if (!string.IsNullOrEmpty(appSettings.HostId))
            {
                hostId = appSettings.HostId;
            }
            else
            {
                // Save generated HostId
                appSettings.HostId = hostId;
                appSettings.Save();
            }

            Console.WriteLine($"Host ID: {hostId}");

            var capture = new ScreenCapture(adapterIndex, outputIndex);
            string resolution = $"{capture.Width}x{capture.Height}";
            
            // Signaling Client Start
            // Old: _signaling = new SignalingClient(appSettings.PusherAppId, appSettings.PusherAppKey, appSettings.PusherSecret, appSettings.PusherCluster, hostId, new { name = hostName, password = password });
            // New: HttpAuthorizer via WebAuthUrl
            var signaling = new SignalingClient(appSettings.Pusher.AppId, appKey, appSettings.Pusher.Cluster, appSettings.WebAuthUrl, accessToken, hostId, hostName, resolution,
                appSettings.SupabaseUrl, appSettings.SupabaseAnonKey, userId);

            var webRtc = new WebRTCManager(capture, password);

            signaling.OnSignalReceived += async (from, signal) => await webRtc.HandleSignalAsync(from, signal);
            webRtc.OnSignalReady += async (to, signal) => await signaling.SendSignalAsync(to, signal);

            Console.WriteLine($"Host ID: {hostId}");
            await signaling.ConnectAsync();
            // await signaling.SubscribeHostChannel(hostId); // Removed as it is handled in ConnectAsync

            await Task.Delay(-1);
        }

        static void InstallService()
        {
            string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (string.IsNullOrEmpty(exePath)) return;

            // sc.exe의 binPath 인자 형식: binPath= "C:\path\to\Host.exe"
            string cmd = $"create ComoteHost binPath= \"{exePath}\" start= auto DisplayName= \"Comote Host Service\"";
            System.Diagnostics.Process.Start("sc.exe", cmd)?.WaitForExit();
            System.Diagnostics.Process.Start("sc.exe", "description ComoteHost \"Comote Remote Control Host Service\"")?.WaitForExit();
            System.Diagnostics.Process.Start("sc.exe", "start ComoteHost")?.WaitForExit();
            Console.WriteLine("Service installed and started.");
        }

        static void UninstallService()
        {
            System.Diagnostics.Process.Start("sc.exe", "stop ComoteHost")?.WaitForExit();
            System.Diagnostics.Process.Start("sc.exe", "delete ComoteHost")?.WaitForExit();
            Console.WriteLine("Service stopped and deleted.");
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
                }
                catch { }
            }
            return (null, null);
        }
    }
}
