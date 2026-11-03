namespace PulletCryptoRadar.Core;

public sealed record QuantParameters
{
    public string Version {get;init;}="Quant-Shadow-1";
    public int MinimumHours {get;init;}=337;
    public int FastEma {get;init;}=20;
    public int SlowEma {get;init;}=60;
    public int SlopeHours {get;init;}=6;
    public int AtrHours {get;init;}=14;
    public int BreakoutHours {get;init;}=20;
    public int PivotLookbackHours {get;init;}=72;
    public decimal MinimumVolume24h {get;init;}=10_000_000;
    public decimal PullbackVolumeRatio {get;init;}=1;
    public decimal BreakoutVolumeRatio {get;init;}=1.5m;
    public decimal PullbackTouchAtr {get;init;}=.35m;
    public decimal PullbackMaximumExtensionAtr {get;init;}=1.5m;
    public decimal BreakoutMaximumExtensionAtr {get;init;}=3;
    public decimal BreakoutBufferAtr {get;init;}=.1m;
    public decimal CloseStrength {get;init;}=.65m;
    public decimal StopBufferAtr {get;init;}=.5m;
    public decimal MaximumAtrFraction {get;init;}=.06m;
    public decimal MaximumOi1hAbsolutePercent {get;init;}=15;
    public decimal MaximumOi24hAbsolutePercent {get;init;}=80;
    public decimal MaximumAbsoluteFundingRate {get;init;}=.003m;
    public decimal MaximumPayFundingRate {get;init;}=.001m;
    public decimal RiskFraction {get;init;}=.004m;
    public decimal AbsoluteRiskCap {get;init;}=4;
    public decimal PilotRiskScale {get;init;}=.5m;
    public decimal TotalRiskFraction {get;init;}=.02m;
    public decimal AbsoluteTotalRiskCap {get;init;}=20;
    public decimal DirectionRiskFraction {get;init;}=.012m;
    public decimal CorrelationRiskFraction {get;init;}=.012m;
    public decimal AbsoluteDirectionRiskCap {get;init;}=12;
    public decimal AbsoluteCorrelationRiskCap {get;init;}=12;
    public decimal CorrelationThreshold {get;init;}=.75m;
    public int CorrelationHours {get;init;}=72;
    public int MaximumPositions {get;init;}=5;
    public int MaximumSameDirection {get;init;}=3;
    public decimal MaximumMargin {get;init;}=100;
    public int MaximumLeverage {get;init;}=5;
    public decimal AssumedMaintenanceFraction {get;init;}=.01m;
    public decimal GapRiskFraction {get;init;}=.001m;
    public decimal LiquidationBufferAtr {get;init;}=1;
    public int MaximumHoldHours {get;init;}=72;
    public int SignalLifetimeSeconds {get;init;}=120;
    public int DecisionWindowMinutes {get;init;}=10;
    public decimal BtcStressAtrFraction {get;init;}=.025m;
    public decimal EthStressAtrFraction {get;init;}=.035m;
}
public sealed record QuantCohort(Guid Id,DateTimeOffset StartedAt,string Version,string RulesHash,QuantParameters Parameters);
public sealed record QuantMarketInput(int AssetId,string Symbol,string BaseAsset,bool Active,DateTimeOffset OnboardAt,decimal? Price,
    decimal? QuoteVolume24h,decimal? Return24h,DateTimeOffset? PriceAt,DateTimeOffset? PriceReceivedAt,decimal? MarkPrice,decimal? FundingRate,DateTimeOffset? MarkAt)
{public DateTimeOffset? MarkReceivedAt {get;init;}}
public sealed record QuantOiEvidence(string Status,decimal? Change1hPercent,decimal? Change24hPercent,decimal RiskScale,
    DerivativeSample? Latest,DerivativeSample? HourAgo,DerivativeSample? DayAgo,string Meaning);
public sealed record QuantFacts(int Hours,DateTimeOffset Hour,decimal Close,decimal Ema20,decimal Ema60,decimal Ema20Ago6,
    int FourHourBars,DateTimeOffset FourHourEnd,decimal FourHourClose,decimal FourHourEma20,decimal FourHourEma60,
    decimal Atr,decimal AtrPercent,decimal VolumeRatio,decimal Return24hPercent,decimal Prior20High,decimal Prior20Low);
public sealed record QuantPivot(string Kind,decimal Price,DateTimeOffset At,DateTimeOffset ConfirmedAt);
public sealed record QuantInputBundle(string Symbol,DateTimeOffset Hour,IReadOnlyList<Candle> Candles,IReadOnlyList<DerivativeSample> Oi);
public sealed record QuantHypothesis(string Id,string RulesHash,string Symbol,string Strategy,string Direction,string Mode,
    DateTimeOffset Hour,DateTimeOffset CapturedAt,DateTimeOffset ExpiresAt,string Status,string Reason,string InputHash,
    QuantMarketInput Market,QuantFacts? Facts,QuantOiEvidence Oi,decimal? Stop,string? StopBasis,
    IReadOnlyList<QuantPivot> Levels,IReadOnlyList<SignalCondition> Conditions)
{public bool InputArchived {get;init;}}
public sealed record QuantMarketContext(DateTimeOffset AsOf,DateTimeOffset Hour,string State,string BtcTrend,string EthTrend,
    int FreshSymbols,int RegisteredSymbols,decimal? PositiveBreadthPercent,DateTimeOffset? OldestQuote,DateTimeOffset? NewestQuote,string Meaning);
public sealed record QuantReservation(string Symbol,string Direction,decimal PlannedRisk,decimal Margin,bool Pending);
public sealed record QuantPortfolio(long Revision,DateTimeOffset AsOf,decimal? Equity,decimal Cash,decimal PeakEquity,IReadOnlyList<QuantReservation> Reservations);
public sealed record QuantPlan(int Leverage,decimal Quantity,decimal Entry,decimal Stop,decimal Margin,decimal Fee,decimal Target1,decimal Target2,
    string Target1Basis,string Target2Basis,decimal EstimatedRisk,decimal FundingReserve,decimal RiskScale,decimal LiquidationEstimate,int MaximumHoldHours);
public sealed record QuantAssessment(string DecisionId,string Symbol,string Strategy,string Direction,string Mode,DateTimeOffset AsOf,
    string Status,string Reason,QuantPlan? Plan,ExecutionQuote? Quote,IReadOnlyList<SignalCondition> Checks)
{
    public QuantMarketInput? Market {get;init;}
    public QuantMarketContext? Context {get;init;}
    public QuantPortfolio? Portfolio {get;init;}
}
public sealed record QuantDecisionBatch(Guid CohortId,string RulesHash,string Mode,DateTimeOffset Hour,DateTimeOffset CapturedAt,
    string Symbol,string InputHash,string UniverseHash,QuantMarketContext Context,IReadOnlyList<QuantHypothesis> Decisions);
public sealed record QuantSnapshot(DateTimeOffset AsOf,DateTimeOffset Hour,string Mode,string RulesHash,IReadOnlyList<string> Universe,
    QuantMarketContext Context,QuantPortfolio Portfolio,IReadOnlyList<QuantHypothesis> Decisions,IReadOnlyList<QuantAssessment> Assessments);
public sealed record QuantHistoryHour(DateTimeOffset Hour,int Symbols,int Observations,int PriceCandidates,int Insufficient);
public sealed record QuantUniverse(Guid CohortId,DateTimeOffset Hour,DateTimeOffset CapturedAt,IReadOnlyList<string> Symbols);
public sealed record QuantShadowStatus(DateTimeOffset AsOf,QuantCohort? Cohort,QuantSnapshot? Latest,bool Stale,
    IReadOnlyList<QuantHistoryHour> ForwardHours,IReadOnlyList<string> CostTargets,IReadOnlyList<string> Notices,string? Error);
public sealed record ExecutionPlanningInputs(ContractTradingRules? Rules,ExecutionOrderBook? Book,int? AdjustedFundingIntervalHours);
public interface IQuantShadowTargets
{IReadOnlyList<string> CostSymbols {get;} IReadOnlyList<string> WarmupSymbols {get;}}
public interface IQuantShadowRepository
{
    QuantCohort OpenCohort(string hash,QuantParameters parameters,DateTimeOffset now);
    QuantUniverse FreezeUniverse(string hash,QuantUniverse universe);
    QuantUniverse? Universe(string hash,DateTimeOffset hour);
    string SaveInput(string hash,QuantInputBundle input);
    QuantInputBundle? ReadInput(string hash,string inputHash);
    bool SaveDecision(QuantDecisionBatch batch);
    void SaveAssessment(string hash,QuantAssessment assessment);
    IReadOnlyList<QuantDecisionBatch> Decisions(string hash,DateTimeOffset hour);
    IReadOnlyList<QuantHistoryHour> History(string hash,int hours);
    void SaveLatest(QuantSnapshot snapshot);
    QuantSnapshot? Latest(string hash);
    IReadOnlyList<QuantAssessment> ReadAssessments(string hash,DateTimeOffset from,DateTimeOffset through)=>[];
}
