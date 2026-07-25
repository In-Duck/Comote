using System.Collections.Concurrent;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Comote.Shared;

namespace Viewer;

public partial class MainWindow
{
    private Button? _fleetUpdateButton;
    private int _fleetUpdateInProgress;
    private int _pendingUpdateDispatchInProgress;
    private readonly ConcurrentDictionary<string, DateTime> _pendingUpdateSentAt =
        new(StringComparer.OrdinalIgnoreCase);

    private async Task RequestAllClientUpdatesAsync()
    {
        if (Interlocked.Exchange(ref _fleetUpdateInProgress, 1) != 0)
            return;

        if (_fleetUpdateButton != null)
            _fleetUpdateButton.IsEnabled = false;

        try
        {
            _statusBarText.Text = "Client 최신 버전 확인 중…";
            await ClientUpdateCatalog.RefreshAsync();

            var registered = _currentHosts
                .GroupBy(host => host.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            var eligible = registered
                .Where(host => host.HasClientUpdate &&
                               host.SupportsManagedUpdate)
                .ToList();
            var online = eligible.Where(host => host.IsOnline).ToList();
            var offline = eligible.Where(host => !host.IsOnline).ToList();
            var manual = registered
                .Where(host => host.HasClientUpdate &&
                               !host.SupportsManagedUpdate)
                .ToList();

            if (eligible.Count == 0)
            {
                var message = manual.Count > 0
                    ? $"자동 업데이트 가능한 PC는 없습니다.\n\n" +
                      $"구형 Client {manual.Count}대는 최신 설치 파일을 한 번 직접 실행해야 합니다."
                    : $"등록된 Client {registered.Count}대가 모두 최신 버전입니다.";
                MessageBox.Show(
                    message,
                    "전체 Client 업데이트",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                _statusBarText.Text = "전체 Client 최신 상태";
                return;
            }

            var detail =
                $"대상 버전: {ClientUpdateCatalog.LatestVersion}\n" +
                $"온라인 즉시 업데이트: {online.Count}대\n" +
                $"오프라인 다음 연결 시 예약 업데이트: {offline.Count}대";
            if (manual.Count > 0)
                detail += $"\n구형 Client 수동 설치 필요: {manual.Count}대";

            if (MessageBox.Show(
                    $"등록된 Client를 일괄 업데이트할까요?\n\n{detail}",
                    "전체 Client 업데이트",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            QueueClientUpdates(eligible);
            var sent = await SendClientUpdateRequestsAsync(
                online,
                trackPendingResult: true);
            _statusBarText.Text =
                $"전체 업데이트 · 즉시 전송 {sent}/{online.Count} · " +
                $"예약 {offline.Count}";

            MessageBox.Show(
                $"일괄 업데이트 요청이 등록되었습니다.\n\n" +
                $"온라인 전송: {sent}/{online.Count}대\n" +
                $"오프라인 예약: {offline.Count}대\n" +
                (manual.Count > 0
                    ? $"수동 설치 필요: {manual.Count}대"
                    : "수동 설치 필요: 없음"),
                "전체 Client 업데이트",
                MessageBoxButton.OK,
                sent == online.Count
                    ? MessageBoxImage.Information
                    : MessageBoxImage.Warning);
        }
        finally
        {
            if (_fleetUpdateButton != null)
                _fleetUpdateButton.IsEnabled = true;
            Interlocked.Exchange(ref _fleetUpdateInProgress, 0);
        }
    }

    private async Task<int> SendClientUpdateRequestsAsync(
        IReadOnlyCollection<HostInfo> targets,
        bool trackPendingResult = false)
    {
        if (targets.Count == 0)
            return 0;

        var sent = 0;
        await Parallel.ForEachAsync(
            targets,
            new ParallelOptions { MaxDegreeOfParallelism = 8 },
            async (host, _) =>
            {
                try
                {
                    if (_isHubMode && _hubServer != null)
                    {
                        await _hubServer.SendCommandAsync(
                            host.Id,
                            "update",
                            "Comote Client 업데이트",
                            "",
                            ProductUpdateSettings.ClientManifestUrl);
                    }
                    else if (_signaling != null)
                    {
                        await _signaling.SendSignalAsync(host.Id, new
                        {
                            type = "comote-command",
                            action = "update",
                        });
                    }
                    else
                    {
                        return;
                    }

                    if (trackPendingResult)
                        _pendingUpdateSentAt[host.Id] = DateTime.UtcNow;
                    Interlocked.Increment(ref sent);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"[Updater] Request failed for {host.Name}: {ex.Message}");
                }
            });

        return sent;
    }

    private void QueueClientUpdates(IEnumerable<HostInfo> targets)
    {
        var pending = (_settings.PendingClientUpdateHostIds ?? new List<string>())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var host in targets)
            pending.Add(host.Id);
        _settings.PendingClientUpdateHostIds = pending.ToList();
        _settings.Save();
    }

    private void ClearPendingClientUpdates(IEnumerable<string> hostIds)
    {
        var completed = hostIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (completed.Count == 0) return;
        _settings.PendingClientUpdateHostIds ??= new List<string>();
        foreach (var hostId in completed)
            _pendingUpdateSentAt.TryRemove(hostId, out _);
        if (_settings.PendingClientUpdateHostIds.RemoveAll(completed.Contains) > 0)
            _settings.Save();
    }

    private async Task DispatchPendingClientUpdatesAsync()
    {
        if (Interlocked.Exchange(ref _pendingUpdateDispatchInProgress, 1) != 0)
            return;

        try
        {
            var pending = (_settings.PendingClientUpdateHostIds ?? new List<string>())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (pending.Count == 0) return;

            var completed = _currentHosts
                .Where(host => pending.Contains(host.Id) && !host.HasClientUpdate)
                .Select(host => host.Id)
                .ToList();
            ClearPendingClientUpdates(completed);
            pending.ExceptWith(completed);

            var ready = _currentHosts
                .Where(host => pending.Contains(host.Id) &&
                               host.IsOnline &&
                               host.HasClientUpdate &&
                               host.SupportsManagedUpdate &&
                               ShouldRetryPendingUpdate(host.Id))
                .ToList();
            if (ready.Count == 0) return;

            var sent = await SendClientUpdateRequestsAsync(
                ready,
                trackPendingResult: true);
            if (sent > 0)
                _statusBarText.Text =
                    $"예약된 Client 업데이트 전송 · {sent}/{ready.Count}";
        }
        finally
        {
            Interlocked.Exchange(ref _pendingUpdateDispatchInProgress, 0);
        }
    }

    private bool ShouldRetryPendingUpdate(string hostId)
    {
        return !_pendingUpdateSentAt.TryGetValue(hostId, out var sentAt) ||
               DateTime.UtcNow - sentAt >= TimeSpan.FromMinutes(2);
    }

    private void MarkPendingClientUpdateFailed(string hostId)
    {
        _pendingUpdateSentAt.TryRemove(hostId, out _);
    }
}
