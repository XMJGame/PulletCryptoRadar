namespace PulletCryptoRadar.Core;

public sealed record ContractTradingRules(string Symbol,string Status,decimal TickSize,decimal MinPrice,decimal MaxPrice,
    decimal StepSize,decimal MinQuantity,decimal MaxQuantity,decimal MarketStepSize,decimal MarketMinQuantity,decimal MarketMaxQuantity,
    decimal MinimumNotional,decimal? MarketTakeBound,DateTimeOffset ObservedAt,string? Issue);
public sealed record ContractRulesSnapshot(DateTimeOffset ObservedAt,IReadOnlyList<ContractTradingRules> Rules);
public sealed record BookLevel(decimal Price,decimal Quantity);
public sealed record ExecutionOrderBook(string Symbol,long UpdateId,DateTimeOffset TransactionAt,DateTimeOffset OutputAt,DateTimeOffset ReceivedAt,
    IReadOnlyList<BookLevel> Bids,IReadOnlyList<BookLevel> Asks);
public sealed record FundingRateRecord(string Symbol,DateTimeOffset FundingAt,decimal Rate,decimal MarkPrice,string RateType,DateTimeOffset ReceivedAt);
public sealed record FundingRateWindow(string Symbol,DateTimeOffset From,DateTimeOffset Through,DateTimeOffset ReceivedAt,bool Complete,
    IReadOnlyList<FundingRateRecord> Records);
public sealed record FundingIntervalRule(string Symbol,int IntervalHours,decimal Cap,decimal Floor);
public sealed record FundingIntervalSnapshot(DateTimeOffset ObservedAt,IReadOnlyList<FundingIntervalRule> Adjustments);

public sealed record ExecutionCostModel
{
    public string Version {get;init;}="Execution-Cost-1";
    public decimal FeeRate {get;init;}=.0005m;
    public decimal ResidualSlippageRate {get;init;}=.0005m;
    public decimal MaximumSpreadBps {get;init;}=30;
    public decimal MaximumImpactBps {get;init;}=50;
    public int BookMaximumAgeSeconds {get;init;}=20;
    public int RulesMaximumAgeHours {get;init;}=12;
}
public sealed record ExecutionQuoteRequest(string Symbol,string Side,decimal Quantity,decimal? StopPrice=null,bool ReduceOnly=false);
public sealed record ExecutionQuote(string Version,string Symbol,string Side,string Status,string Reason,DateTimeOffset AsOf,
    decimal RequestedQuantity,decimal? Quantity,decimal? Remainder,decimal? Vwap,decimal? EstimatedFill,decimal? Notional,decimal? Fee,
    decimal? SpreadBps,decimal? ImpactBps,decimal? RoundedStop,decimal? PlannedLossIncludingCosts,DateTimeOffset? BookAt,DateTimeOffset? RulesAt,
    ExecutionOrderBook? Book,ContractTradingRules? Rules);
public sealed record ExposureReduction(DateTimeOffset At,decimal Quantity);
public sealed record FundingExposure(Guid PositionId,string Symbol,string Direction,DateTimeOffset OpenedAt,decimal InitialQuantity,
    DateTimeOffset? ClosedAt,IReadOnlyList<ExposureReduction> Reductions);
public sealed record FundingAuditPayment(string Version,Guid PositionId,string Symbol,string Direction,DateTimeOffset FundingAt,string RateType,
    decimal Quantity,decimal Rate,decimal MarkPrice,decimal SignedCost,DateTimeOffset ObservedAt);
public sealed record FundingAuditResult(Guid PositionId,string Symbol,string Direction,string Status,string Reason,DateTimeOffset RequiredFrom,
    DateTimeOffset RequiredThrough,DateTimeOffset? KnownThrough,decimal? CompleteSignedCost,decimal ObservedSignedCost,
    decimal LegacyRecordedCost,IReadOnlyList<FundingAuditPayment> Payments);
public sealed record ExecutionSymbolStatus(string Symbol,ContractTradingRules? Rules,ExecutionQuote? Buy100Usdt,ExecutionQuote? Sell100Usdt,
    DateTimeOffset? FundingFrom,DateTimeOffset? FundingThrough,int? FundingRecords,int? AdjustedFundingIntervalHours,
    decimal? LastObservedIntervalHours,DateTimeOffset? NextFundingAt,DateTimeOffset? LastAttempt,string? Error);
public sealed record ExecutionFoundationStatus(DateTimeOffset AsOf,string Mode,ExecutionCostModel Model,DateTimeOffset? RulesAt,int RulesCount,
    IReadOnlyList<ExecutionSymbolStatus> Symbols,IReadOnlyList<FundingAuditResult> FundingAudits,int? StoredFundingPayments,
    IReadOnlyList<ExecutionQuote> HeldExitQuotes,IReadOnlyList<string> Notices);

public interface IExecutionEvidenceProvider
{
    Task<ContractRulesSnapshot> GetTradingRulesAsync(CancellationToken ct);
    Task<ExecutionOrderBook> GetOrderBookAsync(string symbol,CancellationToken ct);
    Task<FundingRateWindow> GetFundingHistoryAsync(string symbol,DateTimeOffset from,DateTimeOffset through,CancellationToken ct);
    Task<FundingIntervalSnapshot> GetFundingIntervalsAsync(CancellationToken ct);
}
public interface IExecutionEvidenceRepository
{
    ContractRulesSnapshot? Rules();void SaveRules(ContractRulesSnapshot snapshot);
    FundingIntervalSnapshot? Intervals();void SaveIntervals(FundingIntervalSnapshot snapshot);
    FundingRateWindow? Funding(string symbol);void SaveFunding(FundingRateWindow window);
    int AddFundingPayments(IReadOnlyList<FundingAuditPayment> payments);
    int FundingPaymentCount();
    void SaveQuote(ExecutionQuote quote);
}
