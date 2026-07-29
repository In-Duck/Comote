using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SIPSorcery.Net;

namespace Comote.Shared
{
    internal static class IceServerConfiguration
    {
        private static readonly HttpClient Http = new()
        {
            Timeout = TimeSpan.FromSeconds(8),
        };
        private static readonly object Sync = new();
        private static List<RTCIceServer> _managedServers = new();
        private static DateTime _expiresAtUtc = DateTime.MinValue;
        private static Task? _refreshTask;

        public static bool HasManagedTurn
        {
            get
            {
                lock (Sync)
                    return _managedServers.Any(IsTurnServer) &&
                           _expiresAtUtc > DateTime.UtcNow;
            }
        }

        public static List<RTCIceServer> Create()
        {
            var servers = ReadStunServers()
                .Select(url => new RTCIceServer { urls = url })
                .ToList();

            lock (Sync)
            {
                if (_expiresAtUtc > DateTime.UtcNow)
                    servers.AddRange(Clone(_managedServers));
            }

            if (servers.Any(IsTurnServer))
                return servers;

            var turnUrls = Split(Environment.GetEnvironmentVariable("COMOTE_TURN_URLS"));
            var username = Environment.GetEnvironmentVariable("COMOTE_TURN_USERNAME")?.Trim();
            var credential = Environment.GetEnvironmentVariable("COMOTE_TURN_CREDENTIAL")?.Trim();
            if (turnUrls.Count > 0 &&
                !string.IsNullOrWhiteSpace(username) &&
                !string.IsNullOrWhiteSpace(credential))
            {
                servers.AddRange(turnUrls.Select(url => new RTCIceServer
                {
                    urls = url,
                    username = username,
                    credential = credential,
                }));
            }
            else
            {
                Console.WriteLine(
                    "[ICE] Managed TURN is not configured. Direct STUN connection remains available.");
            }

            return servers;
        }

        public static Task RefreshManagedCredentialsAsync(
            string webAuthUrl,
            string accessToken,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(webAuthUrl) ||
                string.IsNullOrWhiteSpace(accessToken))
                return Task.CompletedTask;

            lock (Sync)
            {
                if (_managedServers.Count > 0 &&
                    _expiresAtUtc > DateTime.UtcNow.AddMinutes(15))
                    return Task.CompletedTask;
                if (_refreshTask is { IsCompleted: false })
                    return _refreshTask;

                _refreshTask = RefreshCoreAsync(
                    BuildCredentialsUri(webAuthUrl),
                    accessToken,
                    cancellationToken);
                return _refreshTask;
            }
        }

        private static async Task RefreshCoreAsync(
            Uri endpoint,
            string accessToken,
            CancellationToken cancellationToken)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                request.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", accessToken);
                request.Content = new StringContent(
                    "{}", System.Text.Encoding.UTF8, "application/json");

                using var response = await Http.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine(
                        $"[ICE] Managed TURN unavailable ({(int)response.StatusCode}); using direct connection.");
                    return;
                }

                await using var body =
                    await response.Content.ReadAsStreamAsync(cancellationToken);
                using var document = await JsonDocument.ParseAsync(
                    body, cancellationToken: cancellationToken);
                if (!document.RootElement.TryGetProperty(
                        "iceServers", out var serverElements) ||
                    serverElements.ValueKind != JsonValueKind.Array)
                    throw new InvalidOperationException(
                        "TURN response does not contain iceServers.");

                var parsed = new List<RTCIceServer>();
                foreach (var element in serverElements.EnumerateArray())
                {
                    var serverUsername = ReadString(element, "username");
                    var serverCredential = ReadString(element, "credential");
                    if (!element.TryGetProperty("urls", out var urlsElement))
                        continue;

                    IEnumerable<string> urls = urlsElement.ValueKind switch
                    {
                        JsonValueKind.String => new[]
                        {
                            urlsElement.GetString() ?? "",
                        },
                        JsonValueKind.Array => urlsElement.EnumerateArray()
                            .Where(item => item.ValueKind == JsonValueKind.String)
                            .Select(item => item.GetString() ?? ""),
                        _ => Array.Empty<string>(),
                    };

                    foreach (var url in urls.Where(IsSafeIceUrl))
                    {
                        parsed.Add(new RTCIceServer
                        {
                            urls = url,
                            username = serverUsername,
                            credential = serverCredential,
                        });
                    }
                }

                if (!parsed.Any(IsTurnServer))
                    throw new InvalidOperationException(
                        "TURN response contains no relay URL.");

                var expiresAt = DateTime.UtcNow.AddHours(23);
                if (document.RootElement.TryGetProperty(
                        "expiresAt", out var expiresElement) &&
                    expiresElement.ValueKind == JsonValueKind.String &&
                    DateTime.TryParse(
                        expiresElement.GetString(), out var parsedExpiry))
                    expiresAt = parsedExpiry.ToUniversalTime();

                lock (Sync)
                {
                    _managedServers = parsed;
                    _expiresAtUtc = expiresAt;
                }
                Console.WriteLine(
                    $"[ICE] Managed TURN credentials ready until {expiresAt:O}.");
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[ICE] Managed TURN refresh failed: {ex.Message}. Direct connection remains available.");
            }
        }

        private static Uri BuildCredentialsUri(string webAuthUrl)
        {
            var source = new Uri(webAuthUrl, UriKind.Absolute);
            var marker = "/api/pusher/";
            var markerIndex = source.AbsolutePath.IndexOf(
                marker, StringComparison.OrdinalIgnoreCase);
            var basePath = markerIndex >= 0
                ? source.AbsolutePath[..markerIndex]
                : "";
            return new UriBuilder(source)
            {
                Path = $"{basePath}/api/turn/credentials",
                Query = "",
                Fragment = "",
            }.Uri;
        }

        private static List<string> ReadStunServers()
        {
            var configured = Split(
                Environment.GetEnvironmentVariable("COMOTE_STUN_URLS"));
            if (configured.Count == 0)
                configured = Split(
                    Environment.GetEnvironmentVariable("COMOTE_STUN_URL"));
            return configured.Count > 0
                ? configured
                : new List<string>
                {
                    "stun:stun.cloudflare.com:3478",
                    "stun:stun.l.google.com:19302",
                };
        }

        private static List<string> Split(string? value) =>
            value?.Split(
                    ';',
                    StringSplitOptions.RemoveEmptyEntries |
                    StringSplitOptions.TrimEntries)
                .Where(IsSafeIceUrl)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();

        private static bool IsSafeIceUrl(string value) =>
            value.StartsWith("stun:", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("turn:", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("turns:", StringComparison.OrdinalIgnoreCase);

        private static bool IsTurnServer(RTCIceServer server) =>
            server.urls?.StartsWith(
                "turn:", StringComparison.OrdinalIgnoreCase) == true ||
            server.urls?.StartsWith(
                "turns:", StringComparison.OrdinalIgnoreCase) == true;

        private static string? ReadString(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private static IEnumerable<RTCIceServer> Clone(
            IEnumerable<RTCIceServer> servers) =>
            servers.Select(server => new RTCIceServer
            {
                urls = server.urls,
                username = server.username,
                credential = server.credential,
            });
    }
}
