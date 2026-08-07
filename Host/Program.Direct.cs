using Comote.Input;
using System.Security.Cryptography;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows.Forms;
using SIPSorceryMedia.FFmpeg;

namespace Host
{
    internal static partial class Program
    {
        private static async Task RunDirectHostAsync(string[] args)
        {
            ConfigureConsole(false);
            Console.OutputEncoding = Encoding.UTF8;

            var portText =
                GetLanArgumentValue(args, "--port") ?? "45820";
            if (!int.TryParse(portText, out var port) ||
                port is < 1024 or > 65535)
            {
                MessageBox.Show(
                    "수신 포트는 1024~65535 범위여야 합니다.",
                    "Comote Direct Host");
                return;
            }

            var password = GetLanArgumentValue(args, "--access-key") ??
                GetLanArgumentValue(args, "--password");
            var adapterIndex = 0;
            var outputIndex = 0;
            if (string.IsNullOrWhiteSpace(password))
            {
                if (!Environment.UserInteractive)
                {
                    Console.WriteLine(
                        "[Direct] Service mode requires --access-key.");
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using var setup = new SetupForm();
                if (setup.ShowDialog() != DialogResult.OK) return;
                password = setup.Password;
                adapterIndex = setup.SelectedAdapterIndex;
                outputIndex = setup.SelectedOutputIndex;
            }

            if (!ComoteAccessKey.TryParse(password, out var parsedKey))
            {
                MessageBox.Show(
                    "직접 연결 접속 키는 CMT1 형식의 256비트 키여야 합니다.");
                return;
            }
            CryptographicOperations.ZeroMemory(parsedKey);

            Console.WriteLine("==============================================");
            Console.WriteLine("       COMOTE DIRECT HOST 1.4");
            Console.WriteLine("==============================================");
            Console.WriteLine($"TCP 수신 포트: {port}");
            foreach (var address in Dns.GetHostAddresses(
                         Dns.GetHostName()))
            {
                if (address.AddressFamily ==
                    AddressFamily.InterNetwork)
                {
                    Console.WriteLine($"내부 IP: {address}");
                }
            }
            Console.WriteLine(
                $"필수: 공유기 TCP {port} → 이 PC 내부 IP:{port} 포트포워딩");
            Console.WriteLine(
                $"필수: Windows 방화벽 인바운드 TCP {port} 허용");
            Console.WriteLine(
                "Supabase/Pusher/중앙 서버에는 접속하지 않습니다.");

            var ffmpegPath = FFmpegExtractor.ExtractFFmpeg();
            FFmpegInit.Initialise(
                FfmpegLogLevelEnum.AV_LOG_WARNING,
                ffmpegPath);

            using var capture =
                new ScreenCapture(adapterIndex, outputIndex);
            using var webRtc = new WebRTCManager(
                capture,
                inputBackend: InputBackendFactory.Create(
                    args,
                    capture.Left,
                    capture.Top,
                    capture.Width,
                    capture.Height));
            using var server = new LanSignalServer(port, password!);
            using var cancellation = new CancellationTokenSource();

            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };

            webRtc.OnSignalReady += async (_, signal) =>
                await server.SendSignalAsync(signal);

            await server.RunAsync(
                signal => webRtc.HandleSignalAsync(
                    "direct-manager",
                    signal),
                cancellation.Token);
        }
    }
}
