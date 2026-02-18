using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using System.Linq;

namespace Viewer
{
    // DB DTO (Data Transfer Object)
    public class HostDto
    {
        [JsonProperty("host_id")]
        public string HostId { get; set; }

        [JsonProperty("host_name")]
        public string HostName { get; set; }

        [JsonProperty("user_id")]
        public string UserId { get; set; }

        [JsonProperty("last_seen")]
        public DateTime LastSeen { get; set; }

        // 시스템 정보 컬럼 (Host heartbeat에서 업데이트)
        [JsonProperty("ip")]
        public string Ip { get; set; } = "unknown";

        [JsonProperty("resolution")]
        public string Resolution { get; set; } = "N/A";

        [JsonProperty("cpu")]
        public int Cpu { get; set; } = 0;

        [JsonProperty("ram")]
        public string Ram { get; set; } = "N/A";

        [JsonProperty("hdd")]
        public string Hdd { get; set; } = "N/A";

        [JsonProperty("uptime")]
        public string Uptime { get; set; } = "N/A";

        [JsonProperty("mac_address")]
        public string MacAddress { get; set; } = "";
    }

    public class HostRepository
    {
        private HttpClient _client;
        private string _baseUrl;
        private string _key;
        private string _accessToken;
        private string _userId;

        public HostRepository(string url, string key)
        {
            _baseUrl = url;
            _key = key;
            _client = new HttpClient();
        }

        /// <summary>
        /// 인증 토큰과 사용자 ID를 설정합니다.
        /// </summary>
        public Task InitializeAsync(string accessToken, string userId)
        {
            _accessToken = accessToken;
            _userId = userId;
            return Task.CompletedTask;
        }

        private void SetHeaders(HttpRequestMessage request)
        {
            request.Headers.Add("apikey", _key);
            request.Headers.Add("Authorization", $"Bearer {_accessToken}");
        }

        /// <summary>
        /// 현재 사용자의 모든 호스트를 조회합니다.
        /// last_seen 기준 60초 이내면 IsOnline으로 판단합니다.
        /// </summary>
        public async Task<List<HostDto>> GetHostsAsync()
        {
            try
            {
                // 현재 사용자의 호스트만 필터링 (RLS 외 추가 안전장치)
                var url = $"{_baseUrl}/rest/v1/hosts?select=*&user_id=eq.{Uri.EscapeDataString(_userId)}";
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                SetHeaders(request);

                var response = await _client.SendAsync(request);
                var content = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    return JsonConvert.DeserializeObject<List<HostDto>>(content) ?? new List<HostDto>();
                }
                else
                {
                    Console.WriteLine($"[HostRepository] GetHosts Failed: {content}");
                    return new List<HostDto>();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HostRepository] GetHosts Error: {ex.Message}");
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
                    LastSeen = DateTime.UtcNow
                };

                // on_conflict: user_id, host_id 조합으로 UPSERT 동작
                var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/rest/v1/hosts?on_conflict=user_id,host_id");
                SetHeaders(request);
                request.Headers.Add("Prefer", "resolution=merge-duplicates"); // This enables UPSERT
                
                string json = JsonConvert.SerializeObject(dto);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _client.SendAsync(request);
                
                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"[HostRepository] Upsert Failed: {error}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HostRepository] UpsertHost Error: {ex.Message}");
            }
        }
    }
}
