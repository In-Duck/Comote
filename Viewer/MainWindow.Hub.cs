using System.Windows;
using System.Windows.Media;

namespace Viewer
{
    public partial class MainWindow
    {
        private Task StartHubModeAsync()
        {
            Title = $"Comote Manager Hub - TCP {_hubPort}";
            SwitchLobbyTab(false);
            _statusBarText.Text =
                $"HUB · TCP {_hubPort} · Client 접속 대기 중 · 중앙 서버 없음";
            _hubLifetime = new CancellationTokenSource();
            _hubServer = new ManagerHubServer(
                _hubPort,
                _hubPassword!);

            _hubServer.ClientOnline += info =>
                Dispatcher.Invoke(() =>
                {
                    _persistentHosts.TryGetValue(
                        info.Id, out var previousHost);
                    _persistentHosts[info.Id] = new HostInfo
                    {
                        Id = info.Id,
                        Name = info.Name,
                        Ip = info.Address,
                        Resolution = "연결 후 확인",
                        Cpu = 0,
                        Ram = "N/A",
                        Hdd = "N/A",
                        Uptime = "접속됨",
                        IsOnline = true,
                        LastSeen = DateTime.UtcNow,
                        ThumbnailBytes = previousHost?.ThumbnailBytes,
                    };
                    UpdateLobbyUI(_persistentHosts.Values.ToList());
                    _statusBarText.Text =
                        $"HUB · TCP {_hubPort} · " +
                        $"{_persistentHosts.Values.Count(host => host.IsOnline)}대 연결";
                    Console.WriteLine(
                        $"[Hub] Client online: {info.Id} ({info.Name})");
                    if (_connectedHostId == null &&
                        string.Equals(
                            _hubAutoConnectId,
                            info.Id,
                            StringComparison.OrdinalIgnoreCase))
                        ConnectToHost(info.Id);
                });

            _hubServer.ClientOffline += clientId =>
                Dispatcher.Invoke(() =>
                {
                    if (_persistentHosts.TryGetValue(
                            clientId,
                            out var host))
                    {
                        host.IsOnline = false;
                        host.LastSeen = DateTime.UtcNow;
                        host.Uptime = "연결 종료";
                    }
                    UpdateLobbyUI(_persistentHosts.Values.ToList());
                    if (_connectedHostId == clientId)
                    {
                        _statusText.Text = "Client 연결이 종료되었습니다.";
                        _statusText.Foreground = Brushes.Red;
                        _statusText.Visibility = Visibility.Visible;
                    }
                });

            _hubServer.SignalReceived += async (clientId, signal) =>
            {
                if (_connectedHostId == clientId && _receiver != null)
                    await _receiver.HandleSignalAsync(signal);
            };

            _hubServer.ThumbnailReceived += (clientId, jpeg) =>
                Dispatcher.BeginInvoke(() =>
                    SetLiveThumbnail(clientId, jpeg));

            _ = Task.Run(async () =>
            {
                try
                {
                    await _hubServer.RunAsync(_hubLifetime.Token);
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() =>
                    {
                        _statusBarText.Text =
                            $"Hub 수신 오류: {ex.Message}";
                    });
                }
            });

            return Task.CompletedTask;
        }
    }
}
