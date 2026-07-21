using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SIPSorceryMedia.FFmpeg;

namespace Host
{
    internal static partial class Program
    {
        private const string LegacyServiceName = "KymoteHost";

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll")]
        private static extern bool GetConsoleMode(IntPtr handle, out uint mode);

        [DllImport("kernel32.dll")]
        private static extern bool SetConsoleMode(IntPtr handle, uint mode);

        [STAThread]
        private static async Task Main(string[] args)
        {
            if (args.Any(argument => argument.Equals(
                    "--service", StringComparison.OrdinalIgnoreCase)))
            {
                await SecureDesktopService.RunAsync(args);
                return;
            }

            var systemAgent = args.Any(argument => argument.Equals(
                "--system-agent", StringComparison.OrdinalIgnoreCase));
            if (systemAgent && !SecureDesktopService.IsSystemAgent())
            {
                Console.WriteLine("[Security] SYSTEM agent launch rejected.");
                return;
            }
            if (args.Length > 0 && args[0].Equals("--install", StringComparison.OrdinalIgnoreCase))
            {
                Environment.ExitCode = InstallService() ? 0 : 1;
                return;
            }

            if (args.Length > 0 && args[0].Equals("--uninstall", StringComparison.OrdinalIgnoreCase))
            {
                Environment.ExitCode = UninstallService() ? 0 : 1;
                return;
            }

            var listenDirect = Array.Exists(
                args,
                argument => argument.Equals(
                    "--listen-direct",
                    StringComparison.OrdinalIgnoreCase));
            if (listenDirect)
            {
                await RunDirectHostAsync(args);
                return;
            }

            var managerHubMode = Array.Exists(
                args,
                argument => argument.Equals(
                    "--manager-hub",
                    StringComparison.OrdinalIgnoreCase));
            if (managerHubMode)
            {
                await RunManagerClientAsync(args);
                return;
            }

            var forceNoGui = Array.Exists(
                args,
                argument => argument.Equals("--nogui", StringComparison.OrdinalIgnoreCase));
            var isService = systemAgent || !Environment.UserInteractive || forceNoGui;

            if (!systemAgent)
                await AutoUpdater.CheckAndApplyUpdate(isService);
            ConfigureConsole(isService);

            var appSettings = AppSettings.Load();
            var configurationErrors = appSettings.GetConfigurationErrors();
            if (configurationErrors.Count > 0)
            {
                Console.WriteLine(
                    "[Settings] Missing or invalid values: " +
                    string.Join(", ", configurationErrors));
                if (!isService)
                {
                    MessageBox.Show(
                        "서버 설정이 필요합니다.\n" +
                        string.Join("\n", configurationErrors) +
                        $"\n\n설정 파일: {AppSettings.SettingsFilePath}",
                        "Comote Host 설정",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
                return;
            }

            var auth = isService
                ? await AuthenticateServiceAsync(appSettings)
                : AuthenticateInteractive(appSettings);
            if (auth == null) return;

            var (accessToken, userId, userEmail) = auth.Value;
            Console.WriteLine($"[Auth] Authenticated: {userEmail}");

            var hostName = appSettings.DefaultHostName ?? Environment.MachineName;
            string? password = null;
            var adapterIndex = 0;
            var outputIndex = 0;
            var inputBackendMode = appSettings.InputBackendMode;
            var showAdvancedSetup = !isService && Array.Exists(
                args,
                argument => argument.Equals("--setup", StringComparison.OrdinalIgnoreCase));

            if (showAdvancedSetup)
            {
                using var setupForm = new SetupForm(appSettings.InputBackendMode);
                if (setupForm.ShowDialog() != DialogResult.OK) return;
                hostName = setupForm.HostName;
                adapterIndex = setupForm.SelectedAdapterIndex;
                outputIndex = setupForm.SelectedOutputIndex;
                inputBackendMode = setupForm.SelectedInputBackendMode;
                appSettings.DefaultHostName = hostName;
                appSettings.InputBackendMode = inputBackendMode;
                appSettings.Save();
            }

            var resolvedInputBackendMode =
                InputBackendFactory.ResolveConfiguredMode(
                    inputBackendMode,
                    null,
                    allowInstall: !isService);
            if (resolvedInputBackendMode != inputBackendMode)
            {
                inputBackendMode = resolvedInputBackendMode;
                appSettings.InputBackendMode = inputBackendMode;
                appSettings.Save();
            }
            if (!isService && SecureDesktopService.IsInstalled())
            {
                var restarted = SecureDesktopService.Restart();
                MessageBox.Show(
                    restarted
                        ? "설정과 로그인 정보가 저장되었습니다. Comote는 보안 화면 서비스로 백그라운드에서 실행됩니다."
                        : "설정은 저장했지만 Comote 보안 화면 서비스를 시작하지 못했습니다.",
                    "Comote",
                    MessageBoxButtons.OK,
                    restarted ? MessageBoxIcon.Information : MessageBoxIcon.Error);
                return;
            }

            Console.WriteLine(
                "[Host] Cloud account mode enabled; no VPN address or connection password is required.");

            Console.OutputEncoding = Encoding.UTF8;
            var ffmpegPath = FFmpegExtractor.ExtractFFmpeg();
            FFmpegInit.Initialise(FfmpegLogLevelEnum.AV_LOG_WARNING, ffmpegPath);

            var hostId = ResolveHostId(appSettings);
            Console.WriteLine($"[Host] ID: {hostId}");

            using var capture = new ScreenCapture(adapterIndex, outputIndex);
            var resolution = $"{capture.Width}x{capture.Height}";
            var signaling = new SignalingClient(
                appSettings.Pusher.AppKey,
                appSettings.Pusher.Cluster,
                appSettings.WebAuthUrl,
                accessToken,
                hostId,
                hostName,
                resolution,
                appSettings.SupabaseUrl,
                appSettings.SupabaseAnonKey,
                userId);
            var inputBackend = InputBackendFactory.Create(
                inputBackendMode, capture);
            using var webRtc = new WebRTCManager(
                capture, password, inputBackend);

            signaling.OnSignalReceived +=
                async (from, signal) => await webRtc.HandleSignalAsync(from, signal);
            webRtc.OnSignalReady +=
                async (to, signal) => await signaling.SendSignalAsync(to, signal);

            await signaling.ConnectAsync();
            signaling.StartThumbnailReporting(capture);
            using var tray = isService
                ? null
                : CloudClientTrayIcon.Start(hostName, inputBackend.Mode);

            await Task.Delay(Timeout.InfiniteTimeSpan);
        }

        private static (string AccessToken, string UserId, string UserEmail)?
            AuthenticateInteractive(AppSettings settings)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using var loginForm = new LoginForm(settings);
            if (loginForm.ShowDialog() != DialogResult.OK) return null;

            if (!string.IsNullOrWhiteSpace(loginForm.RefreshToken))
            {
                ServiceCredentialStore.Save(loginForm.RefreshToken);
            }

            return (
                loginForm.AccessToken,
                loginForm.UserId,
                loginForm.UserEmail);
        }

        private static async Task<(string AccessToken, string UserId, string UserEmail)?>
            AuthenticateServiceAsync(AppSettings settings)
        {
            if (!ServiceCredentialStore.TryLoad(out var refreshToken))
            {
                Console.WriteLine(
                    "[ServiceAuth] No device refresh token. " +
                    "Run Host as administrator and sign in once.");
                return null;
            }

            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                var url =
                    $"{settings.SupabaseUrl.TrimEnd('/')}/auth/v1/token?grant_type=refresh_token";
                using var content = new StringContent(
                    JsonConvert.SerializeObject(new { refresh_token = refreshToken }),
                    Encoding.UTF8,
                    "application/json");
                client.DefaultRequestHeaders.Add("apikey", settings.SupabaseAnonKey);

                using var response = await client.PostAsync(url, content);
                var responseBody = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[ServiceAuth] Refresh failed: {response.StatusCode}");
                    return null;
                }

                var json = JObject.Parse(responseBody);
                var accessToken = json.Value<string>("access_token");
                var nextRefreshToken = json.Value<string>("refresh_token");
                var userId = json["user"]?.Value<string>("id");
                var email = json["user"]?.Value<string>("email") ?? "service";

                if (
                    string.IsNullOrWhiteSpace(accessToken) ||
                    string.IsNullOrWhiteSpace(userId))
                {
                    return null;
                }

                if (!string.IsNullOrWhiteSpace(nextRefreshToken))
                    ServiceCredentialStore.Save(nextRefreshToken);

                return (accessToken, userId, email);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ServiceAuth] Refresh error: {ex.Message}");
                return null;
            }
        }

        private static string ResolveHostId(AppSettings settings)
        {
            if (!string.IsNullOrWhiteSpace(settings.HostId))
                return settings.HostId;

            settings.HostId = DeviceIdentityStore.GetOrCreate();
            settings.Save();
            return settings.HostId;
        }

        private static void ConfigureConsole(bool isService)
        {
            if (!isService)
            {
                try
                {
                    Console.Title = "Comote Host";
                    Console.BackgroundColor = ConsoleColor.Black;
                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                    Console.Clear();
                    Console.WriteLine("==============================================");
                    Console.WriteLine("          COMOTE HOST v1.3.0");
                    Console.WriteLine("==============================================");
                }
                catch
                {
                    // A console is optional for GUI mode.
                }
            }

            var handle = GetStdHandle(-10);
            if (GetConsoleMode(handle, out var mode))
            {
                SetConsoleMode(handle, mode | 0x0040u | 0x0080u);
            }
        }

        private static bool InstallService()
        {
            var executablePath =
                Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (string.IsNullOrWhiteSpace(executablePath)) return false;

            RunServiceControl("stop", LegacyServiceName);
            RunServiceControl("delete", LegacyServiceName);

            var service = SecureDesktopService.ServiceName;
            var binaryPath = $"\"{executablePath}\" --service";
            var exists = RunServiceControl("query", service) == 0;
            if (exists)
            {
                RunServiceControl("stop", service);
                Thread.Sleep(1000);
            }
            var configured = exists
                ? RunServiceControl(
                    "config", service, "binPath=", binaryPath,
                    "start=", "auto", "obj=", "LocalSystem")
                : RunServiceControl(
                    "create", service, "binPath=", binaryPath,
                    "start=", "auto", "obj=", "LocalSystem",
                    "DisplayName=", "Comote Secure Desktop Service");
            RunServiceControl(
                "description", service,
                "Comote authenticated remote control and secure desktop service");
            RunServiceControl("sidtype", service, "unrestricted");
            RunServiceControl(
                "failure", service, "reset=", "86400",
                "actions=", "restart/5000/restart/15000/\"\"/0");
            var started = configured == 0 &&
                RunServiceControl("start", service) == 0;

            if (Environment.UserInteractive)
            {
                MessageBox.Show(
                    started
                        ? "Comote 보안 화면 서비스가 설치되어 실행 중입니다."
                        : "서비스 설치 또는 시작에 실패했습니다. 관리자 권한과 Windows 이벤트 로그를 확인해 주세요.",
                    "Comote 보안 화면 서비스",
                    MessageBoxButtons.OK,
                    started ? MessageBoxIcon.Information : MessageBoxIcon.Error);
            }
            return started;
        }

        private static bool UninstallService()
        {
            RunServiceControl("stop", SecureDesktopService.ServiceName);
            var removed = RunServiceControl(
                "delete", SecureDesktopService.ServiceName) == 0;
            RunServiceControl("stop", LegacyServiceName);
            RunServiceControl("delete", LegacyServiceName);
            if (Environment.UserInteractive)
            {
                MessageBox.Show(
                    removed
                        ? "Comote 보안 화면 서비스를 제거했습니다."
                        : "서비스가 설치되어 있지 않거나 제거하지 못했습니다.",
                    "Comote 보안 화면 서비스");
            }
            return removed;
        }

        internal static int RunServiceControl(params string[] arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "sc.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);
            using var process = Process.Start(startInfo);
            process?.WaitForExit();
            return process?.ExitCode ?? -1;
        }
    }
}
