namespace PulletCryptoRadar.Core;
public sealed record DailyMonitorReview(string TargetId,string Symbol,string ContractAddress,int Snapshots,int ObservedHalfHourSlots,int CompletedDaySlots,
    DateTimeOffset? FirstSource,DateTimeOffset? LastSource,bool HasComparable24hBaseline,string Finding,
    string? RecordedReleaseDelta,string? ReleaseWindow,string? EarliestOrdinaryRelease7Days,int? ObservedTransfers,
    int? ConfirmedExchangeInflows,int? CandidateExchangeInflows,IReadOnlyList<string> Gaps);
public sealed record DailyOutcomeGroup(string Verdict,int Hours,int Reports,int Observed,int NotDue,int Missing,int NoBenchmark,decimal? MeanPriceChange,
    int BelowInvalidation);
public sealed record DailyResearchReview(string Version,DateOnly Date,DateTimeOffset AsOf,int Reports,IReadOnlyList<DailyMonitorReview> Monitoring,
    IReadOnlyList<DailyOutcomeGroup> Outcomes,IReadOnlyList<string> Missing,IReadOnlyList<string> Review);
public interface IDailyResearchRepository
{
    void Save(DailyResearchReview review);
    DailyResearchReview? Latest(DateOnly date);
}
