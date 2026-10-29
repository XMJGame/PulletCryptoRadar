namespace PulletCryptoRadar.Core;

public sealed record PaperRules
{
    public string Version {get;init;}="Paper-1.0";
    public bool ResearchValidated {get;init;}=false;
    public decimal InitialCash {get;init;}=1000;
    public decimal MarginPerTrade {get;init;}=100;
    public int Leverage {get;init;}=10;
    public int MaximumPositions {get;init;}=2;
    public decimal StopPercent {get;init;}=1.5m;
    public decimal TakePercent {get;init;}=3m;
    public int MaximumHoldHours {get;init;}=4;
    public decimal DailyLossLimit {get;init;}=30;
    public decimal FeeRate {get;init;}=.0005m;
    public decimal SlippageRate {get;init;}=.0005m;
    public decimal MaintenanceRate {get;init;}=.005m;
    public decimal FundingCost8h {get;init;}=.0001m;
}
public sealed record PaperIntent(string Key,Guid ScanId,int AssetId,string Symbol,DateTimeOffset CreatedAt,RadarObservation Evidence);
public sealed record PaperPosition(Guid Id,string SignalKey,int AssetId,string Symbol,DateTimeOffset OpenedAt,decimal EntryPrice,decimal Quantity,
    decimal InitialMargin,decimal RemainingMargin,decimal OpenFee,decimal FundingCost,DateTimeOffset LastFundingAt,
    decimal LastMark,DateTimeOffset LastQuoteTime,decimal StopPrice,decimal TakePrice,PaperIntent Intent)
{public int Bucket {get;init;}}
public sealed record PaperTrade(Guid Id,string SignalKey,int AssetId,string Symbol,DateTimeOffset OpenedAt,DateTimeOffset ClosedAt,
    decimal EntryPrice,decimal ExitPrice,decimal Quantity,decimal Margin,int Leverage,decimal GrossPnl,decimal Fees,decimal FundingCost,decimal NetPnl,
    string Reason,bool QuoteGap,string Version,PaperIntent Evidence)
{public int Bucket {get;init;} public decimal SettlementAdjustment {get;init;}}
public sealed record PaperDecision(DateTimeOffset Time,string Symbol,string Action,string Reason,string? SignalKey=null);
public sealed record PaperEquity(DateTimeOffset Time,decimal Equity,bool Complete);
public sealed record PaperState
{
    public long Revision {get;init;}
    public DateTimeOffset StartedAt {get;init;}
    public DateTimeOffset UpdatedAt {get;init;}
    public PaperRules Rules {get;init;}=new();
    public bool Enabled {get;init;}=false;
    public decimal Cash {get;init;}=1000;
    public Guid? LastScanId {get;init;}
    public IReadOnlyList<PaperPosition> Positions {get;init;}=[];
    public IReadOnlyList<PaperIntent> Pending {get;init;}=[];
    public IReadOnlyList<string> UsedSignals {get;init;}=[];
    public IReadOnlyList<PaperTrade> RecentTrades {get;init;}=[];
    public IReadOnlyList<PaperDecision> Decisions {get;init;}=[];
    public IReadOnlyList<PaperEquity> Curve {get;init;}=[];
}
public sealed record PaperBatch(PaperState State,long ExpectedRevision,IReadOnlyList<PaperTrade> Trades);
public sealed record PaperOverview(PaperState State,decimal Equity,decimal? CompleteEquity,decimal RealizedPnl,decimal? UnrealizedPnl,
    decimal MarginUsed,decimal Fees,int ClosedTrades,int Wins,decimal? WinRate,string Mode,IReadOnlyList<PaperDaily> Days);
public sealed record PaperDaily(string Date,int Trades,int Wins,decimal NetPnl,decimal Fees,decimal FundingCost,string Review);
public interface IPaperRepository {PaperState? Restore();IReadOnlyList<PaperTrade> Trades();}
public interface IPaperPersistence {Task SaveAsync(PaperBatch batch,CancellationToken ct);}
