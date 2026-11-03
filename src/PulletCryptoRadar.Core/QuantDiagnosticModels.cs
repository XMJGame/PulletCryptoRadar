namespace PulletCryptoRadar.Core;

public sealed record QuantDiagnosticMetrics(int Samples,int Completed,int Unfinished,int InvalidAccounting,
    decimal? MeanNetR,decimal? MeanRawGrossR,decimal? MeanSlippageR,decimal? MeanFeeR,decimal? MeanFundingR,
    decimal? MeanHoldingHours,decimal SumNetR,int CostFlipped);
public sealed record QuantDiagnosticGroup(string Dimension,string Key,QuantDiagnosticMetrics Metrics);
public sealed record QuantDiagnosticConcentration(string Dimension,IReadOnlyList<string> RemovedPositiveGroups,
    int RemainingCompleted,decimal? MeanWithoutTopThree,decimal? TopThreeShareOfPositiveGroupNet);
public sealed record QuantDiagnosticPairing(int Matched,int TailOnly,int FullOnly,int UnfinishedPairs,int InvalidPairs,
    decimal? MeanTailMinusFullR,int TailBetter,int FullBetter,int Equal);
public sealed record QuantDiagnosticReport(string Version,Guid StudyId,string StudyHash,string TradeHash,string Window,string Strategy,string Policy,int CostMultiplier,
    QuantDiagnosticMetrics Metrics,IReadOnlyList<QuantDiagnosticGroup> Groups,IReadOnlyList<QuantDiagnosticConcentration> Concentration,
    QuantDiagnosticPairing Pairing,IReadOnlyList<string> Notes);
