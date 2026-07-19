using System.Windows;
using System.Windows.Controls;

namespace Viewer
{
    public partial class MainWindow
    {
        private Window? _remoteWindow;
        private bool _closingRemoteWindow;

        private void ShowRemoteControlWindow(string hostId)
        {
            if (_remoteWindow == null)
            {
                if (_remoteGrid.Parent is Panel parent)
                    parent.Children.Remove(_remoteGrid);

                _remoteWindow = new Window
                {
                    Title = $"Comote Viewer - {hostId}",
                    Width = Math.Max(960, ActualWidth * 0.72),
                    Height = Math.Max(640, ActualHeight * 0.72),
                    MinWidth = 720,
                    MinHeight = 480,
                    Owner = this,
                    Content = _remoteGrid,
                };
                _remoteWindow.Activated += (_, _) => UpdateInputCaptureState();
                _remoteWindow.Deactivated += (_, _) => ReleaseRemoteInputs();
                _remoteWindow.Closed += (_, _) =>
                {
                    if (_remoteWindow?.Content == _remoteGrid)
                        _remoteWindow.Content = null;
                    _remoteWindow = null;
                    if (!_closingRemoteWindow)
                        DisconnectAndReturnToLobby();
                    _closingRemoteWindow = false;
                };
                _remoteWindow.Show();
            }
            else
            {
                _remoteWindow.Title = $"Comote Viewer - {hostId}";
                _remoteWindow.Activate();
            }

            _remoteGrid.Visibility = Visibility.Visible;
            _lobbyGrid.Visibility = Visibility.Visible;
            UpdateInputCaptureState();
        }

        private void CloseRemoteControlWindow()
        {
            if (_remoteWindow == null) return;
            _closingRemoteWindow = true;
            _remoteWindow.Close();
        }
    }
}
