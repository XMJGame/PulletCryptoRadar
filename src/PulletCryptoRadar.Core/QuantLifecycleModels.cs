namespace PulletCryptoRadar.Core;

public sealed record QuantLifecycleCheck(string Key,string Name,string Status,decimal? Expected,decimal? Actual,string Reason);
public sealed record QuantLifecycleLeg(Guid TradeId,DateTimeOffset ClosedAt,string Reason,decimal Quantity,decimal ExitPrice,decimal GrossPnl,
    decimal? EntryFee,decimal? ExitFee,decimal FundingCost,decimal NetPnl,bool FinalExit,DateTimeOffset? TriggerAfter,DateTimeOffset? BookAt,DateTimeOffset? BookReceivedAt)
{public QuantExitTiming? Timing {get;init;}}
public sealed record QuantExitTiming(string Status,QuantExitObservation? Observation,DateTimeOffset ComputedAt,DateTimeOffset? BookReceivedAt,
    decimal? ReceiveToObservationMs,decimal? ObservationToComputationMs,decimal? BookReceiveToComputationMs,bool? BookCachedAtObservation,string Meaning);
public sealed record QuantLifecycle(string Version,DateTimeOffset AsOf,string Status,decimal InitialQuantity,decimal ExitedQuantity,decimal RemainingQuantity,
    decimal InitialMargin,decimal EntryFee,decimal? ExitFees,decimal SettledFunding,decimal OpenFunding,decimal LateFundingAdjustment,decimal ObservedFunding,
    decimal RealizedNet,decimal? FinalNet,DateTimeOffset? ClosedAt,string FundingStatus,DateTimeOffset? FundingKnownThrough,
    decimal? CurrentStop,DateTimeOffset HoldUntil,string? PendingExit,DateTimeOffset? ExitAfter,
    IReadOnlyList<QuantLifecycleCheck> Checks,IReadOnlyList<QuantLifecycleLeg> Legs)
{[System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public QuantExitAttribution? Attribution {get;init;}}
