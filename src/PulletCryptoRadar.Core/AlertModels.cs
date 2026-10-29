namespace PulletCryptoRadar.Core;

public enum SignalGrade { Unknown, C, B, A, S }
public enum SignalRisk { Unknown, Low, Medium, High, Critical }
public enum NotificationKind { Normal, Systemic, Test, WatchListCritical, WatchListPersonal }
public enum NotificationStatus { Pending, Dispatching, Submitted, Failed, Indeterminate, Expired, Disabled }

public sealed record AlertSignal(string Key, long EpisodeId, int AssetId, string Symbol, MoveDirection Direction,
    DateTimeOffset AsOf, MoveAssessment Assessment, SignalGrade Grade);
public sealed record AlertDecision(string Key, string Symbol, MoveDirection Direction, SignalGrade Grade,
    double? Score, int Confidence, string Stage, bool Eligible, string Reason, long? EpisodeId);
public sealed record NotificationRecord(Guid Id, string DedupKey, NotificationKind Kind, long? EpisodeId, int? AssetId,
    string Symbol, MoveDirection? Direction, SignalGrade Grade, double? Score, string Stage, DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt, NotificationStatus Status, string Title, string Body, string? Error = null,
    DateTimeOffset? UpdatedAt = null, string? StrategyVersion = null);
public sealed record AlertCandidate(AlertSignal Signal, DateTimeOffset FirstSeen);
public sealed record AlertPolicyState(IReadOnlyList<AlertCandidate> Candidates, string? SystemicKey = null,
    MarketRegime? SystemicRegime = null, DateTimeOffset? LastMarketObservation = null);
public sealed record AlertPolicyPlan(AlertPolicyState State, IReadOnlyList<NotificationRecord> Notifications,
    IReadOnlyList<AlertDecision> Decisions);
public sealed record AlertRun(Guid ScanId, DateTimeOffset AsOf, AlertPolicyPlan Plan, string ParametersJson);
public sealed record AlertPolicySnapshot(DateTimeOffset AsOf, Guid ScanId, IReadOnlyList<AlertDecision> Decisions,
    int DailyUsed, int DailyLimit, int WindowAssetsUsed, int WindowAssetLimit, string BudgetDate, bool ToastEnabled,
    string PolicyVersion, string? Error = null);
public sealed record AlertRestoreState(AlertPolicyState State, IReadOnlyList<NotificationRecord> Notifications, Guid? LastScanId);
public sealed record ToastResult(NotificationStatus Status, string? Error = null);
public interface IRadarSnapshotSource { RadarSnapshot? GetSnapshot(); }

public interface IAlertRepository
{
    Task<AlertRestoreState> RestoreAsync(CancellationToken ct);
    Task<IReadOnlyList<NotificationRecord>> ReadNotificationsAsync(int limit, CancellationToken ct);
}
public interface IAlertPersistence
{
    Task SaveRunAsync(AlertRun run, CancellationToken ct);
    Task<bool> TransitionAsync(Guid id, NotificationStatus expected, NotificationStatus next, string? error, DateTimeOffset now, CancellationToken ct);
}
public interface INotificationSender
{
    Task<ToastResult> SendAsync(NotificationRecord notification, CancellationToken ct);
}
