using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Viewer
{
    public class HostDto
    {
        [JsonProperty("host_id")]
        public string HostId { get; set; } = "";

        [JsonProperty("host_name")]
        public string HostName { get; set; } = "";

        [JsonProperty("user_id")]
        public string UserId { get; set; } = "";

        [JsonProperty("last_seen")]
        public DateTime LastSeen { get; set; }

        [JsonProperty("ip")]
        public string Ip { get; set; } = "unknown";

        [JsonProperty("resolution")]
        public string Resolution { get; set; } = "N/A";

        [JsonProperty("cpu")]
        public int Cpu { get; set; }

        [JsonProperty("ram")]
        public string Ram { get; set; } = "N/A";

        [JsonProperty("hdd")]
        public string Hdd { get; set; } = "N/A";

        [JsonProperty("uptime")]
        public string Uptime { get; set; } = "N/A";

        [JsonProperty("mac_address")]
        public string MacAddress { get; set; } = "";

        [JsonProperty("thumbnail_path")]
        public string? ThumbnailPath { get; set; }

        [JsonIgnore]
        public string? ThumbnailUrl { get; set; }
    }

    public class HostRepository : IDisposable
    {
        private readonly HttpClient _client;
        private readonly string _baseUrl;
        private readonly string _key;
        private readonly string _thumbnailApiUrl;
        private string _accessToken = "";
        private string _userId = "";

        public HostRepository(string url, string key, string webAuthUrl)
        {
            _baseUrl = url.TrimEnd('/');
            _key = key;
            _thumbnailApiUrl = webAuthUrl.Replace(
                "/api/pusher/auth",
                "/api/hosts/thumbnails",
                StringComparison.OrdinalIgnoreCase);
            _client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        }

        public Task InitializeAsync(string accessToken, string userId)
        {
            _accessToken = accessToken;
            _userId = userId;
            return Task.CompletedTask;
        }

        public async Task<List<HostDto>> GetHostsAsync()
        {
            try
            {
                var url =
                    $"{_baseUrl}/rest/v1/hosts?select=*&user_id=eq.{Uri.EscapeDataString(_userId)}";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                SetSupabaseHeaders(request);

                using var response = await _client.SendAsync(request);
                var content = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[HostRepository] GetHosts failed: {content}");
                    return new List<HostDto>();
                }

                var hosts = JsonConvert.DeserializeObject<List<HostDto>>(content)
                    ?? new List<HostDto>();
                await ApplySignedThumbnailUrlsAsync(hosts);
                return hosts;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HostRepository] GetHosts error: {ex.Message}");
                return new List<HostDto>();
            }
        }

        public async Task UpsertHostAsync(string hostId, string hostName, string userId)
        {
            try
            {
                var dto = new HostDto
                {
                    HostId = hostId,
                    HostName = hostName,
                    UserId = userId,
                    LastSeen = DateTime.UtcNow,
                };

                using var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"{_baseUrl}/rest/v1/hosts?on_conflict=user_id,host_id");
                SetSupabaseHeaders(request);
                request.Headers.Add("Prefer", "resolution=merge-duplicates");
                request.Content = new StringContent(
                    JsonConvert.SerializeObject(dto),
                    Encoding.UTF8,
                    "application/json");

                using var response = await _client.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"[HostRepository] Upsert failed: {error}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HostRepository] Upsert error: {ex.Message}");
            }
        }

        public async Task DeleteHostAsync(string hostId)
        {
            try
            {
                var url =
                    $"{_baseUrl}/rest/v1/hosts?host_id=eq.{Uri.EscapeDataString(hostId)}" +
                    $"&user_id=eq.{Uri.EscapeDataString(_userId)}";
                using var request = new HttpRequestMessage(HttpMethod.Delete, url);
                SetSupabaseHeaders(request);

                using var response = await _client.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"[HostRepository] Delete failed: {error}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HostRepository] Delete error: {ex.Message}");
            }
        }

        private async Task ApplySignedThumbnailUrlsAsync(List<HostDto> hosts)
        {
            if (hosts.Count == 0 || string.IsNullOrWhiteSpace(_thumbnailApiUrl))
                return;

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, _thumbnailApiUrl);
                request.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", _accessToken);
                using var response = await _client.SendAsync(request);
                if (!response.IsSuccessStatusCode) return;

                var json = await response.Content.ReadAsStringAsync();
                var result = JsonConvert.DeserializeObject<ThumbnailUrlResponse>(json);
                if (result?.Urls == null) return;

                foreach (var host in hosts)
                {
                    if (result.Urls.TryGetValue(host.HostId, out var url))
                        host.ThumbnailUrl = url;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HostRepository] Thumbnail signing error: {ex.Message}");
            }
        }

        private void SetSupabaseHeaders(HttpRequestMessage request)
        {
            request.Headers.Add("apikey", _key);
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", _accessToken);
        }

        public void Dispose()
        {
            _client.Dispose();
        }

        private sealed class ThumbnailUrlResponse
        {
            [JsonProperty("urls")]
            public Dictionary<string, string> Urls { get; set; } = new();
        }
    }
}
