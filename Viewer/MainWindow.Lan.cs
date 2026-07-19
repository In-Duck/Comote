using System.Windows;
using System.Windows.Media;

namespace Viewer
{
    public partial class MainWindow
    {
        private async Task StartLanModeAsync()
        {
            Title = $"Comote Direct Manager - {_lanHost}:{_lanPort}";
            _lobbyGrid.Visibility = Visibility.Collapsed;
            _remoteGrid.Visibility = Visibility.Visible;
            _connectedHostId = "lan-host";
            _statusText.Text =
                $"Client {_lanHost}:{_lanPort}에 직접 연결 중...";
            _statusText.Foreground = new SolidColorBrush(
                Color.FromRgb(56, 189, 248));
            _statusText.Visibility = Visibility.Visible;
            _statsOverlay.Visibility = Visibility.Collapsed;

            try
            {
                _lanSignal = new LanSignalClient();
                _lanSignal.SignalReceived += async signal =>
                {
                    if (_receiver != null)
                    {
                        await _receiver.HandleSignalAsync(signal);
                    }
                };
                _lanSignal.Disconnected += reason =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        _statusText.Text = $"직접 연결 종료: {reason}";
                        _statusText.Foreground = Brushes.Red;
                        _statusText.Visibility = Visibility.Visible;
                    });
                };

                using var connectionTimeout =
                    new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await _lanSignal.ConnectAsync(
                    _lanHost!,
                    _lanPort,
                    _lanPassword!,
                    connectionTimeout.Token);

                InitializeReceiver("lan-host");
                UpdateInputCaptureState();
                await _receiver!.StartAsync();
                _statusBarText.Text =
                    $"DIRECT · {_lanHost}:{_lanPort} · 서버 통신 없음";
            }
            catch (Exception ex)
            {
                _statusText.Text = $"직접 연결 실패: {ex.Message}";
                _statusText.Foreground = Brushes.Red;
                _statusText.Visibility = Visibility.Visible;
            }
        }
    }
}

