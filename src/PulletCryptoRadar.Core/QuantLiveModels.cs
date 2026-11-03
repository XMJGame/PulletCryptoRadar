namespace PulletCryptoRadar.Core;
public sealed record QuantLiveControl(string Version,DateTimeOffset ActivatedAt,string RulesHash,QuantParameters Parameters,ExecutionCostModel Costs);
public sealed record QuantLiveIntent(QuantHypothesis Decision,QuantAssessment Assessment);
public sealed record QuantExitObservation(string Version,string Reason,DateTimeOffset? SourceAt,DateTimeOffset? ReceivedAt,DateTimeOffset ObservedAt,
    decimal TriggerPrice,decimal StopAtObservation,DateTimeOffset PreviousQuoteSourceAt);
public sealed record QuantLivePosition(QuantParameters Parameters,ExecutionCostModel Costs,QuantPlan Plan,ExecutionQuote EntryQuote,
    DateTimeOffset LastTrailHour,decimal TrailPeak)
{
    public string? PendingExit {get;init;}
    public DateTimeOffset? ExitAfter {get;init;}
    public DateTimeOffset? LastFillAt {get;init;}
    public DateTimeOffset StopEffectiveAfter {get;init;}
    public decimal FirstReduced {get;init;}
    public decimal SecondReduced {get;init;}
    public bool SkipFirst {get;init;}
    public bool SkipSecond {get;init;}
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public QuantExitObservation? ExitObservation {get;init;}
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public DateTimeOffset? ExitComputedAt {get;init;}
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public string? EntryImplementationVersion {get;init;}
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public string? EntryReviewId {get;init;}
}
public sealed record QuantFundingEvent(string Key,DateTimeOffset BookedAt,decimal OpenAllocated,decimal ClosedAllocated,FundingAuditPayment Evidence);
public sealed record QuantFundingCoverage(Guid PositionId,string Symbol,string Status,DateTimeOffset? KnownThrough,DateTimeOffset CheckedAt,string Reason);
public sealed record QuantLiveStatus(string Mode,QuantLiveControl? Control,int NewPositions,int OldPositions,int Pending,
    IReadOnlyList<QuantFundingCoverage> Funding,decimal LateFundingAdjustment,IReadOnlyList<string> Notices)
{public string? EntryImplementationVersion {get;init;}}
