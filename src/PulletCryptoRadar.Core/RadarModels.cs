namespace PulletCryptoRadar.Core;

public enum MoveDirection { Up, Down }
public enum UpMoveStage { Normal, Preheat, Ignition, Breakout, Acceleration, Overheated, Distribution, Cooling }
public enum DownMoveStage { Normal, Weakening, Breakdown, Selloff, Crash, Cooling }
public enum MarketRegime { Normal, BroadRiskOn, BroadRiskOff, SystemicPump, SystemicCrash }
public enum EpisodeState { Created, Active, Escalating, Overheated, Cooling, Closed }

public sealed record ScoreComponent(string Family, double Weight, double? Strength, bool Confirmed,
    string Description, DateTimeOffset? SourceTime);

public sealed record MoveAssessment(MoveDirection Direction, string StrategyVersion, double? Score,
    int DataConfidence, string Stage, DataStatus Status, bool IsCandidate,
    IReadOnlyList<ScoreComponent> Components, IReadOnlyList<string> ConfirmedFamilies,
    IReadOnlyList<string> MissingFamilies, IReadOnlyList<string> Reasons)
{
    public SignalRisk Risk { get; init; } = SignalRisk.Unknown;
    // Set only by a future verified WatchTower source; Phase-3 scanners never set this flag.
    public bool IsWatchListCritical { get; init; }
}

public sealed record MarketRegimeSnapshot(DateTimeOffset AsOf, MarketRegime? Regime, DataStatus Status,
    int TotalAssets, int ValidAssets, int Advancing, int Declining, int Above5, int BelowMinus5,
    int Above10, int BelowMinus10, decimal? MedianReturn1h, decimal? BtcReturn1h, decimal? EthReturn1h,
    string Reason)
{
    public double CoveragePercent => TotalAssets == 0 ? 0 : Math.Round(100.0 * ValidAssets / TotalAssets, 1);
}

public sealed record MoveEpisode
{
    public long Id { get; init; }
    public int AssetId { get; init; }
    public string Symbol { get; init; } = "";
    public MoveDirection Direction { get; init; }
    public string StrategyVersion { get; init; } = "";
    public DateTimeOffset StartTime { get; init; }
    public DateTimeOffset? EndTime { get; init; }
    public DateTimeOffset LastObservedTime { get; init; }
    public DateTimeOffset LastExtremeTime { get; init; }
    public decimal StartPrice { get; init; }
    public decimal CurrentPrice { get; init; }
    public decimal PeakPrice { get; init; }
    public decimal LowestPrice { get; init; }
    public double StartScore { get; init; }
    public double CurrentScore { get; init; }
    public double MaxScore { get; init; }
    public double LastRecordedScore { get; init; }
    public string Stage { get; init; } = "";
    public EpisodeState State { get; init; }
    public DataStatus DataStatus { get; init; }
    public string? EndReason { get; init; }
}

public sealed record RadarObservation(MarketView Input, MoveAssessment Up, MoveAssessment Down, long? EpisodeId = null);
public sealed record RadarEvent(Guid Id, Guid ScanId, long EpisodeId, int AssetId, string Symbol,
    MoveDirection Direction, string Kind, DateTimeOffset Time, string StrategyVersion,
    MarketView? Input, MoveAssessment? Assessment, MoveEpisode Episode);
public sealed record RadarSnapshot(Guid ScanId, DateTimeOffset AsOf, MarketRegimeSnapshot Market,
    IReadOnlyList<RadarObservation> Observations, IReadOnlyList<MoveEpisode> ActiveEpisodes,
    IReadOnlyList<RadarEvent> RecentEvents, string ParametersJson);
public sealed record RadarPersistenceBatch(RadarSnapshot Snapshot, IReadOnlyList<MoveEpisode> ChangedEpisodes,
    IReadOnlyList<RadarEvent> NewEvents)
{
    public ReplayFrame? ReplayFrame { get; init; }
    public int ReplayRetentionDays { get; init; } = 7;
}
public sealed record RadarRestoreState(IReadOnlyList<MoveEpisode> ActiveEpisodes, long MaxEpisodeId,
    IReadOnlyList<MoveEpisode>? LatestEpisodes = null);

public interface IRadarRepository
{
    Task<RadarRestoreState> RestoreAsync(CancellationToken ct);
    Task<IReadOnlyList<RadarEvent>> ReadEventsAsync(int? assetId, int limit, CancellationToken ct);
    Task<IReadOnlyList<MoveEpisode>> ReadEpisodesAsync(int? assetId, int limit, CancellationToken ct);
}
public interface IRadarPersistence
{
    Task SaveAsync(RadarPersistenceBatch batch, CancellationToken ct);
}
