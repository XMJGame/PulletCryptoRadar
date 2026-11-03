namespace PulletCryptoRadar.Core;
public sealed record QuantStudyParameters
{
    public string Version {get;init;}="Quant-Validation-1";
    public int Days {get;init;}=60;
    public int HoldHours {get;init;}=72;
    public decimal Notional {get;init;}=100;
    public decimal FeePerSide {get;init;}=.0005m;
    public decimal SlippagePerSide {get;init;}=.0005m;
    public decimal AdverseFundingPerHour {get;init;}=.0001m;
    public decimal FirstFraction {get;init;}=.3m;
    public decimal SecondFraction {get;init;}=.3m;
    public decimal TrailAtr {get;init;}=3;
    public int BootstrapSamples {get;init;}=1000;
    public int BootstrapSeed {get;init;}=17;
}
public sealed record QuantStudyWindow(string Name,DateTimeOffset From,DateTimeOffset Through,DateTimeOffset LastEntryExclusive);
public sealed record QuantStudyRequest(Guid Id,DateTimeOffset RequestedAt,DateTimeOffset From,DateTimeOffset Through,string RulesHash,string StudyHash,
    QuantParameters Strategy,QuantStudyParameters Parameters,IReadOnlyList<Instrument> Universe,IReadOnlyList<QuantStudyWindow> Windows);
public sealed record QuantStudySeries(Instrument Instrument,DateTimeOffset CollectedAt,string Source,IReadOnlyList<Candle> Candles,string? Error);
public sealed record QuantStudySource(string Symbol,string Hash,int Candles,DateTimeOffset? First,DateTimeOffset? Through,string Source,string? Error);
public sealed record QuantStudyProgress(QuantStudyRequest Request,string Status,DateTimeOffset UpdatedAt,int Collected,int Processed,string Message,IReadOnlyList<QuantStudySource> Sources)
{public QuantForwardReproduction? ForwardAudit {get;init;}}
public sealed record QuantExitSeed(string Symbol,string Direction,DateTimeOffset EntryAt,decimal Entry,decimal Quantity,decimal Stop,decimal Target1,decimal Target2,decimal Atr);
public sealed record QuantExitLeg(DateTimeOffset At,DateTimeOffset BarOpen,decimal Quantity,decimal Price,decimal Gross,decimal Fee,string Reason);
public sealed record QuantExitResult(string Status,DateTimeOffset? ClosedAt,decimal? Net,decimal Fees,decimal Funding,decimal InitialRisk,
    int AmbiguousBars,decimal RemainingQuantity,IReadOnlyList<QuantExitLeg> Legs,string Reason);
public sealed record QuantStudyTrade(string Id,string Window,string Symbol,string Strategy,string Direction,string Policy,int CostMultiplier,
    DateTimeOffset SignalHour,DateTimeOffset ReconstructedAt,string SourceHash,QuantExitSeed Seed,QuantExitResult Conservative,QuantExitResult TargetFirst);
public sealed record QuantStudySummary(string Window,string Strategy,string Policy,int CostMultiplier,int Trades,int Completed,int MissingPath,int Ambiguous,
    decimal? MeanNetR,decimal? TargetFirstMeanNetR,decimal? ProfitFactor,decimal? WinRate,int LongTrades,int ShortTrades,int SignalDays,decimal? MeanLower95,decimal? MeanUpper95);
public sealed record QuantForwardReproduction(int Batches,int Decisions,int Matches,int Mismatches,int MissingInputs,IReadOnlyList<string> Issues);
public sealed record QuantStudyGate(string Name,string Status,string Detail);
public sealed record QuantStudyReport(QuantStudyRequest Request,DateTimeOffset CompletedAt,IReadOnlyList<QuantStudySource> Sources,
    IReadOnlyList<QuantStudySummary> Summaries,IReadOnlyDictionary<string,long> Counts,QuantForwardReproduction Forward,IReadOnlyList<QuantStudyGate> Gates,IReadOnlyList<string> Limitations);
public sealed record QuantStudyStatus(QuantStudyProgress? Progress,QuantStudyReport? Report,bool Running,string? Error);
public interface IQuantStudyRepository
{
    QuantStudyProgress? LatestProgress();void SaveProgress(QuantStudyProgress progress);
    QuantStudyReport? LatestReport();void SaveReport(QuantStudyReport report,IReadOnlyList<QuantStudyTrade> trades);
    QuantStudyReport? Report(Guid id);
    IReadOnlyList<QuantStudyTrade> ReadTrades(Guid id);
    QuantStudySeries? Series(string hash);string SaveSeries(QuantStudySeries series);
    IReadOnlyList<QuantStudyTrade> Trades(Guid id,string? window,string? strategy,string? policy,int cost,int limit);
}
