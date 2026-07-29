using System;
using System.Collections.Generic;
using System.Linq;

namespace Viewer;

public partial class MainWindow
{
    private List<HostInfo> BuildStableHostOrder(IEnumerable<HostInfo> hosts)
    {
        var uniqueHosts = hosts
            .Where(host => !string.IsNullOrWhiteSpace(host.Id))
            .GroupBy(host => host.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(host => host.IsOnline)
                .ThenByDescending(host => host.LastSeen)
                .First())
            .ToList();
        var byId = uniqueHosts.ToDictionary(
            host => host.Id,
            StringComparer.OrdinalIgnoreCase);

        _settings.HostOrder ??= new List<string>();
        var existingOrder = _settings.HostOrder
            .Where(byId.ContainsKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var customOrder =
            _settings.HostOrderCustomized == true ||
            (_settings.HostOrderCustomized == null &&
             _settings.HostOrder.Count > 0);

        // Preserve every position already shown. Newly registered PCs are
        // appended in database creation order. Heartbeats, online state and
        // renames must never reshuffle the grid.
        var existingIds = existingOrder.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        var newIds = uniqueHosts
            .Where(host => !existingIds.Contains(host.Id))
            .OrderBy(host => host.CreatedAt == default
                ? DateTime.MaxValue
                : host.CreatedAt)
            .ThenBy(host => host.LastSeen)
            .ThenBy(host => host.Id, StringComparer.OrdinalIgnoreCase)
            .Select(host => host.Id);
        var desiredOrder = existingOrder.Concat(newIds).ToList();

        if (!_settings.HostOrder.SequenceEqual(
                desiredOrder,
                StringComparer.OrdinalIgnoreCase) ||
            _settings.HostOrderCustomized == null)
        {
            _settings.HostOrder = desiredOrder;
            _settings.HostOrderCustomized = customOrder;
            _settings.Save();
        }

        return desiredOrder
            .Where(byId.ContainsKey)
            .Select(id => byId[id])
            .ToList();
    }

    private void MarkHostOrderCustomized()
    {
        _settings.HostOrderCustomized = true;
    }
}
