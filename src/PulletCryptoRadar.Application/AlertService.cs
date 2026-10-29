using System.Text.Json;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Scanner;

namespace PulletCryptoRadar.Application;

public sealed class AlertService(IRadarSnapshotSource radar, AlertPolicyEngine policy, AlertOptions options,
    IAlertRepository repository, IAlertPersistence persistence, INotificationSender sender, TimeProvider time,IWatchRepository? watchRepository=null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AlertPolicySnapshot? _snapshot;
    private Guid? _lastScan;
    private AlertPolicyState _state = new([]);
    private List<NotificationRecord> _notifications = [];
    public AlertPolicySnapshot? GetSnapshot() => Volatile.Read(ref _snapshot);
    public Task<IReadOnlyList<NotificationRecord>> GetNotificationsAsync(int limit, CancellationToken ct)
        => repository.ReadNotificationsAsync(Math.Clamp(limit, 1, 200), ct);

    public async Task TickAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var now = time.GetUtcNow();
            // Read authoritative reservations each tick, including commits whose acknowledgement was lost.
            {
                var restored = await repository.RestoreAsync(ct);
                _state = restored.State; _notifications = restored.Notifications.ToList(); _lastScan = restored.LastScanId;
                // The OS may already have accepted this message before the process stopped. Never resend blindly.
                foreach (var n in _notifications.Where(n => n.Status == NotificationStatus.Dispatching).ToArray())
                    await Transition(n, NotificationStatus.Indeterminate, "上次进程中断，无法确认 Windows 是否接收；不自动重发", now, ct);
            }
            if (radar.GetSnapshot() is { ScanId: var scanId } scan && scanId != Guid.Empty && scanId != _lastScan)
            {
                var plan = policy.Evaluate(scan, _state, _notifications, now);
                await persistence.SaveRunAsync(new(scanId, now, plan, JsonSerializer.Serialize(options)), ct);
                var committed = await repository.RestoreAsync(ct);
                _state = committed.State; _notifications = committed.Notifications.ToList(); _lastScan = committed.LastScanId;
                Volatile.Write(ref _snapshot, new(now, scanId, plan.Decisions, policy.DailyUsed(_notifications, now), options.DailyNormalLimit,
                    policy.WindowAssetsUsed(_notifications, now), options.MaxNewAssetsPerWindow, policy.BudgetDate(now), options.ToastEnabled, AlertPolicyEngine.Version));
            }
            foreach (var n in _notifications.Where(n => n.Status == NotificationStatus.Pending).ToArray())
            {
                if (now >= n.ExpiresAt) { await Transition(n, NotificationStatus.Expired, "预警已过期，不补发旧行情", now, ct); continue; }
                if(n.StrategyVersion is WatchStrategyEngine.Version or WatchOnChainStrategyEngine.Version or WatchFlowStrategyEngine.Version&&watchRepository?.Configs().Any(c=>c.AssetId==n.AssetId&&c.Enabled&&c.EnableAlerts)!=true)
                {await Transition(n,NotificationStatus.Disabled,"个人监控或个人提醒已关闭",now,ct);continue;}
                if (!options.ToastEnabled) { await Transition(n, NotificationStatus.Disabled, "Windows 通知已关闭", now, ct); continue; }
                if (!await Transition(n, NotificationStatus.Dispatching, null, now, ct)) continue;
                ToastResult result;
                try { result = await sender.SendAsync(n, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { result = new(NotificationStatus.Indeterminate, ex.Message); }
                var dispatching = n with { Status = NotificationStatus.Dispatching };
                await Transition(dispatching, result.Status, result.Error, time.GetUtcNow(), ct);
            }
        }
        finally { _gate.Release(); }
    }
    private async Task<bool> Transition(NotificationRecord record, NotificationStatus status, string? error, DateTimeOffset now, CancellationToken ct)
    {
        var changed = await persistence.TransitionAsync(record.Id, record.Status, status, error, now, ct);
        if (changed)
        {
            var index = _notifications.FindIndex(n => n.Id == record.Id);
            _notifications[index] = record with { Status = status, Error = error, UpdatedAt = now };
        }
        return changed;
    }
}
