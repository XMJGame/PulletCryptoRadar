namespace PulletCryptoRadar.Core;

public sealed record CandidatePattern(EntryStrategy Strategy,TradeSide Side,string Status,DateTimeOffset AsOf,
    IReadOnlyList<string> Reasons);
public sealed record CandidateDiagnostic(string Symbol,Guid? AssetId,string State,bool Priority,bool Focus,
    DateTimeOffset? LastEvaluatedAt,IReadOnlyList<CandidatePattern> Patterns,
    string? LastAdmission,DateTimeOffset? LastAdmissionAt);
public sealed record CandidateDiagnostics(DateTimeOffset AsOf,bool UniverseFresh,int EligibleContracts,
    int BackgroundCallsAvailable,IReadOnlyList<CandidateDiagnostic> Candidates);
