using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using SIPSorceryMedia.FFmpeg;

namespace Host
{
    internal static partial class Program
    {
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll")]
        private static extern bool GetConsoleMode(IntPtr handle, out uint mode);

        [DllImport("kernel32.dll")]
        private static extern bool SetConsoleMode(IntPtr handle, uint mode);

        [STAThread]
        private static async Task Main(string[] args)
        {
            if (!Environment.UserInteractive)
            {
                throw new InvalidOperationException(
                    "Comote Client must run in an interactive user session. " +
                    "Only Comote.InputBroker may run as a Windows service.");
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

            await AutoUpdater.CheckAndApplyUpdate(false);
            ConfigureConsole(false);

            var appSettings = AppSettings.Load();
            var configurationErrors = appSettings.GetConfigurationErrors();
            if (configurationErrors.Count > 0)
            {
                Console.WriteLine(
                    "[Settings] Missing or invalid values: " +
                    string.Join(", ", configurationErrors));
                MessageBox.Show(
                    "Comote Client 설정이 필요합니다.\n" +
                    string.Join("\n", configurationErrors) +
                    $"\n\n설정 파일: {AppSettings.SettingsFilePath}",
                    "Comote Client 설정",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            var auth = AuthenticateInteractive(appSettings);
            if (auth == null) return;

            var (accessToken, userId, userEmail) = auth.Value;
            Console.WriteLine($"[Auth] Authenticated: {userEmail}");

            using var setupForm = new SetupForm();
            if (setupForm.ShowDialog() != DialogResult.OK) return;
            var hostName = setupForm.HostName;
            var password = setupForm.Password;
            var adapterIndex = setupForm.SelectedAdapterIndex;
            var outputIndex = setupForm.SelectedOutputIndex;

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
            var webRtc = new WebRTCManager(
                capture,
                password,
                InputBackendFactory.Create(
                    args,
                    capture.Left,
                    capture.Top,
                    capture.Width,
                    capture.Height));

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

            return (
                loginForm.AccessToken,
                loginForm.UserId,
                loginForm.UserEmail);
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
            if (isService)
            {
                throw new InvalidOperationException(
                    "Comote Client cannot run its capture or network stack as a service.");
            }

            try
            {
                Console.Title = "Comote Client";
                Console.BackgroundColor = ConsoleColor.Black;
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.Clear();
                Console.WriteLine("==============================================");
                var version =
                    typeof(Program).Assembly.GetName().Version?.ToString() ??
                    "unknown";
                Console.WriteLine($"          COMOTE CLIENT {version}");
                Console.WriteLine("==============================================");
            }
            catch
            {
                // A console is optional for GUI mode.
            }

            var handle = GetStdHandle(-10);
            if (GetConsoleMode(handle, out var mode))
            {
                SetConsoleMode(handle, mode | 0x0040u | 0x0080u);
            }
        }
    }
}
