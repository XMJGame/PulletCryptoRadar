namespace PulletCryptoRadar.Core;

public sealed record QuantSizingLimits(decimal MaximumQuantity,int MaximumLeverage,decimal MaximumRisk,decimal RequiredStop);
public sealed record QuantEntryValue(string Key,string Name,decimal? Approved,decimal? Proposed,decimal? Selected,bool? Passed,string Meaning);
public sealed record QuantEntryFrame(string Symbol,DateTimeOffset Hour,string? Issue,IReadOnlyList<Candle> Bars);
public sealed record QuantEntryEvidence(QuantMarketInput? Market,QuantMarketContext? Context,QuantPortfolio Portfolio,decimal CashReserved,ExecutionPlanningInputs? Execution,
    IReadOnlyList<QuantEntryFrame> Frames,IReadOnlyList<QuantFundingCoverage> Funding,bool SignalVerified);
public sealed record QuantEntryReview(string Id,string Version,DateTimeOffset At,long Revision,string Phase,string Outcome,string Reason,
    PaperIntent Intent,QuantLiveControl Control,QuantAssessment? Proposed,QuantAssessment? Selected,IReadOnlyList<QuantEntryValue> Values,QuantEntryEvidence Evidence);
public sealed record QuantEntrySummary(string Id,string Version,DateTimeOffset At,long Revision,string Symbol,string Direction,string DecisionId,string Phase,string Outcome,string Reason,
    IReadOnlyList<QuantEntryValue> Values,bool Capped,DateTimeOffset? BookAt);
public sealed record QuantEntryWindow(IReadOnlyList<QuantEntrySummary> Records,bool Truncated);
