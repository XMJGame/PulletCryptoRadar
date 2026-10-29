namespace PulletCryptoRadar.Core;

public sealed record EvaluationOptions
{
    public int LookbackDays { get; init; } = 3;
    public int PrecisionK { get; init; } = 3;
    public int MaximumSignals { get; init; } = 5000;
    public int ReplayRetentionDays { get; init; } = 7;
    public bool IsValid() => LookbackDays is >= 1 and <= 7 && PrecisionK is >= 1 and <= 20
        && MaximumSignals is >= 100 and <= 10000 && ReplayRetentionDays is >= 2 and <= 30;
}

public sealed record EvaluationSignal(string Key, int AssetId, string Symbol, MoveDirection Direction,
    DateTimeOffset Time, decimal Price, double? Score, string StrategyVersion, long EpisodeId,
    decimal? ReferencePrice, bool IsNotification = false);
public enum EvaluationStatus { AwaitingEntry, Pending, Incomplete, Complete }
public sealed record SignalEvaluation(EvaluationSignal Signal, EvaluationStatus Status, DateTimeOffset? EntryTime,
    decimal? EntryPrice, DateTimeOffset? WindowEnd, int ObservedBars, int ExpectedBars,
    decimal? Mfe, decimal? Mae, bool? Hit20, bool? Hit50, bool? Drop20, bool? Drop30,
    decimal? CaptureLead, decimal? CrashLead, string Reason);
public sealed record DailyPrecision(string Date, MoveDirection Direction, int K, int Selected, int Completed,
    int Hits, decimal? Percent);
public sealed record ExtremeRecall(string Date, MoveDirection Direction, int ExpectedAssets, int CoveredAssets,
    int ExtremeAssets, int CapturedAssets, bool ScanCoverageComplete, decimal? Percent);
public sealed record EvaluationReport(Guid Id, DateTimeOffset AsOf, DateTimeOffset From, string Version,
    IReadOnlyList<SignalEvaluation> Signals, IReadOnlyList<DailyPrecision> Precision,
    IReadOnlyList<ExtremeRecall> Recall, bool SignalsTruncated, string Methodology);
public sealed record EvaluationData(IReadOnlyList<EvaluationSignal> Signals, IReadOnlyDictionary<int, IReadOnlyList<Candle>> Candles,
    IReadOnlyList<Instrument> Instruments, IReadOnlyList<DateTimeOffset> ScanTimes, bool Truncated);

// Captured inputs come from the live FeatureEngine. This is a feature-snapshot tape, not reconstructed tick history.
public sealed record ReplayFrame(Guid ScanId, DateTimeOffset AsOf, IReadOnlyList<MarketView> Inputs,
    string ParametersJson, string AlertParametersJson, string EngineVersion);
public sealed record ReplayRequest(DateTimeOffset From, DateTimeOffset To, string Symbol = "ARIAUSDT");
public sealed record ReplayPoint(DateTimeOffset Time, int AssetId, string Symbol, decimal? Price,
    MoveAssessment Up, MoveAssessment Down, long? EpisodeId);
public sealed record ReplayFrameCoverage(DateTimeOffset Time, int Assets, int FreshPrices);
public sealed record ReplayResult(Guid Id, DateTimeOffset From, DateTimeOffset To, int Frames, int Observations,
    int GapCount, IReadOnlyList<ReplayPoint> Trace, IReadOnlyList<EvaluationSignal> Signals,
    IReadOnlyList<NotificationRecord> SimulatedNotifications, string Mode, string Note)
{
    public IReadOnlyList<ReplayFrameCoverage> Coverage { get; init; } = [];
}
public sealed record ReplayAvailability(DateTimeOffset? First, DateTimeOffset? Last, int Frames, int EventInputs);
public sealed record ReplayAuditInput(RadarEvent Event, string ParametersJson);
public sealed record ReplayAuditResult(Guid EventId, string Symbol, DateTimeOffset Time, string StrategyVersion,
    string Status, double? SavedScore, double? ReplayedScore, string Reason);

public interface IEvaluationRepository
{
    EvaluationData ReadData(DateTimeOffset from, DateTimeOffset asOf, int maximumSignals, CancellationToken ct);
    EvaluationReport? ReadLatest();
    ReplayAvailability GetAvailability();
    IEnumerable<ReplayFrame> ReadFrames(DateTimeOffset from, DateTimeOffset to, CancellationToken ct);
    IReadOnlyList<ReplayAuditInput> ReadAuditInputs(int limit);
}
public interface IEvaluationPersistence
{
    Task SaveAsync(EvaluationReport report, CancellationToken ct);
}
