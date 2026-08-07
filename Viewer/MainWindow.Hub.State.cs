namespace Viewer
{
    public partial class MainWindow
    {
        private bool _isHubMode;
        private int _hubPort;
        private IReadOnlyList<ManagerHubDeviceCredential> _hubCredentials =
            Array.Empty<ManagerHubDeviceCredential>();
        private ManagerHubServer? _hubServer;
        private CancellationTokenSource? _hubLifetime;
        private string? _hubAutoConnectId;
        private readonly SemaphoreSlim _hubSignalDispatchGate = new(1, 1);
        private bool _hubClosing;

        internal MainWindow(
            int hubPort,
            IEnumerable<ManagerHubDeviceCredential> hubCredentials,
            string? autoConnectId = null)
            : this("hub", "hub-manager", false, true)
        {
            ArgumentNullException.ThrowIfNull(hubCredentials);
            var credentials = hubCredentials.ToArray();
            if (credentials.Length == 0)
            {
                throw new ArgumentException(
                    "At least one Manager Hub device key is required.",
                    nameof(hubCredentials));
            }

            _isHubMode = true;
            _hubPort = hubPort;
            _hubCredentials = credentials;
            _hubAutoConnectId = autoConnectId;
            Closed += (_, _) =>
            {
                _hubClosing = true;
                _hubLifetime?.Cancel();
                _hubServer?.Dispose();
            };
        }
    }
}
