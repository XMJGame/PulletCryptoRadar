namespace PulletCryptoRadar.Core;

public sealed record QuantLedgerPosition(Guid Id,string DecisionId,string Symbol,string Direction,DateTimeOffset OpenedAt,
    decimal InitialQuantity,decimal Quantity,decimal Entry,int Leverage,decimal Stop,decimal Target1,decimal Target2,decimal PlannedRisk,
    decimal Collateral,decimal EntryFeeRemaining,decimal FundingRemaining,DateTimeOffset? ClosedAt,IReadOnlyList<ExposureReduction> Reductions);
public sealed record QuantLedgerFill(string Key,Guid PositionId,string Kind,DateTimeOffset At,decimal Quantity,decimal Price,
    decimal Gross,decimal Fee,decimal ReleasedCollateral,decimal AllocatedEntryFee,decimal AllocatedFunding,decimal NetPnl,ExecutionQuote Evidence);
public sealed record QuantLedgerFunding(string Key,DateTimeOffset BookedAt,decimal OpenAllocated,decimal ClosedAllocated,FundingAuditPayment Evidence);
public sealed record QuantRehearsalLedger(string Version,string BaselineHash,PaperState Baseline,DateTimeOffset EffectiveAt,decimal Cash,
    IReadOnlyList<QuantLedgerPosition> Positions,IReadOnlyList<string> UsedSignals,IReadOnlyList<QuantLedgerFill> Fills,IReadOnlyList<QuantLedgerFunding> Funding);
public sealed record QuantRehearsalOperation(string Status,string Reason,QuantRehearsalLedger Ledger,QuantAssessment? Assessment=null,ExecutionQuote? Quote=null);
public sealed record QuantRehearsalInput(Guid Id,DateTimeOffset CapturedAt,PaperOverview Paper,IReadOnlyList<PaperTrade> LegacyTrades,QuantCohort? Cohort,QuantSnapshot? Shadow,
    IReadOnlyList<MarketView> Markets,IReadOnlyDictionary<string,IReadOnlyList<Candle>> Hourly,
    IReadOnlyDictionary<string,ExecutionPlanningInputs> Execution,IReadOnlyDictionary<string,FundingRateWindow?> Funding,
    ExecutionCostModel Costs)
{public IReadOnlyDictionary<string,QuantInputBundle> ArchivedSignals {get;init;}=new Dictionary<string,QuantInputBundle>();}
public sealed record QuantRehearsalOutcome(string DecisionId,string Symbol,string Strategy,string Direction,string Status,string Reason,QuantPlan? Plan);
public sealed record QuantRehearsalReport(Guid Id,string Version,DateTimeOffset AsOf,string InputHash,long SourceRevision,string LegacyVersion,
    decimal SourceCash,decimal CopyCash,int LegacyPositions,int LegacyPending,int PriceCandidates,int ForwardIntents,int CopiedEntries,
    IReadOnlyList<QuantRehearsalOutcome> Outcomes,IReadOnlyList<FundingAuditResult> FundingComparison,IReadOnlyList<ExecutionQuote> LegacyExitQuotes,
    IReadOnlyList<QuantStudyGate> Checks,IReadOnlyList<string> Limitations);
public sealed record QuantRehearsalRun(QuantRehearsalInput Input,QuantRehearsalReport Report,QuantRehearsalLedger Ledger);
public interface IQuantRehearsalRepository
{QuantRehearsalRun? Latest();void Save(QuantRehearsalRun run);}
