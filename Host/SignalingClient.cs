
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using PusherClient;
using Newtonsoft.Json;
using System.Text;
using System.Net.Http;
using System.Net.Http.Headers;

namespace Host
{
    public class SignalingClient
    {
        // 소켓 고갈 방지를 위해 static HttpClient 사용 (앱 수명 동안 재사용)
        private static readonly HttpClient _httpClient = new HttpClient();

        private Pusher _pusher;
        private string _appId;
        private string _appKey;
        private string _cluster;
        private string _hostId;
        private string _webAuthUrl;
        private string _accessToken;
        private string _hostName;
        private string _resolution;
        private PerformanceCounter? _cpuCounter;
        private DateTime _startTime = DateTime.Now;

        // Supabase heartbeat (Presence 채널 대체)
        private string _supabaseUrl;
        private string _supabaseKey;
        private string _userId;
        private System.Threading.Timer? _heartbeatTimer;

        public event Action<string, object> OnSignalReceived;

        public SignalingClient(string appId, string appKey, string cluster, string webAuthUrl, string accessToken,
            string hostId, string? hostName = null, string? resolution = null,
            string? supabaseUrl = null, string? supabaseKey = null, string? userId = null)
        {
            _appId = appId;
            _appKey = appKey;
            _cluster = cluster;
            _webAuthUrl = webAuthUrl;
            _accessToken = accessToken;
            _hostId = hostId;
            _hostName = hostName ?? Environment.MachineName;
            _resolution = resolution ?? "unknown";
            _supabaseUrl = supabaseUrl ?? "";
            _supabaseKey = supabaseKey ?? "";
            _userId = userId ?? "";
            
            // 시스템 정보 수집
            try
            {
                _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                _cpuCounter.NextValue();
            }
            catch { _cpuCounter = null; }
        }

        public async Task ConnectAsync()
        {
            Console.WriteLine("[Signaling] Connecting to Pusher...");

            // Pusher 초기화 (Private 채널 전용, Presence 채널 미사용)
            _pusher = new Pusher(_appKey, new PusherOptions
            {
                Cluster = _cluster,
                Encrypted = true,
                Authorizer = new HttpAuthorizer(_webAuthUrl)
                {
                    AuthenticationHeader = new AuthenticationHeaderValue("Bearer", _accessToken)
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

            await _pusher.ConnectAsync();

            // Private 채널만 구독 (시그널링 전용)
            Console.WriteLine($"[Signaling] Subscribing to private-control-{_hostId}...");
            var privateChannel = await _pusher.SubscribeAsync($"private-control-{_hostId}");
            privateChannel.Bind("pusher:subscription_succeeded", (PusherEvent eventData) => {
                Console.WriteLine($"[Signaling] Subscribed to private-control-{_hostId} successfully");
            });
            privateChannel.Bind("signal", (PusherEvent eventData) =>
            {
                try {
                    var data = JsonConvert.DeserializeObject<dynamic>(eventData.Data);
                    string from = data.from;
                    object signal = data.signal;
                    OnSignalReceived?.Invoke(from, signal);
                } catch(Exception ex) { Console.WriteLine("[Signaling] Signal parse error: " + ex.Message); }
            });

            // Supabase heartbeat 시작 (30초마다 시스템 정보 + last_seen 업데이트)
            if (!string.IsNullOrEmpty(_supabaseUrl) && !string.IsNullOrEmpty(_userId))
            {
                // 최초 즉시 실행 + 이후 30초 간격
                _heartbeatTimer = new System.Threading.Timer(async _ => await SendHeartbeatAsync(), null, 0, 30000);
                Console.WriteLine("[Signaling] Supabase heartbeat started (30s interval)");
            }
        }

        /// <summary>
        /// Supabase hosts 테이블에 시스템 정보 + last_seen을 UPSERT합니다.
        /// Presence 채널을 완전히 대체합니다.
        /// </summary>
        private async Task SendHeartbeatAsync()
        {
            try
            {
                var info = CollectSystemInfo();
                var dto = new
                {
                    host_id = _hostId,
                    user_id = _userId,
                    host_name = _hostName,
                    // [Fix] DB 스키마 불일치로 인한 Heartbeat 에러 방지 (ip, resolution, cpu 등 제거)
                    // ip = (string?)((dynamic)info).ip ?? "unknown",
                    // resolution = (string?)((dynamic)info).resolution ?? "N/A",
                    // [Fix] DB 스키마에 해당 컬럼이 없어 에러 발생 (임시 비활성화)
                    // cpu = (int?)((dynamic)info).cpu ?? 0,
                    // ram = (string?)((dynamic)info).ram ?? "N/A",
                    // hdd = (string?)((dynamic)info).hdd ?? "N/A",
                    // uptime = (string?)((dynamic)info).uptime ?? "N/A",
                    last_seen = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
                };

                var baseUrl = _supabaseUrl.TrimEnd('/');
                var request = new HttpRequestMessage(HttpMethod.Post,
                    $"{baseUrl}/rest/v1/hosts?on_conflict=user_id,host_id");
                request.Headers.Add("apikey", _supabaseKey);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
                request.Headers.Add("Prefer", "resolution=merge-duplicates");
                request.Content = new StringContent(
                    JsonConvert.SerializeObject(dto), Encoding.UTF8, "application/json");

                var response = await _httpClient.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    string body = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"[Heartbeat] Failed: {body}");
                }
                else
                {
                    // Console.WriteLine("[Heartbeat] Sent successfully"); // 너무 자주 뜨지 않게 주석 처리하거나 필요시 활성화
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Heartbeat] Error: {ex.Message}");
            }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        public object CollectSystemInfo()
        {
            int cpu = 0;
            try { if (_cpuCounter != null) cpu = (int)_cpuCounter.NextValue(); } catch { }

            string ram = "N/A";
            try
            {
                var memStatus = new MEMORYSTATUSEX();
                memStatus.dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(MEMORYSTATUSEX));
                if (GlobalMemoryStatusEx(ref memStatus))
                {
                    double totalGB = memStatus.ullTotalPhys / (1024.0 * 1024 * 1024);
                    double freeGB = memStatus.ullAvailPhys / (1024.0 * 1024 * 1024);
                    double usedGB = totalGB - freeGB;
                    ram = $"{usedGB:F1}GB / {totalGB:F0}GB";
                }
            }
            catch { }

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

            string ip = "unknown";
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
                socket.Connect("8.8.8.8", 65530);
                if (socket.LocalEndPoint is IPEndPoint endPoint)
                    ip = endPoint.Address.ToString();
            }
            catch { }

            var uptime = DateTime.Now - _startTime;
            string uptimeStr = uptime.TotalHours >= 24
                ? $"{(int)uptime.TotalDays}일 {uptime.Hours}시간"
                : uptime.TotalHours >= 1
                    ? $"{(int)uptime.TotalHours}시간 {uptime.Minutes}분"
                    : $"{uptime.Minutes}분";

            // MAC Address
            var mac = "";
            try {
                foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()) {
                    if (nic.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up && 
                        nic.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback) {
                        mac = nic.GetPhysicalAddress().ToString();
                        if (!string.IsNullOrEmpty(mac)) {
                            // Format: XX:XX:XX:XX:XX:XX
                            var sb = new System.Text.StringBuilder();
                            for(int i=0; i<mac.Length; i++) {
                                if (i>0 && i%2==0) sb.Append(":");
                                sb.Append(mac[i]);
                            }
                            mac = sb.ToString();
                            break;
                        }
                    }
                }
            } catch {}

            return new
            {
                id = _hostId,
                name = _hostName,
                ip = ip,
                resolution = _resolution,
                cpu = cpu,
                ram = ram,
                hdd = hdd,
                uptime = uptimeStr,
                mac_address = mac
            };
        }

        public async Task SendSignalAsync(string to, object signal)
        {
            try
            {
                string triggerUrl = _webAuthUrl.Replace("/auth", "/trigger");
                var payload = new
                {
                    channel = $"private-viewer-{to}",
                    @event = "signal",
                    data = new { @from = _hostId, signal = signal }
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
}
