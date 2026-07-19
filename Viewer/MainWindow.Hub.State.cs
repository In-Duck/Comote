namespace Viewer
{
    public partial class MainWindow
    {
        private bool _isHubMode;
        private int _hubPort;
        private string? _hubPassword;
        private ManagerHubServer? _hubServer;
        private CancellationTokenSource? _hubLifetime;
        private string? _hubAutoConnectId;

        public MainWindow(
            int hubPort,
            string hubPassword,
            string? autoConnectId = null)
            : this("hub", "hub-manager", false, true)
        {
            _isHubMode = true;
            _hubPort = hubPort;
            _hubPassword = hubPassword;
            _hubAutoConnectId = autoConnectId;
            Closed += (_, _) =>
            {
                _hubLifetime?.Cancel();
                _hubServer?.Dispose();
            };
        }
    }
}

