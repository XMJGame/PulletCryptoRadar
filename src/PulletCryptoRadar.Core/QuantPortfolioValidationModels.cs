namespace PulletCryptoRadar.Core;

public sealed record QuantPortfolioGate(string Key,string Name,string Status,decimal? Limit,decimal? Before,decimal? After,string Reason);
public sealed record QuantPortfolioSource(QuantEntryReview Review,QuantInputBundle? Signal);
public sealed record QuantPortfolioSourceRef(string Id,string Hash);
public sealed record QuantPortfolioFrame(string Symbol,DateTimeOffset Hour,string? Issue,IReadOnlyList<Candle> Bars);
public sealed record QuantPortfolioMarketCapture(DateTimeOffset At,IReadOnlyList<MarketView> Markets,QuantSnapshot? Snapshot,
    IReadOnlyDictionary<string,QuantPortfolioFrame> Frames,IReadOnlyDictionary<string,ExecutionPlanningInputs> Execution,
    IReadOnlyDictionary<string,FundingRateWindow?> Funding,IReadOnlyDictionary<string,QuantInputBundle> Archived,IReadOnlyList<string> Issues);
public sealed record QuantPortfolioFrozenInput(Guid Id,string Version,string Date,DateTimeOffset CapturedAt,PaperState State,
    IReadOnlyList<PaperTrade> Trades,IReadOnlyList<QuantFundingEvent> Funding,QuantPortfolioMarketCapture Market,IReadOnlyList<QuantPortfolioSourceRef> Sources);
public sealed record QuantPortfolioAuditRow(string Id,DateTimeOffset At,long Revision,string Symbol,string Phase,string Outcome,string Status,
    string Reason,string SignalStatus,int BeforeSlots,int AfterSlots,int PendingBefore,decimal Cash,decimal ReservedCash,
    decimal? AvailableAfter,decimal RiskBefore,decimal? RiskAfter,decimal MarginBefore,decimal? MarginAfter,int UnknownCorrelations,
    IReadOnlyList<QuantPortfolioGate> Checks);
public sealed record QuantPortfolioScenario(string Key,string Name,string Expected,string Actual,string Status,string Reason,
    decimal? SelectedRisk,IReadOnlyList<QuantPortfolioGate> Checks);
public sealed record QuantPortfolioResources(long Revision,DateTimeOffset At,int Positions,int Pending,decimal Cash,decimal PendingCash,
    decimal AvailableCash,decimal OriginalMargin,decimal PlannedRisk,decimal? Equity,IReadOnlyList<QuantPortfolioGate> Checks);
public sealed record QuantPortfolioValidationReport(Guid Id,string Version,string Date,DateTimeOffset CapturedAt,string InputHash,string Status,
    int SourceReviews,int Reproduced,int NotReplayed,int MissingEvidence,int Mismatches,int ConstraintFailures,int FilledReviews,
    int MaximumObservedSlots,int ActualEntries,int LinkedEntries,int UnlinkedEntries,QuantPortfolioResources Before,QuantPortfolioResources CopyAfter,
    string CopyHash,bool Repeatable,bool SourcePreserved,int CopyFills,int CopyFunding,int CopyNewPositions,
    string? ScenarioSeed,int ScenariosPassed,int ScenariosUnknown,int ScenariosFailed,IReadOnlyList<QuantPortfolioScenario> Scenarios,
    IReadOnlyList<QuantPortfolioAuditRow> Rows,bool RowsLimited,IReadOnlyList<QuantStudyGate> Gates,IReadOnlyList<string> Limitations);
public sealed record QuantPortfolioValidationRun(QuantPortfolioFrozenInput Input,QuantPortfolioValidationReport Report);
public interface IQuantPortfolioValidationRepository
{
    string SaveSource(QuantPortfolioSource source);QuantPortfolioSource ReadSource(string hash);
    QuantPortfolioValidationReport? Latest();QuantPortfolioValidationRun? Read(Guid id);void Save(QuantPortfolioValidationRun run);
}
