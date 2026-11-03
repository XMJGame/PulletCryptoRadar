using System.Text.Json.Serialization;
namespace PulletCryptoRadar.Core;

public sealed record PaperRules
{
    public string Version {get;init;}="Paper-1.0";
    public bool ResearchValidated {get;init;}=false;
    public bool ExperimentalSimulation {get;init;}=false;
    public decimal RiskPerTrade {get;init;}=6;
    public decimal TotalPlannedRiskLimit {get;init;}=30;
    public decimal ReviewLossFraction {get;init;}=.20m;
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
public sealed record PaperIntent(string Key,Guid ScanId,int AssetId,string Symbol,DateTimeOffset CreatedAt,RadarObservation? Evidence)
{public ForwardDecision? StructuralEvidence {get;init;} [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public QuantLiveIntent? QuantIntent {get;init;}}
public sealed record PaperPosition(Guid Id,string SignalKey,int AssetId,string Symbol,DateTimeOffset OpenedAt,decimal EntryPrice,decimal Quantity,
    decimal InitialMargin,decimal RemainingMargin,decimal OpenFee,decimal FundingCost,DateTimeOffset LastFundingAt,
    decimal LastMark,DateTimeOffset LastQuoteTime,decimal StopPrice,decimal TakePrice,PaperIntent Intent)
{
    public int Bucket {get;init;}
    public decimal InitialQuantity {get;init;}
    public decimal InitialStop {get;init;}
    public decimal InitialAtr {get;init;}
    public decimal Target2 {get;init;}
    public bool FirstTaken {get;init;}
    public bool SecondTaken {get;init;}
    public decimal PeakPrice {get;init;}
    public decimal PlannedRisk {get;init;}
    public bool QuoteGapObserved {get;init;}
    public string Direction {get;init;}="Long";
    public DateTimeOffset LastMarkSourceTime {get;init;}
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public QuantLivePosition? Quant {get;init;}
}
public sealed record PaperTrade(Guid Id,string SignalKey,int AssetId,string Symbol,DateTimeOffset OpenedAt,DateTimeOffset ClosedAt,
    decimal EntryPrice,decimal ExitPrice,decimal Quantity,decimal Margin,int Leverage,decimal GrossPnl,decimal Fees,decimal FundingCost,decimal NetPnl,
    string Reason,bool QuoteGap,string Version,PaperIntent Evidence)
{public int Bucket {get;init;} public decimal SettlementAdjustment {get;init;} public Guid? PositionId {get;init;} public bool FinalExit {get;init;}=true;public string Direction {get;init;}="Long"; [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public QuantLivePosition? Quant {get;init;} [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public ExecutionQuote? ExitQuote {get;init;}}
public sealed record PaperDecision(DateTimeOffset Time,string Symbol,string Action,string Reason,string? SignalKey=null);
public sealed record PaperEquity(DateTimeOffset Time,decimal Equity,bool Complete);
public sealed record PaperState
{
    public long Revision {get;init;}
    public DateTimeOffset StartedAt {get;init;}
    public DateTimeOffset UpdatedAt {get;init;}
    public PaperRules Rules {get;init;}=new();
    public bool Enabled {get;init;}=false;
    public DateTimeOffset? ExecutionStartedAt {get;init;}
    public decimal PeakEquity {get;init;}=1000;
    public string? EntryBlockReason {get;init;}
    public bool ReviewThresholdReached {get;init;}
    public DateTimeOffset? SignalHour {get;init;}
    public DateTimeOffset? LastAnalysisAttempt {get;init;}
    public DateTimeOffset? LastHoldingSampleAt {get;init;}
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public DateTimeOffset? LastEquitySampleAt {get;init;}
    public IReadOnlyList<ForwardDecision> SignalObservations {get;init;}=[];
    public DateTimeOffset? AnalysisHour {get;init;}
    public string UniverseVersion {get;init;}="Legacy-20";
    public IReadOnlyList<string> AnalysisSymbols {get;init;}=[];
    public decimal Cash {get;init;}=1000;
    public Guid? LastScanId {get;init;}
    public IReadOnlyList<PaperPosition> Positions {get;init;}=[];
    public IReadOnlyList<PaperIntent> Pending {get;init;}=[];
    public IReadOnlyList<string> UsedSignals {get;init;}=[];
    public IReadOnlyList<PaperTrade> RecentTrades {get;init;}=[];
    public IReadOnlyList<PaperDecision> Decisions {get;init;}=[];
    public IReadOnlyList<PaperEquity> Curve {get;init;}=[];
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public QuantLiveControl? QuantControl {get;init;}
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public IReadOnlyList<string>? QuantUsedSignals {get;init;}
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public IReadOnlyList<QuantFundingCoverage>? QuantFunding {get;init;}
}
public sealed record PaperHoldingSnapshot(Guid PositionId,DateTimeOffset CapturedAt,long AccountRevision,string Version,
    string Event,PaperPosition Position,MarketView? Market,bool DataFresh,decimal? MarkPnl,decimal? AccountEquity,
    IReadOnlyList<PaperTrade> Executions);
public sealed record PaperBatch(PaperState State,long ExpectedRevision,IReadOnlyList<PaperTrade> Trades)
{public IReadOnlyList<PaperHoldingSnapshot> HoldingSnapshots {get;init;}=[]; public IReadOnlyList<QuantFundingEvent> FundingEvents {get;init;}=[]; public IReadOnlyList<QuantEntryReview> EntryReviews {get;init;}=[]; public IReadOnlyList<PaperEquitySample> EquitySamples {get;init;}=[];}
public sealed record PaperOverview(PaperState State,decimal Equity,decimal? CompleteEquity,decimal RealizedPnl,decimal? UnrealizedPnl,
    decimal MarginUsed,decimal Fees,int ClosedTrades,int Wins,decimal? WinRate,string Mode,IReadOnlyList<PaperDaily> Days)
{public int SettlementLegs {get;init;} public decimal OpenPlannedRisk {get;init;} public DateTimeOffset? NextAnalysisAt {get;init;}}
public sealed record PaperDaily(string Date,int Trades,int Wins,decimal NetPnl,decimal Fees,decimal FundingCost,string Review);
public interface IPaperRepository
{
    PaperState? Restore();IReadOnlyList<PaperTrade> Trades();
    IReadOnlyList<PaperHoldingSnapshot> HoldingHistory(Guid? positionId,int limit,long? before=null);
    long HoldingSnapshotCount();
    PaperAuditWindow AnalysisAudit(DateTimeOffset since,DateTimeOffset through)=>new([],false);
    IReadOnlyList<QuantFundingEvent> QuantFundingEvents()=>[];
    QuantEntryWindow EntryReviews(DateTimeOffset since,DateTimeOffset through,int limit=200)=>new([],false);
    QuantEntryReview? EntryReview(string id)=>null;
    DateTimeOffset? FirstEntryReviewAt()=>null;
    IReadOnlyList<string> EntryReviewIds(DateTimeOffset since,DateTimeOffset through)=>throw new NotSupportedException("当前仓库未提供完整复核索引，不能将截断摘要冒充全部资料");
    IReadOnlyList<PaperEquityPoint> EquityPoints(DateTimeOffset since,DateTimeOffset through)=>[];
    IReadOnlyList<PaperEquitySample> EquitySamples(DateTimeOffset since,DateTimeOffset through)=>[];
    DateTimeOffset? FirstEquitySampleAt()=>null;
}
public interface IPaperPersistence {Task SaveAsync(PaperBatch batch,CancellationToken ct);}
