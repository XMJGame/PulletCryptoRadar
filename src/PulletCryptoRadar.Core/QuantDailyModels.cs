namespace PulletCryptoRadar.Core;

public sealed record QuantDailyHour(DateTimeOffset Hour,int ExpectedSymbols,int FrozenSymbols,int Observations,int EligibleObservations,int Candidates,int Insufficient,string Status);
public sealed record QuantDailyCandidate(string Id,string Symbol,string Strategy,string Direction,DateTimeOffset CapturedAt,string Outcome,string Reason,int AssessmentSamples,DateTimeOffset? FirstReadyAt);
public sealed record QuantDailyInputIssue(DateTimeOffset Hour,string Symbol,string Category,string Reason,int Judgments);
public sealed record QuantDailyFunnel(int Observations,int PriceCandidates,int EverReady,int AwaitDataOnly,int RejectedOnly,int Unassessed,int ObservedIntents,int Openings,int Cancellations,int ReadyWithoutObservedIntent);
public sealed record QuantDailyCosts(int SettlementLegs,int FinalExits,decimal GrossPnl,decimal Fees,decimal SettledFunding,decimal LateFundingAdjustment,decimal RealizedNet,decimal EntryFeesPaid,decimal FundingBooked);
public sealed record QuantDailyPath(Guid PositionId,string Symbol,string Strategy,string Direction,DateTimeOffset OpenedAt,DateTimeOffset IntentAt,DateTimeOffset SignalAt,
    string SignalId,decimal Entry,int Leverage,decimal InitialQuantity,decimal RemainingQuantity,decimal InitialMargin,decimal PlannedRisk,decimal InitialStop,decimal Target1,decimal Target2,
    string StopBasis,string Target1Basis,string Target2Basis,string Environment,decimal? VolumeRatio,decimal? Oi1h,decimal? Oi24h,
    DateTimeOffset? BookAt,DateTimeOffset? BookReceivedAt,bool SubsequentBook,bool InputArchived,bool OpenNow,int SettlementLegs,decimal SettledNet,
    int FreshSamples,decimal? SampledBestPricePercent,decimal? SampledWorstPricePercent,bool SamplesLimited,string FundingStatus,decimal? OpenGrossPnl,decimal RemainingOpenFee,decimal AllocatedOpenFunding,IReadOnlyList<string> Exits)
{public string? EntryImplementationVersion {get;init;} public string? EntryReviewId {get;init;}
 [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public QuantLifecycle? Lifecycle {get;init;}}
public sealed record QuantDailyReport(string Version,string Date,DateTimeOffset GeneratedAt,DateTimeOffset From,DateTimeOffset Through,bool DayEnded,
    QuantLiveControl Control,long AccountRevision,DateTimeOffset AccountUpdatedAt,bool AccountStale,QuantDailyFunnel Funnel,QuantDailyCosts Costs,
    IReadOnlyList<QuantDailyHour> Hours,IReadOnlyList<ActivityCount> Reasons,IReadOnlyList<ActivityCount> FailedConditions,IReadOnlyList<QuantDailyCandidate> Candidates,
    IReadOnlyList<QuantDailyPath> Paths,IReadOnlyList<PaperDecision> ExecutionEvents,bool AuditTruncated,bool PathsLimited,IReadOnlyList<string> Notices)
{public QuantEntryWindow EntryReviews {get;init;}=new([],false); public DateTimeOffset? EntryDiagnosticsStartedAt {get;init;} public string? EntryImplementationVersion {get;init;}
 public IReadOnlyList<QuantDailyInputIssue> InputIssues {get;init;}=[];
 [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public PaperPerformance? AccountPerformance {get;init;}
 [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public QuantReview? Review {get;init;}}
public interface IQuantDailyRepository
{
    void Save(QuantDailyReport report);
    QuantDailyReport? Read(QuantLiveControl control,DateOnly date);
}
