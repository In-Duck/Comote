using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PusherClient;
using Newtonsoft.Json;

namespace Viewer
{
    public class SignalingClient
    {
        private Pusher _pusher;
        private PusherServer.Pusher _pusherServer;
        private Channel _lobbyChannel;
        private string _viewerId;
        private string _appKey;
        private string _secret;
        private string _appId;
        private string _cluster;

        public event Action<List<HostInfo>> OnHostListReceived;
        public event Action<string, object> OnSignalReceived;

        private Dictionary<string, HostInfo> _hostCache = new Dictionary<string, HostInfo>();
        private Timer? _cleanupTimer;

        public string ViewerId => _viewerId;

        public SignalingClient(string appId, string appKey, string secret, string cluster)
        {
            _appId = appId;
            _appKey = appKey;
            _secret = secret;
            _cluster = cluster;
            _viewerId = Guid.NewGuid().ToString().Substring(0, 8);
            
            _pusher = new Pusher(appKey, new PusherOptions { Cluster = cluster, Encrypted = true });
            _pusherServer = new PusherServer.Pusher(appId, appKey, secret, new PusherServer.PusherOptions { Cluster = cluster, Encrypted = true });
        }

        public async Task ConnectAsync()
        {
            await _pusher.ConnectAsync();

            // 호스트 목록 수신 (로비)
            Console.WriteLine("[Signaling] Subscribing to host-lobby...");
            _lobbyChannel = await _pusher.SubscribeAsync("host-lobby");
            _lobbyChannel.Bind("host-alive", (PusherEvent eventData) =>
            {
                try {
                    var data = JsonConvert.DeserializeObject<dynamic>(eventData.Data);
                    string hostId = data.id;
                    Console.WriteLine($"[Signaling] Host alive received: {hostId}");

                    // 시스템 정보 포함한 HostInfo 업데이트
                    if (!_hostCache.ContainsKey(hostId))
                        _hostCache[hostId] = new HostInfo { Id = hostId, SocketId = hostId };

                    var info = _hostCache[hostId];
                    info.LastSeen = DateTime.Now;
                    info.IsOnline = true;
                    info.Name = (string?)(data.name) ?? hostId;
                    info.Ip = (string?)(data.ip) ?? "unknown";
                    info.Resolution = (string?)(data.resolution) ?? "N/A";
                    info.Cpu = (int?)(data.cpu) ?? 0;
                    info.Ram = (string?)(data.ram) ?? "N/A";
                    info.Hdd = (string?)(data.hdd) ?? "N/A";
                    info.Uptime = (string?)(data.uptime) ?? "N/A";

                    UpdateHostList();
                } catch(Exception ex) { Console.WriteLine("[Signaling] Lobby parse error: " + ex.Message + " | Data: " + eventData.Data); }
            });
            Console.WriteLine("[Signaling] Subscribed to host-lobby");

            // 뷰어 전용 채널 구독 (Host로부터 수신용)
            Console.WriteLine($"[Signaling] Subscribing to viewer-{_viewerId}...");
            var myChannel = await _pusher.SubscribeAsync($"viewer-{_viewerId}");
            myChannel.Bind("signal", (PusherEvent eventData) =>
            {
                Console.WriteLine($"[Signaling] Signal event received on viewer-{_viewerId}");
                try {
                    var data = JsonConvert.DeserializeObject<dynamic>(eventData.Data);
                    string from = data.from;
                    object signal = data.signal;
                    OnSignalReceived?.Invoke(from, signal);
                } catch(Exception ex) { Console.WriteLine("[Signaling] Signal parse error: " + ex.Message + " | Data: " + eventData.Data); }
            });
            Console.WriteLine($"[Signaling] Subscribed and bound to viewer-{_viewerId}");

            // 주기적 호스트 상태 갱신 (5초 간격, 오프라인 감지용)
            _cleanupTimer = new Timer(_ => UpdateHostList(), null, 5000, 5000);
        }

        /// <summary>
        /// 호스트 캐시 초기화 (재연결 시 오래된 호스트 제거용)
        /// </summary>
        public void ClearHostCache()
        {
            _hostCache.Clear();
            Console.WriteLine("[Signaling] Host cache cleared");
        }

        private void UpdateHostList()
        {
            var activeHosts = new List<HostInfo>();
            var now = DateTime.Now;
            var toRemove = new List<string>();

            foreach (var kvp in _hostCache)
            {
                var host = kvp.Value;
                double elapsed = (now - host.LastSeen).TotalSeconds;
                host.IsOnline = elapsed < 30;

                // 5분 이상 오프라인이면 캐시에서 제거
                if (elapsed > 300)
                {
                    toRemove.Add(kvp.Key);
                    continue;
                }
                activeHosts.Add(host);
            }

            foreach (var key in toRemove)
                _hostCache.Remove(key);

            // 온라인 먼저, 최근 alive 먼저 정렬
            activeHosts.Sort((a, b) =>
            {
                if (a.IsOnline != b.IsOnline) return b.IsOnline.CompareTo(a.IsOnline);
                return b.LastSeen.CompareTo(a.LastSeen);
            });
            OnHostListReceived?.Invoke(activeHosts);
        }

        public async Task SendSignalAsync(string to, object signal)
        {
            Console.WriteLine($"[Signaling] Triggering signal to host control-{to}...");
            var result = await _pusherServer.TriggerAsync($"control-{to}", "signal", new { from = _viewerId, signal = signal });
            Console.WriteLine($"[Signaling] Trigger result: {result.StatusCode}");
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
        public DateTime LastSeen { get; set; } = DateTime.MinValue;
    }
}
