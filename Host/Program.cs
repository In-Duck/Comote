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
        private const string ServiceName = "KymoteHost";

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll")]
        private static extern bool GetConsoleMode(IntPtr handle, out uint mode);

        [DllImport("kernel32.dll")]
        private static extern bool SetConsoleMode(IntPtr handle, uint mode);

        [STAThread]
        private static async Task Main(string[] args)
        {
            if (args.Length > 0 && args[0].Equals("--install", StringComparison.OrdinalIgnoreCase))
            {
                InstallService();
                return;
            }

            if (args.Length > 0 && args[0].Equals("--uninstall", StringComparison.OrdinalIgnoreCase))
            {
                UninstallService();
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
            var isService = !Environment.UserInteractive || forceNoGui;

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

            string hostName;
            string? password;
            int adapterIndex;
            int outputIndex;

            if (isService)
            {
                hostName = appSettings.DefaultHostName ?? Environment.MachineName;
                password = appSettings.DefaultPassword;
                adapterIndex = 0;
                outputIndex = 0;
            }
            else
            {
                using var setupForm = new SetupForm();
                if (setupForm.ShowDialog() != DialogResult.OK) return;
                hostName = setupForm.HostName;
                password = setupForm.Password;
                adapterIndex = setupForm.SelectedAdapterIndex;
                outputIndex = setupForm.SelectedOutputIndex;
            }

            Console.OutputEncoding = Encoding.UTF8;
            var ffmpegPath = FFmpegExtractor.ExtractFFmpeg();
            FFmpegInit.Initialise(FfmpegLogLevelEnum.AV_LOG_WARNING, ffmpegPath);

            var hostId = ResolveHostId(appSettings);
            Console.WriteLine($"[Host] ID: {hostId}");

            var capture = new ScreenCapture(adapterIndex, outputIndex);
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
            var webRtc = new WebRTCManager(capture, password);

            signaling.OnSignalReceived +=
                async (from, signal) => await webRtc.HandleSignalAsync(from, signal);
            webRtc.OnSignalReady +=
                async (to, signal) => await signaling.SendSignalAsync(to, signal);

            await signaling.ConnectAsync();
            signaling.StartThumbnailReporting(capture);

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

        private static void InstallService()
        {
            var executablePath =
                Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (string.IsNullOrWhiteSpace(executablePath)) return;

            RunServiceControl(
                $"create {ServiceName} binPath= \"\\\"{executablePath}\\\" --nogui\" " +
                "start= auto DisplayName= \"Comote Host Service\"");
            RunServiceControl(
                $"description {ServiceName} \"Comote Remote Control Host Service\"");
            RunServiceControl($"start {ServiceName}");
        }

        private static void UninstallService()
        {
            RunServiceControl($"stop {ServiceName}");
            RunServiceControl($"delete {ServiceName}");
        }

        private static void RunServiceControl(string arguments)
        {
            using var process = Process.Start(
                new ProcessStartInfo
                {
                    FileName = "sc.exe",
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
            process?.WaitForExit();
        }
    }
}
