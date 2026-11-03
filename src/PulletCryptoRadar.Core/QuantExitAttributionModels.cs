namespace PulletCryptoRadar.Core;

public sealed record QuantExitBudget(string Status,decimal Risk,decimal? StopLoss,decimal? EntryFee,decimal? ExitFeeReserve,
    decimal? ResidualReserve,decimal? FundingReserve,decimal? GapReserve,decimal? Sum,decimal? Difference,string Reason);
public sealed record QuantExitDifference(string Key,string Name,decimal? Value,string Meaning);
public sealed record QuantExitPriceLeg(Guid TradeId,DateTimeOffset ClosedAt,string Reason,decimal Quantity,bool FinalExit,bool QuoteGapObserved,
    string BookStatus,string ObservationStatus,decimal InitialStop,decimal? Reference,string ReferenceBasis,decimal? ObservedPrice,
    decimal? Mid,decimal? Best,decimal? Vwap,decimal Fill,decimal? OriginalStopToMid,decimal? ReferenceShift,decimal? ReferenceToObserved,
    decimal? ObservedToMid,decimal? HalfSpread,decimal? Depth,decimal? Residual,decimal? PriceDifference,decimal? BridgeDifference,
    IReadOnlyList<string> Issues);
public sealed record QuantExitAttribution(string Version,string Status,string BridgeBasis,QuantExitBudget Budget,decimal? FinalNet,
    decimal? ActualLoss,decimal? SignedRiskDifference,decimal? LossBeyondPlan,decimal? LossBeyondPlanPercent,
    decimal? BridgeSum,decimal? BridgeDifference,int RecordedObservations,int MissingObservations,int InvalidObservations,
    IReadOnlyList<QuantExitDifference> Parts,IReadOnlyList<QuantExitPriceLeg> Legs,IReadOnlyList<string> Notices);
