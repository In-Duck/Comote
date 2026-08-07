using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using SIPSorceryMedia.FFmpeg;
using Comote.Input;

namespace Host
{
    internal static partial class Program
    {
        private static async Task RunManagerClientAsync(string[] args)
        {
            ConfigureConsole(false);
            var forceSetup = args.Any(argument =>
                argument.Equals("--setup", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("--configure", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("--reset-device-id", StringComparison.OrdinalIgnoreCase));
            if (HasHubArgument(args, "--access-key") ||
                HasHubArgument(args, "--password") ||
                HasHubArgument(args, "--client-id"))
            {
                MessageBox.Show(
                    "Manager Hub 키와 기기 ID는 명령줄에서 받을 수 없습니다. " +
                    "--setup 화면 또는 Windows 보호 저장소를 사용하세요.",
                    "Comote Client");
                return;
            }

            var savedAccessKey = "";
            var savedSettings = forceSetup
                ? null
                : HubClientSettingsStore.TryLoad(out savedAccessKey);
            Console.OutputEncoding = Encoding.UTF8;

            var manager = GetHubArgument(args, "--manager") ??
                savedSettings?.ManagerAddress;
            var portText = GetHubArgument(args, "--port") ??
                savedSettings?.ManagerPort.ToString() ?? "45820";
            var accessKey = savedAccessKey;
            var clientName =
                GetHubArgument(args, "--name") ??
                savedSettings?.ClientName ?? Environment.MachineName;
            var adapterIndex = savedSettings?.AdapterIndex ?? 0;
            var outputIndex = savedSettings?.OutputIndex ?? 0;

            if (!int.TryParse(portText, out var port) ||
                port is < 1024 or > 65535)
            {
                MessageBox.Show(
                    "Manager 포트는 1024~65535 범위여야 합니다.");
                return;
            }

            var hasValidInitialKey =
                ComoteAccessKey.TryParse(accessKey, out var initialKey);
            try
            {
                if (forceSetup ||
                    string.IsNullOrWhiteSpace(manager) ||
                    !hasValidInitialKey)
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    using var setup = new HubClientSetupForm(savedSettings);
                    if (setup.ShowDialog() != DialogResult.OK) return;
                    manager = setup.ManagerAddress;
                    port = setup.ManagerPort;
                    accessKey = setup.AccessKey;
                    clientName = setup.ClientName;
                    adapterIndex = setup.AdapterIndex;
                    outputIndex = setup.OutputIndex;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(initialKey);
            }

            if (!ComoteAccessKey.TryParse(accessKey, out var parsedKey))
            {
                MessageBox.Show(
                    "Manager 접속 키는 CMT1 형식의 256비트 키여야 합니다.");
                return;
            }
            var routingId = ManagerHubProtocol.DeriveRoutingId(parsedKey);
            CryptographicOperations.ZeroMemory(parsedKey);
            var allowRemoteTasks = args.Any(argument =>
                argument.Equals(
                    "--allow-remote-tasks",
                    StringComparison.OrdinalIgnoreCase));
            var allowSystemCommands = args.Any(argument =>
                argument.Equals(
                    "--allow-system-commands",
                    StringComparison.OrdinalIgnoreCase));

            if (!HubClientSettingsStore.Save(
                    new HubClientSettings
                    {
                        ManagerAddress = manager,
                        ManagerPort = port,
                        ClientName = clientName,
                        AdapterIndex = adapterIndex,
                        OutputIndex = outputIndex,
                    },
                    accessKey))
            {
                MessageBox.Show(
                    "Manager Hub 연결 설정을 Windows 사용자 보호 저장소에 " +
                    "저장하지 못했습니다.",
                    "Comote Client");
                return;
            }

            Console.WriteLine("==============================================");
            Console.WriteLine("       COMOTE CLIENT 1.6 PREVIEW");
            Console.WriteLine("==============================================");
            Console.WriteLine($"Hub device ID: {routingId}");
            Console.WriteLine($"Manager: {manager}:{port}");
            Console.WriteLine("Client 측 포트포워딩: 필요 없음");

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
            using var hub = new ManagerHubClient(
                manager,
                port,
                accessKey,
                clientName,
                allowRemoteTasks,
                allowSystemCommands);
            using var cancellation = new CancellationTokenSource();
            using var tray = ClientTrayIcon.Start(
                manager,
                port,
                clientName,
                cancellation,
                _ => RestartForSetup(cancellation, args));

            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };
            hub.ConnectionChanged += (connected, message) =>
            {
                Console.WriteLine(
                    connected
                        ? $"[Hub] Manager connected: {message}"
                        : $"[Hub] Disconnected: {message}");
                tray.SetConnected(connected, message);
            };
            hub.SignalReceived += signal =>
                webRtc.HandleSignalAsync("hub-manager", signal);
            var commandExecutor = new RemoteTaskExecutor(
                new WindowsSystemCommandExecutor(
                    () => RestartForSetup(cancellation, args, keepSetup: false)),
                new Comote.Input.RemoteCommandDeduplicator(),
                Comote.Input.RemoteCommandAuditLog.Open("host"));
            hub.CommandReceived += async command =>
            {
                var result = await commandExecutor.ExecuteAsync(command);
                Console.WriteLine($"[Hub Command] {command.Value<string>("action")}: {result.Value<string>("message")}");
            };
            webRtc.OnSignalReady += async (_, signal) =>
                await hub.SendSignalAsync(signal);

            _ = RunThumbnailLoopAsync(
                webRtc, capture, hub, cancellation.Token);

            await hub.RunAsync(cancellation.Token);
        }

        private static bool RestartForSetup(
            CancellationTokenSource cancellation,
            IReadOnlyList<string> currentArguments,
            bool keepSetup = true)
        {
            try
            {
                var backend = InputBackendFactory.ResolveMode(
                    currentArguments,
                    Environment.GetEnvironmentVariable(
                        InputBackendFactory.EnvironmentVariableName));
                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = Environment.ProcessPath!,
                    UseShellExecute = true,
                };
                startInfo.ArgumentList.Add("--manager-hub");
                if (keepSetup)
                {
                    startInfo.ArgumentList.Add("--setup");
                }
                startInfo.ArgumentList.Add(
                    backend == InputBackendMode.VirtualHid
                        ? "--virtual-hid"
                        : "--send-input");
                if (!keepSetup)
                {
                    // A restart-host command must not silently drop the
                    // permissions the operator launched this Client with.
                    if (HasHubArgument(
                            currentArguments,
                            "--allow-remote-tasks"))
                    {
                        startInfo.ArgumentList.Add("--allow-remote-tasks");
                    }
                    if (HasHubArgument(
                            currentArguments,
                            "--allow-system-commands"))
                    {
                        startInfo.ArgumentList.Add("--allow-system-commands");
                    }
                }
                var started = System.Diagnostics.Process.Start(startInfo);
                if (started is null)
                {
                    return false;
                }
                cancellation.Cancel();
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Client] Setup restart failed: {ex.Message}");
                return false;
            }
        }

        private static bool HasHubArgument(
            IEnumerable<string> args,
            string name) =>
            args.Any(argument => argument.Equals(
                name,
                StringComparison.OrdinalIgnoreCase));

        private static string? GetHubArgument(
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

        private static async Task RunThumbnailLoopAsync(
            WebRTCManager webRtc,
            ScreenCapture capture,
            ManagerHubClient hub,
            CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(2),
                    cancellationToken);
                using var timer = new PeriodicTimer(
                    TimeSpan.FromSeconds(5));
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (webRtc.IsSessionActive)
                    {
                        await timer.WaitForNextTickAsync(cancellationToken);
                        continue;
                    }

                    var jpeg = capture.CaptureThumbnail(300, 45);
                    if (jpeg != null)
                    {
                        try
                        {
                            await hub.SendThumbnailAsync(jpeg);
                        }
                        catch (IOException)
                        {
                        }
                    }
                    await timer.WaitForNextTickAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}
