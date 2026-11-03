namespace PulletCryptoRadar.Core;
public sealed record PaperAnalysisAudit(long Revision,DateTimeOffset Time,IReadOnlyList<ForwardDecision> Signals,IReadOnlyList<PaperDecision> Decisions);
public sealed record PaperAuditWindow(IReadOnlyList<PaperAnalysisAudit> Records,bool Truncated);
public sealed record ActivityCount(string Name,int Count);
public sealed record PaperAnalysisHour(DateTimeOffset Hour,int Symbols,int Observations,int Insufficient,int NoSignal,int RiskSkipped,int Candidates,int OiEligible);
public sealed record PaperActivity(DateTimeOffset AsOf,DateTimeOffset From,DateTimeOffset AccountUpdatedAt,string Status,string Summary,bool Enabled,
    int OpenPositions,int MaximumPositions,int Pending,int UniverseSymbols,DateTimeOffset? LastAnalysis,DateTimeOffset? LastSignalHour,
    int Analyses,int UniqueSymbols,int Hours,int Insufficient,int NoSignal,int RiskSkipped,int Candidates,int OiEligibleCandidates,
    int IntentCreated,int Openings,int Cancellations,bool AuditTruncated,DateTimeOffset? EarliestAnalysis,
    IReadOnlyList<ActivityCount> Reasons,IReadOnlyList<ActivityCount> FailedConditions,IReadOnlyList<PaperAnalysisHour> Hourly,
    IReadOnlyList<ForwardDecision> BlockedPlans,IReadOnlyList<PaperDecision> ExecutionEvents,IReadOnlyList<string> Notices);
