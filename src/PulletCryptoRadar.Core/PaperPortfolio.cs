namespace PulletCryptoRadar.Core;

public sealed record PaperPosition(Guid PositionId, Guid AssetId, Guid SetupId, TradeSide Side, EntryStrategy Strategy,
    string CorrelationGroup, decimal InitialQuantity, decimal RemainingQuantity, decimal EntryPrice,
    decimal InitialStop, decimal StopPrice, decimal InitialPlannedRisk, decimal InitialMargin, int Leverage,
    decimal RealizedPnl, decimal FeesPaid, decimal FundingPnl, decimal MfeR, decimal MaeR,
    bool FirstTargetDone, bool SecondTargetDone, decimal LastPrice, DateTimeOffset OpenedAt,
    DateTimeOffset LastObservedAt, long LastBookUpdateId, DateTimeOffset? ClosedAt,
    SizingAssumptions Costs, ContractRules Rules);
public sealed record PaperPortfolio(Guid AccountId, decimal InitialCash, decimal Balance, long Revision,
    DateTimeOffset UpdatedAt, IReadOnlyList<PaperPosition> Positions)
{
    public decimal UsedMargin => Positions.Sum(position => position.InitialMargin*position.RemainingQuantity/position.InitialQuantity);
    public decimal AvailableBalance => Balance-UsedMargin;
    public decimal OpenRisk => Positions.Sum(position => position.InitialPlannedRisk*position.RemainingQuantity/position.InitialQuantity);
    public decimal UnrealizedPnl => Positions.Sum(position => (position.Side == TradeSide.Long ? 1 : -1)*
        (position.LastPrice-position.EntryPrice)*position.RemainingQuantity);
    public decimal ConservativeEquity => Math.Min(Balance,Balance+UnrealizedPnl);
}
public sealed record PaperLedgerEvent(Guid EventId, Guid PositionId, string Type, DateTimeOffset At,
    decimal Quantity, decimal? Price, decimal RealizedPnl, decimal Fee, decimal Funding, decimal CashDelta,
    string Reason, Guid? EvidenceId, DateTimeOffset? SettlementAt = null, string? AssumptionVersion = null);
public sealed record PaperTransition(PaperPortfolio Portfolio, IReadOnlyList<PaperLedgerEvent> Events, string Status);
public sealed record PaperAudit(PaperPortfolio Portfolio,PaperAccount Account,IReadOnlyList<PaperLedgerEvent> Events)
{
    public bool CashReconciled => Portfolio.InitialCash+Events.Sum(item => item.CashDelta) == Portfolio.Balance &&
        Portfolio.Balance == Account.Cash;
}

public interface IPaperPortfolioStore
{
    PaperPortfolio Portfolio();
    PaperTransition CommitPaper(Guid commandId, Func<PaperPortfolio,PaperTransition> apply);
    IReadOnlyList<PaperLedgerEvent> PaperEvents(Guid? positionId = null);
}
