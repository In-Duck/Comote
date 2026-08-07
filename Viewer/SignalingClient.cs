
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using PusherClient;
using Newtonsoft.Json;
using System.Text;
using System.Net.Http;
using System.Net.Http.Headers;

namespace Viewer
{
    public class SignalingClient
    {
        // 소켓 고갈 방지를 위해 static HttpClient 사용 (앱 수명 동안 재사용)
        private static readonly HttpClient _httpClient = new HttpClient();

        private Pusher _pusher;
        private string _viewerId;
        private string _appKey;
        private string _cluster;
        private string _webAuthUrl;
        private string _accessToken;

        public event Func<string, object, Task>? OnSignalReceived;

        public string ViewerId => _viewerId;

        public SignalingClient(string appKey, string cluster, string webAuthUrl, string accessToken)
        {
            _appKey = appKey;
            _cluster = cluster;
            _webAuthUrl = webAuthUrl;
            _accessToken = accessToken;
            _viewerId = Guid.NewGuid().ToString("N");
            
            _pusher = new Pusher(appKey, new PusherOptions
            {
                Cluster = cluster,
                Encrypted = true,
                Authorizer = new HttpAuthorizer(webAuthUrl)
                {
                    AuthenticationHeader = new AuthenticationHeaderValue("Bearer", accessToken)
                }
            });

            _pusher.Connected += (s) => {
                Console.WriteLine("[Signaling] Pusher Connected (Private Channel Only)");
            };
            _pusher.Error += (s, e) => {
                Console.WriteLine($"[Signaling] Pusher Connection Error: {e.Message}");
            };
            _pusher.ConnectionStateChanged += (s, state) => {
                Console.WriteLine($"[Signaling] Connection State: {state}");
            };
        }

        public async Task ConnectAsync()
        {
            await _pusher.ConnectAsync();

            // Private 채널만 구독 (시그널링 전용)
            // Presence 채널(presence-host-lobby)은 제거됨 → 호스트 목록은 Supabase에서 직접 조회
            Console.WriteLine($"[Signaling] Subscribing to private-viewer-{_viewerId}...");
            var myChannel = await _pusher.SubscribeAsync($"private-viewer-{_viewerId}");
            myChannel.Bind("signal", (PusherEvent eventData) =>
            {
                try {
                    var data = JsonConvert.DeserializeObject<Newtonsoft.Json.Linq.JObject>(
                        eventData.Data) ?? throw new JsonException(
                        "Signal payload is empty.");
                    string from = data.Value<string>("from") ??
                        throw new JsonException("Signal sender is missing.");
                    object signal = data["signal"] ??
                        throw new JsonException("Signal body is missing.");
                    QueueSignalReceived(from, signal);
                } catch(Exception ex) { Console.WriteLine("[Signaling] Signal parse error: " + ex.Message); }
            });

            Console.WriteLine("[Signaling] Ready (Private channel only, no Presence)");
        }

        private void QueueSignalReceived(string from, object signal)
        {
            var handlers = OnSignalReceived;
            if (handlers == null)
            {
                return;
            }

            foreach (Func<string, object, Task> handler in
                     handlers.GetInvocationList())
            {
                _ = InvokeSignalHandlerAsync(handler, from, signal);
            }
        }

        private static async Task InvokeSignalHandlerAsync(
            Func<string, object, Task> handler,
            string from,
            object signal)
        {
            try
            {
                await handler(from, signal).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[Signaling] Signal handler failed: {ex.GetType().Name}");
            }
        }

        public async Task SendSignalAsync(string to, object signal)
        {
            try
            {
                string triggerUrl = _webAuthUrl.Replace("/auth", "/trigger");
                var payload = new
                {
                    channel = $"private-control-{to}",
                    @event = "signal",
                    data = new { @from = _viewerId, signal = signal }
                };

                // 매 요청마다 Authorization 헤더를 설정 (thread-safe 방식)
                var request = new HttpRequestMessage(HttpMethod.Post, triggerUrl);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
                request.Content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");
                await _httpClient.SendAsync(request);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Signaling] SendSignal Error: {ex.Message}");
            }
        }
    }

    public class HostInfo
    {
        public string SocketId { get; set; } = "";
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Ip { get; set; } = "unknown";
        public string Resolution { get; set; } = "N/A";
        public int Cpu { get; set; } = 0;
        public string Ram { get; set; } = "N/A";
        public string Hdd { get; set; } = "N/A";
        public string Uptime { get; set; } = "N/A";
        public bool IsOnline { get; set; } = false;
        public bool AllowsRemoteTasks { get; set; } = false;
        public bool AllowsSystemCommands { get; set; } = false;
        public DateTime LastSeen { get; set; } = DateTime.MinValue;
        public string? ThumbnailUrl { get; set; }
        public byte[]? ThumbnailBytes { get; set; }
    }
}
