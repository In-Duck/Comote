using System;
using System.Collections.Generic;
using System.Linq;
using SIPSorcery.Net;

namespace Comote.Shared
{
    internal static class IceServerConfiguration
    {
        public static List<RTCIceServer> Create()
        {
            var servers = new List<RTCIceServer>
            {
                new() { urls = Read("COMOTE_STUN_URL", "stun:stun.l.google.com:19302") },
            };

            var turnUrls = Environment.GetEnvironmentVariable("COMOTE_TURN_URLS")?
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var username = Environment.GetEnvironmentVariable("COMOTE_TURN_USERNAME")?.Trim();
            var credential = Environment.GetEnvironmentVariable("COMOTE_TURN_CREDENTIAL")?.Trim();

            if (turnUrls?.Length > 0 && !string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(credential))
            {
                servers.AddRange(turnUrls.Select(url => new RTCIceServer
                {
                    urls = url,
                    username = username,
                    credential = credential,
                }));
                return servers;
            }

            Console.WriteLine("[ICE] COMOTE_TURN_* is not configured; using the public compatibility relay. Configure a private TURN service for production.");
            servers.Add(new RTCIceServer { urls = "stun:openrelay.metered.ca:80" });
            servers.Add(new RTCIceServer { urls = "turn:openrelay.metered.ca:80", username = "openrelayproject", credential = "openrelayproject" });
            servers.Add(new RTCIceServer { urls = "turn:openrelay.metered.ca:443?transport=tcp", username = "openrelayproject", credential = "openrelayproject" });
            return servers;
        }

        private static string Read(string name, string fallback) =>
            Environment.GetEnvironmentVariable(name)?.Trim() is { Length: > 0 } value
                ? value
                : fallback;
    }
}