using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Comote.Shared;
using Newtonsoft.Json;
using PusherClient;

namespace Viewer
{
    public class SignalingClient : IDisposable
    {
        private static readonly HttpClient Http = new();
        private readonly string _viewerId = Guid.NewGuid().ToString("N");
        private readonly string _appKey;
        private readonly string _cluster;
        private readonly string _webAuthUrl;
        private readonly string _accessToken;
        private readonly CancellationTokenSource _lifetimeCts = new();
        private readonly SemaphoreSlim _connectLock = new(1, 1);
        private Pusher? _pusher;
        private int _reconnectStarted;
        private int _disposed;

        public event Action<string, object>? OnSignalReceived;
        public event Action<bool>? OnAvailabilityChanged;

        public string ViewerId => _viewerId;

        public SignalingClient(
            string appKey,
            string cluster,
            string webAuthUrl,
            string accessToken)
        {
            _appKey = appKey;
            _cluster = cluster;
            _webAuthUrl = webAuthUrl;
            _accessToken = accessToken;
        }

        public async Task ConnectAsync()
        {
            await IceServerConfiguration.RefreshManagedCredentialsAsync(
                _webAuthUrl,
                _accessToken,
                _lifetimeCts.Token);
            try
            {
                await ConnectCoreAsync(_lifetimeCts.Token);
            }
            catch
            {
                StartReconnectLoop();
                throw;
            }
        }

        private async Task ConnectCoreAsync(CancellationToken cancellationToken)
        {
            await _connectLock.WaitAsync(cancellationToken);
            try
            {
                if (_pusher?.State == ConnectionState.Connected)
                    return;

                var pusher = new Pusher(_appKey, new PusherOptions
                {
                    Cluster = _cluster,
                    Encrypted = true,
                    Authorizer = new HttpAuthorizer(_webAuthUrl)
                    {
                        AuthenticationHeader =
                            new AuthenticationHeaderValue(
                                "Bearer",
                                _accessToken),
                    },
                });

                pusher.Connected += _ =>
                {
                    Console.WriteLine("[Signaling] Connected");
                    OnAvailabilityChanged?.Invoke(true);
                    _ = ReportAsync("connected", "info");
                };
                pusher.Error += (_, error) =>
                {
                    Console.WriteLine(
                        $"[Signaling] Connection error: {error.Message}");
                    OnAvailabilityChanged?.Invoke(false);
                    _ = ReportAsync("degraded", "warning");
                    StartReconnectLoop();
                };
                pusher.ConnectionStateChanged += (_, state) =>
                {
                    Console.WriteLine(
                        $"[Signaling] Connection state: {state}");
                    if (state == ConnectionState.Disconnected)
                    {
                        OnAvailabilityChanged?.Invoke(false);
                        _ = ReportAsync("disconnected", "warning");
                        StartReconnectLoop();
                    }
                };

                _pusher = pusher;
                await pusher.ConnectAsync();
                var channel = await pusher.SubscribeAsync(
                    $"private-viewer-{_viewerId}");
                channel.Bind("signal", (PusherEvent eventData) =>
                {
                    try
                    {
                        var data =
                            Newtonsoft.Json.Linq.JObject.Parse(eventData.Data);
                        var from = data.Value<string>("from");
                        var signal = data["signal"]?.ToObject<object>();
                        if (!string.IsNullOrWhiteSpace(from) && signal != null)
                            OnSignalReceived?.Invoke(from, signal);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(
                            "[Signaling] Signal parse error: " + ex.Message);
                    }
                });
                Console.WriteLine("[Signaling] Private channel ready");
            }
            finally
            {
                _connectLock.Release();
            }
        }

        private void StartReconnectLoop()
        {
            if (Volatile.Read(ref _disposed) != 0 ||
                Interlocked.Exchange(ref _reconnectStarted, 1) != 0)
                return;
            _ = RunReconnectLoopAsync(_lifetimeCts.Token);
        }

        private async Task RunReconnectLoopAsync(
            CancellationToken cancellationToken)
        {
            try
            {
                var attempt = 0;
                while (!cancellationToken.IsCancellationRequested)
                {
                    attempt++;
                    if (attempt == 1)
                        _ = ReportAsync("reconnecting", "warning");
                    var maximumDelay = Math.Min(30, 1 << Math.Min(attempt, 5));
                    var delay = Random.Shared.Next(1, maximumDelay + 1);
                    await Task.Delay(
                        TimeSpan.FromSeconds(delay),
                        cancellationToken);
                    try
                    {
                        await IceServerConfiguration
                            .RefreshManagedCredentialsAsync(
                                _webAuthUrl,
                                _accessToken,
                                cancellationToken);
                        await ConnectCoreAsync(cancellationToken);
                        Console.WriteLine(
                            $"[Signaling] Reconnected after {attempt} attempt(s)");
                        _ = ReportAsync("recovered", "info", new { attempt });
                        return;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(
                            $"[Signaling] Reconnect attempt {attempt} failed: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
            }
            finally
            {
                Interlocked.Exchange(ref _reconnectStarted, 0);
            }
        }

        private async Task ReportAsync(
            string eventType,
            string severity,
            object? details = null)
        {
            try
            {
                var source = new Uri(_webAuthUrl, UriKind.Absolute);
                var markerIndex = source.AbsolutePath.IndexOf(
                    "/api/pusher/",
                    StringComparison.OrdinalIgnoreCase);
                var basePath = markerIndex >= 0
                    ? source.AbsolutePath[..markerIndex]
                    : "";
                var endpoint = new UriBuilder(source)
                {
                    Path = $"{basePath}/api/telemetry",
                    Query = "",
                    Fragment = "",
                }.Uri;
                using var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    endpoint);
                request.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", _accessToken);
                request.Content = new StringContent(
                    JsonConvert.SerializeObject(new
                    {
                        source = "manager",
                        eventType,
                        severity,
                        details = details ?? new { },
                    }),
                    Encoding.UTF8,
                    "application/json");
                using var timeout = new CancellationTokenSource(
                    TimeSpan.FromSeconds(5));
                using var linked = CancellationTokenSource
                    .CreateLinkedTokenSource(
                        timeout.Token,
                        _lifetimeCts.Token);
                using var response = await Http.SendAsync(
                    request,
                    linked.Token);
            }
            catch
            {
                // Telemetry must never interrupt remote control.
            }
        }

        public async Task SendSignalAsync(string to, object signal)
        {
            var triggerUrl = _webAuthUrl.Replace("/auth", "/trigger");
            var payload = new
            {
                channel = $"private-control-{to}",
                @event = "signal",
                data = new { @from = _viewerId, signal },
            };

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                triggerUrl);
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", _accessToken);
            request.Content = new StringContent(
                JsonConvert.SerializeObject(payload),
                Encoding.UTF8,
                "application/json");
            using var response = await Http.SendAsync(
                request,
                _lifetimeCts.Token);
            response.EnsureSuccessStatusCode();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _lifetimeCts.Cancel();
            _lifetimeCts.Dispose();
            _connectLock.Dispose();
        }
    }

    public class HostInfo
    {
        public string SocketId { get; set; } = "";
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Ip { get; set; } = "unknown";
        public string Resolution { get; set; } = "N/A";
        public int Cpu { get; set; }
        public string Ram { get; set; } = "N/A";
        public string Hdd { get; set; } = "N/A";
        public string Uptime { get; set; } = "N/A";
        public bool IsOnline { get; set; }
        public DateTime LastSeen { get; set; } = DateTime.MinValue;
        public string AgentVersion { get; set; } = "";
        public bool HasClientUpdate
        {
            get
            {
                return Version.TryParse(AgentVersion, out var installed) &&
                       installed < ClientUpdateCatalog.LatestVersion;
            }
        }

        public bool SupportsManagedUpdate =>
            Version.TryParse(AgentVersion, out var installed) &&
            installed >= ProductUpdateSettings.LatestClientVersion;
        public string? ThumbnailUrl { get; set; }
        public byte[]? ThumbnailBytes { get; set; }
    }
}
