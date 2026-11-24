namespace PulletCryptoRadar.Core;

public sealed record BookLevel(decimal Price, decimal Quantity);
public sealed record OrderBook(Guid AssetId, long UpdateId, DateTimeOffset SourceTime, DateTimeOffset EventTime,
    DateTimeOffset ReceivedTime, IReadOnlyList<BookLevel> Bids, IReadOnlyList<BookLevel> Asks);
public sealed record FundingSettlement(Guid AssetId, DateTimeOffset SettledAt, DateTimeOffset ReceivedTime,
    decimal Rate, decimal? MarkPrice, string RateType);
public sealed record ContractRules(string Symbol, decimal MinimumQuantity, decimal MaximumQuantity,
    decimal StepSize, decimal MinimumNotional, decimal PriceTick);
public sealed record ExecutionEvidence(Guid AssetId, DateTimeOffset AsOf, ProviderRead Book, ProviderRead Funding);
public sealed record SizingAssumptions(string Version, decimal FeePerSide = .0006m, decimal StopSlippageFraction = .001m,
    decimal FundingReserveFraction = .001m, decimal MaximumSpreadFraction = .002m, decimal MaximumEntrySlippageFraction = .001m);
public sealed record SizingProposal(string Version, Guid AssetId, TradeSide Side, DateTimeOffset AsOf,
    bool ExecutionAllowed, decimal? Quantity, decimal? AverageEntry, decimal? Notional, decimal? Margin,
    decimal? PlannedLossIncludingCosts, decimal RiskBudget, decimal? SpreadFraction, decimal? EntrySlippageFraction,
    IReadOnlyList<string> Reasons, string AssumptionsVersion, Guid? BookEvidenceId, Guid? FundingEvidenceId,
    Guid SetupId, EntryStrategy Strategy, decimal StructuralStop, SizingAssumptions Assumptions,
    ContractRules? RulesUsed, TradingLimits LimitsUsed, decimal EquityUsed, decimal AvailableCashUsed);
public sealed record FundingCashflow(string Status, decimal? Cashflow);

public interface IExecutionStore
{
    PaperAccount Account();
    void SaveExecution(ExecutionEvidence evidence);
    ExecutionEvidence? LatestExecution(Guid assetId);
    void SaveSizing(SizingProposal proposal);
    IReadOnlyList<SizingProposal> SizingHistory(Guid assetId);
}
