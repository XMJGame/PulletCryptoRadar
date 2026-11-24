namespace PulletCryptoRadar.Core;

public sealed record ArchiveProof(string Symbol,string Kind,string Period,string Url,string Sha256,int Bytes,DateTimeOffset DownloadedAt);
public sealed record ArchivedFunding(DateTimeOffset At,int IntervalHours,decimal Rate);
public sealed record QuantResearchDataset(string Symbol,DateTimeOffset Start,DateTimeOffset End,string Partition,
    IReadOnlyList<HourCandle> Candles,IReadOnlyList<HourCandle> MarkCandles,IReadOnlyList<OpenInterestPoint> OpenInterest,
    IReadOnlyList<ArchivedFunding> Funding,IReadOnlyList<ArchiveProof> Proofs,IReadOnlyList<string> Missing);
public sealed record QuantReplayTrade(EntryStrategy Strategy,TradeSide Side,DateTimeOffset OpenedAt,DateTimeOffset? ClosedAt,
    decimal Entry,decimal InitialStop,decimal Quantity,decimal NetPnl,decimal Fees,decimal Funding,decimal MfeR,decimal MaeR,string Status);
public sealed record ReplayEntryDiagnostic(EntryStrategy Strategy,TradeSide Side,string Reason,int Count);
public sealed record QuantReplayResult(string Symbol,string Partition,string RegimeScenario,string CostScenario,
    int DecisionBars,int OiMissingBars,int CandleMissingBars,int Confirmations,int SizingRejected,
    decimal InitialCash,decimal FinalCash,decimal MaximumEquityDrawdownPercent,IReadOnlyList<QuantReplayTrade> Trades,
    IReadOnlyDictionary<string,int> Reasons,IReadOnlyList<string> Limitations,IReadOnlyList<ReplayEntryDiagnostic>? EntryDiagnostics=null);
public sealed record QuantDatasetEvidence(string Symbol,string Partition,DateTimeOffset Start,DateTimeOffset End,string DatasetSha256,
    int Candles,int OiPoints,int FundingSettlements,int VerifiedArchives,IReadOnlyList<string> Missing);
public sealed record QuantReplayReport(string Version,DateTimeOffset At,IReadOnlyList<QuantReplayResult> Results,IReadOnlyList<string> Limitations,
    IReadOnlyList<QuantDatasetEvidence>? Datasets=null);
