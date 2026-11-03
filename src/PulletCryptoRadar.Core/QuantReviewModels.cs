namespace PulletCryptoRadar.Core;

public sealed record QuantReviewTrade(Guid PositionId,string Symbol,string Strategy,string Direction,string Environment,DateTimeOffset OpenedAt,
    DateTimeOffset? ClosedAt,bool OpenNow,string Status,decimal PlannedRisk,decimal? GrossPnl,decimal? Fees,decimal? Funding,decimal? NetCost,decimal? NetPnl,
    decimal? NetR,decimal? CostToAbsoluteGrossPercent,decimal? LossBeyondPlan,decimal? LossBeyondPlanPercent,string Reason)
{[System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public QuantExitAttribution? Attribution {get;init;}}
public sealed record QuantReviewMetrics(int Confirmed,int Wins,int Losses,int Flat,decimal GrossPnl,decimal Fees,decimal Funding,decimal NetCost,decimal NetPnl,
    decimal PlannedRisk,decimal? WinRate,decimal? AverageNet,decimal? AverageR,decimal? ProfitFactor,decimal? CostToAbsoluteGrossPercent,int RiskOverruns,
    decimal LossBeyondPlan,decimal? WorstR);
public sealed record QuantReviewGroup(string Dimension,string Value,QuantReviewMetrics Metrics);
public sealed record QuantReviewPeriod(string Kind,DateTimeOffset From,DateTimeOffset Through,int OpenPositions,int AwaitFunding,int Incomplete,int Inconsistent,
    QuantReviewMetrics Metrics,IReadOnlyList<QuantReviewGroup> Groups,IReadOnlyList<QuantReviewTrade> Trades,bool TradesLimited);
public sealed record QuantReviewTiming(string Metric,int Samples,decimal? MedianMs,decimal? P95Ms,decimal? MaximumMs);
public sealed record QuantReviewQuality(int ExitLegs,int RecordedTiming,int HistoricalTiming,int InvalidTiming,IReadOnlyList<QuantReviewTiming> Timings,
    bool AccountStale,int FinishedHours,int MissingHours,int IncompleteHours,int FrozenBaselineIssues);
public sealed record QuantReviewReadiness(string Status,decimal ElapsedDays,int ConfirmedClosed,int MinimumDays,int MinimumClosed,int OpenPositions,
    int AwaitFunding,int Incomplete,int Inconsistent,DateTimeOffset FirstReviewAfter,string Reason);
public sealed record QuantReview(string Version,DateTimeOffset AsOf,QuantReviewPeriod Daily,QuantReviewPeriod Week,QuantReviewQuality Quality,
    QuantReviewReadiness Readiness,IReadOnlyList<string> Notices);
