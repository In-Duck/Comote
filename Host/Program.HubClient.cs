using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using SIPSorceryMedia.FFmpeg;

namespace Host
{
    internal static partial class Program
    {
        private static async Task RunManagerClientAsync(string[] args)
        {
            ConfigureConsole(false);
            var forceSetup = args.Any(argument =>
                argument.Equals("--setup", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("--configure", StringComparison.OrdinalIgnoreCase));
            var resetDeviceId = args.Any(argument =>
                argument.Equals("--reset-device-id", StringComparison.OrdinalIgnoreCase));
            if (resetDeviceId) DeviceIdentityStore.Reset();
            var savedPassword = "";
            var savedSettings = forceSetup
                ? null
                : HubClientSettingsStore.TryLoad(out savedPassword);
            var updateManifest =
                GetHubArgument(args, "--update-manifest") ??
                Environment.GetEnvironmentVariable(
                    "COMOTE_UPDATE_MANIFEST_URL") ??
                savedSettings?.UpdateManifestUrl;
            Console.OutputEncoding = Encoding.UTF8;

            var manager = GetHubArgument(args, "--manager") ??
                savedSettings?.ManagerAddress;
            var portText = GetHubArgument(args, "--port") ??
                savedSettings?.ManagerPort.ToString() ?? "45820";
            var password = GetHubArgument(args, "--password") ??
                savedPassword;
            var clientName =
                GetHubArgument(args, "--name") ??
                savedSettings?.ClientName ?? Environment.MachineName;
            var adapterIndex = savedSettings?.AdapterIndex ?? 0;
            var outputIndex = savedSettings?.OutputIndex ?? 0;
            var inputBackendMode = savedSettings?.InputBackendMode ??
                InputBackendMode.SendInput;

            if (!int.TryParse(portText, out var port) ||
                port is < 1024 or > 65535)
            {
                MessageBox.Show(
                    "Manager 포트는 1024~65535 범위여야 합니다.");
                return;
            }

            if (forceSetup || string.IsNullOrWhiteSpace(manager) ||
                string.IsNullOrWhiteSpace(password))
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using var setup = new HubClientSetupForm(
                    savedSettings);
                if (setup.ShowDialog() != DialogResult.OK) return;
                manager = setup.ManagerAddress;
                port = setup.ManagerPort;
                updateManifest = setup.UpdateManifestUrl;
                password = setup.AccessPassword;
                clientName = setup.ClientName;
                adapterIndex = setup.AdapterIndex;
                outputIndex = setup.OutputIndex;
                inputBackendMode = setup.SelectedInputBackendMode;
            }

            if (password.Length < 8)
            {
                MessageBox.Show(
                    "Manager 등록 암호는 최소 8자 이상이어야 합니다.");
                return;
            }

            inputBackendMode = InputBackendFactory.ResolveConfiguredMode(
                inputBackendMode,
                null,
                allowInstall: Environment.UserInteractive);
            HubClientSettingsStore.Save(
                new HubClientSettings
                {
                    ManagerAddress = manager,
                    ManagerPort = port,
                    ClientName = clientName,
                    AdapterIndex = adapterIndex,
                    OutputIndex = outputIndex,
                    InputBackendMode = inputBackendMode,
                    UpdateManifestUrl = updateManifest ?? "",
                },
                password);

            if (!string.IsNullOrWhiteSpace(updateManifest) &&
                await ClientAutoUpdater.TryStageUpdateAsync(
                    updateManifest, args))
                return;

            var clientId =
                GetHubArgument(args, "--client-id") ??
                DeviceIdentityStore.GetOrCreate();

            Console.WriteLine("==============================================");
            Console.WriteLine("       COMOTE CLIENT 1.6 PREVIEW");
            Console.WriteLine("==============================================");
            Console.WriteLine($"Client ID: {clientId}");
            Console.WriteLine($"Manager: {manager}:{port}");
            Console.WriteLine("Client 측 포트포워딩: 필요 없음");

            var ffmpegPath = FFmpegExtractor.ExtractFFmpeg();
            FFmpegInit.Initialise(
                FfmpegLogLevelEnum.AV_LOG_WARNING,
                ffmpegPath);
            using var capture =
                new ScreenCapture(adapterIndex, outputIndex);
            using var inputBackend = InputBackendFactory.Create(
                inputBackendMode, capture);
            using var webRtc = new WebRTCManager(
                capture, inputBackend: inputBackend);
            using var hub = new ManagerHubClient(
                manager,
                port,
                password,
                clientId,
                clientName);
            using var cancellation = new CancellationTokenSource();
            using var tray = ClientTrayIcon.Start(
                manager,
                port,
                clientName,
                cancellation,
                reset => RestartForSetup(cancellation, reset));

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
            hub.CommandReceived += async command =>
            {
                var result = await RemoteTaskExecutor.ExecuteAsync(command);
                Console.WriteLine($"[Hub Command] {command.Value<string>("action")}: {result.Value<string>("message")}");
            };
            webRtc.OnSignalReady += async (_, signal) =>
                await hub.SendSignalAsync(signal);

            _ = RunThumbnailLoopAsync(
                webRtc, capture, hub, cancellation.Token);

            await hub.RunAsync(cancellation.Token);
        }

        private static void RestartForSetup(
            CancellationTokenSource cancellation,
            bool resetDeviceId)
        {
            try
            {
                var arguments = resetDeviceId
                    ? "--setup --reset-device-id"
                    : "--setup";
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = Environment.ProcessPath!,
                        Arguments = arguments,
                        UseShellExecute = true,
                    });
                cancellation.Cancel();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Client] Setup restart failed: {ex.Message}");
            }
        }
        private static string? GetHubArgument(
            string[] args,
            string name)
        {
            for (var index = 0; index < args.Length - 1; index++)
            {
                if (args[index].Equals(
                    name,
                    StringComparison.OrdinalIgnoreCase))
                    return args[index + 1];
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
