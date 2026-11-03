namespace PulletCryptoRadar.Core;

public sealed record ForwardRules
{
    public string Version { get; init; }="Forward-Observation-1";
    public int Days { get; init; }=14;
    public int SnapshotMinutes { get; init; }=5;
    public int PriceAgeSeconds { get; init; }=90;
    public int MaximumCoverageGapSeconds { get; init; }=600;
    public int MinimumHours { get; init; }=337;
    public int MaximumDecisionDelayMinutes { get; init; }=10;
    public decimal CompressionMax { get; init; }=.12m;
    public decimal BreakoutBuffer { get; init; }=.002m;
    public decimal VolumeRatioA { get; init; }=1.2m;
    public decimal VolumeRatioB { get; init; }=1.5m;
    public decimal Leverage { get; init; }=10;
    public decimal MarginCap { get; init; }=100;
    public decimal ReferenceRisk { get; init; }=10;
    public decimal FeePerSide { get; init; }=.0005m;
    public decimal SlippagePerSide { get; init; }=.0005m;
    public decimal MaintenanceRate { get; init; }=.005m;
    public string Definition { get; init; }="独立前瞻观察；每个候选不占账户资金，不执行交易。A/B沿用结构假设，但本机K线只含报价成交额，量比明确采用quoteVolume，区别于离线R1的baseVolume。24h/72h输出采样价格标签，不是策略净收益。没有真实资金费结算/盘口/逐笔路径时不计算净利润，不以14天届满自动验收。";
}
public sealed record ForwardCohort(Guid Id,DateTimeOffset StartedAt,DateTimeOffset EndsAt,ForwardRules Rules,string RulesHash);
public sealed record ForwardRank(int AssetId,string Symbol,int? Rank,decimal? Price,decimal? Return24h,decimal? Volume24h,
    DateTimeOffset? SourceTime,DateTimeOffset? ReceivedTime,bool Fresh,FeatureValue<decimal> OpenInterest,
    decimal? FundingRate,DateTimeOffset? FundingTime,bool TrustedIdentity,bool HolderSeriesKnown);
public sealed record ForwardSnapshot(DateTimeOffset CapturedAt,DateTimeOffset Bucket,IReadOnlyList<ForwardRank> Rows);
public sealed record ForwardSnapshotSummary(DateTimeOffset CapturedAt,int Registered,int Fresh);
public sealed record ForwardPlan(decimal ReferenceEntry,decimal Stop,decimal Quantity,decimal Margin,decimal EstimatedRisk,
    decimal Target1,decimal Target2,string Target1Basis,string Target2Basis,decimal Atr);
public sealed record ForwardDecision(string Id,string Symbol,string Strategy,DateTimeOffset DecisionAt,DateTimeOffset CapturedAt,
    string Status,string Reason,int Hours,int? Rank,decimal? QuoteVolumeRatio,decimal? Return24h,
    decimal? Oi24hPercent,DateTimeOffset? OiSourceTime,DateTimeOffset? OiReceivedTime,
    string OiConfirmation,ForwardPlan? Plan,string RulesHash)
{
    public string Direction {get;init;}="Long";
    public IReadOnlyList<SignalCondition> Conditions {get;init;}=[];
}
public sealed record SignalCondition(string Key,string Name,decimal? Actual,string Requirement,bool Passed);
public sealed record ForwardOutcome(string Id,string DecisionId,int HorizonHours,DateTimeOffset DueAt,DateTimeOffset EvaluatedAt,
    string Status,int Samples,int ExpectedSamples,bool CoverageComplete,decimal? EndPrice,decimal? EndReturnPercent,
    decimal? SampledMaxReturnPercent,decimal? SampledMinReturnPercent,bool? SampledStopCrossed,
    bool? SampledTarget1Crossed,bool? SampledTarget2Crossed,string Meaning);
public interface IForwardRepository
{
    ForwardCohort? Cohort();
    void Initialize(ForwardCohort cohort);
    bool HasSnapshot(DateTimeOffset bucket);
    void Save(ForwardSnapshot snapshot,IReadOnlyList<ForwardDecision> decisions);
    IReadOnlyList<ForwardDecision> Decisions();
    IReadOnlyList<ForwardSnapshot> Snapshots(DateTimeOffset from,DateTimeOffset to);
    IReadOnlyList<ForwardSnapshotSummary> SnapshotSummaries();
    ForwardSnapshot? LatestSnapshot();
    IReadOnlyList<ForwardOutcome> Outcomes();
    void SaveOutcomes(IReadOnlyList<ForwardOutcome> outcomes);
}
