using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using PusherClient;
using Newtonsoft.Json;

namespace Host
{
    public class SignalingClient
    {
        private Pusher _pusher;
        private PusherServer.Pusher _pusherServer;
        private Channel _channel;
        private string _hostId;
        private string _appKey;
        private string _secret;
        private string _appId;
        private string _cluster;

        // 시스템 정보
        private string _hostName;
        private string _resolution;
        private PerformanceCounter? _cpuCounter;
        private DateTime _startTime = DateTime.Now;

        public event Action<string, object> OnSignalReceived;

        public SignalingClient(string appId, string appKey, string secret, string cluster, string hostId,
            string? hostName = null, string? resolution = null)
        {
            _appId = appId;
            _appKey = appKey;
            _secret = secret;
            _cluster = cluster;
            _hostId = hostId;
            _hostName = hostName ?? Environment.MachineName;
            _resolution = resolution ?? "unknown";

            _pusherServer = new PusherServer.Pusher(_appId, _appKey, _secret, new PusherServer.PusherOptions { Cluster = _cluster, Encrypted = true });

            // CPU 카운터 초기화 (첫 호출은 0 반환하므로 미리 시작)
            try
            {
                _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                _cpuCounter.NextValue();
            }
            catch { _cpuCounter = null; }
        }

        public async Task ConnectAsync()
        {
            _pusher = new Pusher(_appKey, new PusherOptions { Cluster = _cluster, Encrypted = true });
            
            _pusher.Connected += (s) => {
                Console.WriteLine("Pusher Connected");
                StartHeartbeat();
            };

            await _pusher.ConnectAsync();

            // 호스트 전용 채널 구독 (Viewer로부터 수신용)
            Console.WriteLine($"[Signaling] Subscribing to control-{_hostId}...");
            _channel = await _pusher.SubscribeAsync($"control-{_hostId}");
            _channel.Bind("signal", (PusherEvent eventData) =>
            {
                Console.WriteLine($"[Signaling] Signal event received on control-{_hostId}");
                try {
                    var data = JsonConvert.DeserializeObject<dynamic>(eventData.Data);
                    string from = data.from;
                    object signal = data.signal;
                    Console.WriteLine($"[Signaling] Signal received from: {from}");
                    OnSignalReceived?.Invoke(from, signal);
                } catch(Exception ex) { Console.WriteLine("[Signaling] Signal parse error: " + ex.Message + " | Data: " + eventData.Data); }
            });
            Console.WriteLine($"[Signaling] Subscribed and bound to control-{_hostId}");
        }

        private async void StartHeartbeat()
        {
            Console.WriteLine("[Signaling] Heartbeat loop started");
            while (true)
            {
                try {
                    if (_pusher?.State == ConnectionState.Connected)
                    {
                        var info = CollectSystemInfo();
                        var result = await _pusherServer.TriggerAsync("host-lobby", "host-alive", info);
                        Console.WriteLine($"[Signaling] Heartbeat sent (ID: {_hostId}, Result: {result.StatusCode})");
                    }
                } catch (Exception ex) {
                    Console.WriteLine($"[Signaling] Heartbeat error: {ex.Message}");
                }
                await Task.Delay(10000);
            }
        }

        /// <summary>
        /// 시스템 정보를 수집하여 host-alive 메시지에 포함합니다.
        /// </summary>
        private object CollectSystemInfo()
        {
            // CPU 사용률
            int cpu = 0;
            try { if (_cpuCounter != null) cpu = (int)_cpuCounter.NextValue(); } catch { }

            // RAM 정보
            string ram = "N/A";
            try
            {
                var gcInfo = GC.GetGCMemoryInfo();
                long totalBytes = gcInfo.TotalAvailableMemoryBytes;
                long totalGB = totalBytes / (1024L * 1024 * 1024);
                // 사용 가능한 메모리를 근사적으로 계산
                var pc = new PerformanceCounter("Memory", "Available MBytes");
                long availMB = (long)pc.NextValue();
                long usedGB = (totalGB * 1024 - availMB) / 1024;
                ram = $"{usedGB}GB / {totalGB}GB";
                pc.Dispose();
            }
            catch { }

            // HDD 정보 (C: 드라이브)
            string hdd = "N/A";
            try
            {
                var drive = DriveInfo.GetDrives().FirstOrDefault(d => d.IsReady && d.Name.StartsWith("C"));
                if (drive != null)
                {
                    long totalGB = drive.TotalSize / (1024L * 1024 * 1024);
                    long freeGB = drive.AvailableFreeSpace / (1024L * 1024 * 1024);
                    long usedGB = totalGB - freeGB;
                    hdd = $"{usedGB}GB / {totalGB}GB";
                }
            }
            catch { }

            // 로컬 IP
            string ip = "unknown";
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
                socket.Connect("8.8.8.8", 65530);
                if (socket.LocalEndPoint is IPEndPoint endPoint)
                    ip = endPoint.Address.ToString();
            }
            catch { }

            // 가동시간
            var uptime = DateTime.Now - _startTime;
            string uptimeStr = uptime.TotalHours >= 24
                ? $"{(int)uptime.TotalDays}일 {uptime.Hours}시간"
                : uptime.TotalHours >= 1
                    ? $"{(int)uptime.TotalHours}시간 {uptime.Minutes}분"
                    : $"{uptime.Minutes}분";

            return new
            {
                id = _hostId,
                name = _hostName,
                ip = ip,
                resolution = _resolution,
                cpu = cpu,
                ram = ram,
                hdd = hdd,
                uptime = uptimeStr
            };
        }

        public async Task SendSignalAsync(string to, object signal)
        {
            Console.WriteLine($"[Signaling] Triggering signal to viewer-{to}...");
            var result = await _pusherServer.TriggerAsync($"viewer-{to}", "signal", new { from = _hostId, signal = signal });
            Console.WriteLine($"[Signaling] Trigger result: {result.StatusCode}");
        }
    }
}
