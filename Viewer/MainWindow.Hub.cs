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
            var server = new ManagerHubServer(
                _hubPort,
                _hubCredentials);
            _hubServer = server;

            server.ClientOnline += info =>
                Dispatcher.BeginInvoke(() =>
                {
                    if (_hubClosing)
                    {
                        return;
                    }

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
                        AllowsRemoteTasks = info.AllowsRemoteTasks,
                        AllowsSystemCommands = info.AllowsSystemCommands,
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
                            StringComparison.Ordinal))
                    {
                        ConnectToHost(info.Id);
                    }
                });

            server.ClientOffline += clientId =>
                Dispatcher.BeginInvoke(() =>
                {
                    if (_hubClosing)
                    {
                        return;
                    }

                    if (_persistentHosts.TryGetValue(
                            clientId,
                            out var host))
                    {
                        host.IsOnline = false;
                        host.LastSeen = DateTime.UtcNow;
                        host.Uptime = "연결 종료";
                    }
                    UpdateLobbyUI(_persistentHosts.Values.ToList());
                    if (string.Equals(
                            _connectedHostId,
                            clientId,
                            StringComparison.Ordinal))
                    {
                        _statusText.Text = "Client 연결이 종료되었습니다.";
                        _statusText.Foreground = Brushes.Red;
                        _statusText.Visibility = Visibility.Visible;
                    }
                });

            server.SignalReceived += HandleHubSignalAsync;

            server.ThumbnailReceived += (clientId, jpeg) =>
                Dispatcher.BeginInvoke(() =>
                {
                    if (!_hubClosing)
                    {
                        SetLiveThumbnail(clientId, jpeg);
                    }
                });

            _ = Task.Run(async () =>
            {
                try
                {
                    await server.RunAsync(_hubLifetime.Token);
                }
                catch (OperationCanceledException)
                    when (_hubLifetime.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        if (!_hubClosing)
                        {
                            _statusBarText.Text =
                                $"Hub 수신 오류: {ex.Message}";
                        }
                    });
                }
            });

            return Task.CompletedTask;
        }

        private async Task HandleHubSignalAsync(
            string clientId,
            object signal)
        {
            var lifetime = _hubLifetime;
            if (lifetime is null || lifetime.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await _hubSignalDispatchGate.WaitAsync(lifetime.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                var receiver = await Dispatcher.InvokeAsync(() =>
                {
                    if (_hubClosing ||
                        !string.Equals(
                            _connectedHostId,
                            clientId,
                            StringComparison.Ordinal))
                    {
                        return null;
                    }

                    return _receiver;
                });
                if (receiver is null)
                {
                    return;
                }

                await receiver.HandleSignalAsync(clientId, signal);
            }
            catch (OperationCanceledException)
                when (lifetime.IsCancellationRequested)
            {
            }
            catch (ObjectDisposedException)
                when (_hubClosing || lifetime.IsCancellationRequested)
            {
            }
            finally
            {
                _hubSignalDispatchGate.Release();
            }
        }
    }
}
