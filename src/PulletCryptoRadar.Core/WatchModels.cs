namespace PulletCryptoRadar.Core;

public enum StrategyState { Observe, PositiveObserve, HoldObserve, WaitConfirmation, AvoidChase, RiskIncreasing, ReduceRisk, CriticalRisk }
public sealed record WatchWallet(string Chain,string Address,string Role);
public sealed record WatchAsset(int AssetId,bool Enabled,decimal? CostPrice,decimal? TargetPrice,decimal? AttentionPrice,
    decimal? SupportPrice,decimal? ResistancePrice,bool EnableAlerts,IReadOnlyList<WatchWallet> Wallets,
    long Revision,DateTimeOffset CreatedAt,DateTimeOffset UpdatedAt,bool IsSpecificationExample=false);
public sealed record WatchRequest(long ExpectedRevision,bool Enabled,decimal? CostPrice,decimal? TargetPrice,decimal? AttentionPrice,
    decimal? SupportPrice,decimal? ResistancePrice,bool EnableAlerts,IReadOnlyList<WatchWallet>? Wallets=null);
public sealed record WatchInput(WatchAsset Config,SpeculativeInput Quote,MoveAssessment? Up,MoveAssessment? Down,MarketRegimeSnapshot? Market)
{ public FlowSummary Flow {get;init;}=new(); }
public sealed record WatchResult(string Version,DateTimeOffset AsOf,int AssetId,string Symbol,string Exchange,string NativeSymbol,
    DataStatus Status,FeatureValue<decimal> Price,decimal? CostReturnPercent,decimal? ToCostPercent,
    string Trend,string CurrentStage,StrategyState State,SignalRisk Risk,int Confidence,
    double? TrendScore,double? DerivativeScore,double? OnChainScore,double? WalletRiskScore,double? CrowdingRisk,
    IReadOnlyList<string> PositiveReasons,IReadOnlyList<string> RiskReasons,IReadOnlyList<string> MissingEvidence,
    IReadOnlyList<string> NextTriggers);
public sealed record WatchCheckpoint(WatchInput Input,WatchResult Result,string? ActiveCondition,Guid EpisodeId,DateTimeOffset? LastNotificationAt)
{
    public string? LastNotifiedCondition { get;init; }
    public string ParametersJson { get;init; }="";
}
public sealed record WatchCandle(int AssetId,Candle Candle);
public sealed record WatchRun(Guid Id,DateTimeOffset AsOf,IReadOnlyList<WatchCheckpoint> Checkpoints,IReadOnlyList<NotificationRecord> Notifications,
    IReadOnlyList<WatchCandle> Candles,string ParametersJson);
public sealed record WatchView(WatchAsset Config,WatchResult? Result);
public sealed record WatchAudit(DateTimeOffset AsOf,int AssetId,string Status,string Reason);
public interface IWatchRepository
{
    IReadOnlyList<WatchAsset> Configs();
    IReadOnlyList<WatchCheckpoint> Checkpoints();
    IReadOnlyList<WatchCheckpoint> History(int assetId,int limit);
}
public interface IWatchPersistence
{
    Task SaveConfigAsync(WatchAsset config,long expectedRevision,CancellationToken ct);
    Task SaveRunAsync(WatchRun run,CancellationToken ct);
}
