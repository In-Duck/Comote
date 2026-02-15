using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using SIPSorceryMedia.FFmpeg;

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
            }

            bool isService = !(Environment.UserInteractive);
            
            // [무인 업데이트] 시작 시 업데이트 체크
            await AutoUpdater.CheckAndApplyUpdate(isService);
            
            var handle = GetStdHandle(-10);
            if (GetConsoleMode(handle, out uint mode))
            {
                mode &= ~0x0040u;
                mode |= 0x0080u;
                SetConsoleMode(handle, mode);
            }

            string? hostName = null;
            string? password = null;
            int adapterIndex = 0;
            int outputIndex = 0;

            if (!isService)
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

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
            Console.WriteLine("Aion2 Comote Host Starting...");

            string ffmpegPath = FFmpegExtractor.ExtractFFmpeg();
            FFmpegInit.Initialise(FfmpegLogLevelEnum.AV_LOG_WARNING, ffmpegPath);

            // 설정 로드 (서비스 모드에서 이미 로드했더라도 Pusher 설정은 여기서 통합)
            var appSettings = AppSettings.Load();
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
            string hostId = "host_" + machineHash;

            var capture = new ScreenCapture(adapterIndex, outputIndex);
            string resolution = $"{capture.Width}x{capture.Height}";
            
            var signaling = new SignalingClient(appSettings.Pusher.AppId, appKey, appSettings.Pusher.Secret, appSettings.Pusher.Cluster, hostId, hostName, resolution);
            var webRtc = new WebRTCManager(capture, password);

            signaling.OnSignalReceived += async (from, signal) => await webRtc.HandleSignalAsync(from, signal);
            webRtc.OnSignalReady += async (to, signal) => await signaling.SendSignalAsync(to, signal);

            Console.WriteLine($"Host ID: {hostId}");
            await signaling.ConnectAsync();

            await Task.Delay(-1);
        }

        static void InstallService()
        {
            string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (string.IsNullOrEmpty(exePath)) return;

            // sc.exe의 binPath 인자 형식: binPath= "C:\path\to\Host.exe"
            string cmd = $"create ComoteHost binPath= \"{exePath}\" start= auto DisplayName= \"Comote Host Service\"";
            System.Diagnostics.Process.Start("sc.exe", cmd)?.WaitForExit();
            System.Diagnostics.Process.Start("sc.exe", "description ComoteHost \"Aion2 Comote Remote Control Host Service\"")?.WaitForExit();
            System.Diagnostics.Process.Start("sc.exe", "start ComoteHost")?.WaitForExit();
            Console.WriteLine("Service installed and started.");
        }

        static void UninstallService()
        {
            System.Diagnostics.Process.Start("sc.exe", "stop ComoteHost")?.WaitForExit();
            System.Diagnostics.Process.Start("sc.exe", "delete ComoteHost")?.WaitForExit();
            Console.WriteLine("Service stopped and deleted.");
        }
    }
}
