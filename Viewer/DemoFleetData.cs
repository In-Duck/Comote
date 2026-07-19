using System;
using System.Collections.Generic;

namespace Viewer
{
    internal static class DemoFleetData
    {
        public static List<HostInfo> CreateHosts(int count = 36)
        {
            var hosts = new List<HostInfo>(count);
            var now = DateTime.UtcNow;

            for (var index = 1; index <= count; index++)
            {
                var online = index % 7 != 0 && index % 11 != 0;
                var cpu = 8 + (index * 13 % 78);
                var usedRam = 3 + (index * 5 % 24);
                var disk = 38 + (index * 7 % 55);

                hosts.Add(
                    new HostInfo
                    {
                        Id = $"demo-host-{index:000}",
                        Name = index <= 20
                            ? $"사무실-{index:00}"
                            : $"원격-{index - 20:00}",
                        Ip = $"192.168.{10 + index / 20}.{20 + index}",
                        Resolution = index % 4 == 0
                            ? "2560x1440"
                            : "1920x1080",
                        Cpu = cpu,
                        Ram = $"{usedRam}.0 / 32 GB",
                        Hdd = $"{disk}%",
                        Uptime = $"{index % 12 + 1}일 {index % 23}시간",
                        IsOnline = online,
                        LastSeen = online
                            ? now.AddSeconds(-(index % 8))
                            : now.AddMinutes(-(index * 3)),
                    });
            }

            return hosts;
        }
    }
}
