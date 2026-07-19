namespace Viewer
{
    public partial class MainWindow
    {
        private bool _isLanMode;
        private string? _lanHost;
        private int _lanPort;
        private string? _lanPassword;
        private LanSignalClient? _lanSignal;

        public MainWindow(
            string accessToken,
            string userId,
            string lanHost,
            int lanPort,
            string lanPassword)
            : this(accessToken, userId, false, true)
        {
            _isLanMode = true;
            _lanHost = lanHost;
            _lanPort = lanPort;
            _lanPassword = lanPassword;
            Closed += (_, _) => _lanSignal?.Dispose();
        }
    }
}
