using System.Text;
using System.Windows.Forms;
using SIPSorceryMedia.FFmpeg;

namespace Host
{
    internal static partial class Program
    {
        private static async Task RunLanTestAsync(string[] args)
        {
            ConfigureConsole(false);
            var portText = GetLanArgumentValue(args, "--port") ?? "45820";
            if (!int.TryParse(portText, out var port) ||
                port is < 1024 or > 65535)
            {
                MessageBox.Show(
                    "LAN 포트는 1024~65535 범위여야 합니다.",
                    "Comote LAN Host");
                return;
            }

            var password = GetLanArgumentValue(args, "--password");
            if (string.IsNullOrWhiteSpace(password) ||
                password.Length < 8)
            {
                MessageBox.Show(
                    "LAN 테스트 암호는 8자 이상으로 지정해 주세요.\n\n" +
                    "예: ComoteHost.exe --lan-test --port 45820 " +
                    "--password \"테스트암호\"",
                    "Comote LAN Host");
                return;
            }

            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine("Comote LAN Host 1.4 Demo");
            Console.WriteLine($"Port: {port}");
            Console.WriteLine(
                "Windows 방화벽에서 이 앱의 개인 네트워크 통신을 허용하세요.");

            var ffmpegPath = FFmpegExtractor.ExtractFFmpeg();
            FFmpegInit.Initialise(
                FfmpegLogLevelEnum.AV_LOG_WARNING,
                ffmpegPath);

            using var capture = new ScreenCapture(0, 0);
            using var webRtc = new WebRTCManager(capture);
            using var server = new LanSignalServer(port, password);
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
                    "lan-manager",
                    signal),
                cancellation.Token);
        }

        private static string? GetLanArgumentValue(
            string[] args,
            string name)
        {
            for (var index = 0; index < args.Length - 1; index++)
            {
                if (args[index].Equals(
                    name,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return args[index + 1];
                }
            }

            return null;
        }
    }
}
