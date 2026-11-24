namespace PulletCryptoRadar.Core;

public sealed record HistoricalSample(string Symbol,string Role,DateTimeOffset StudyStart,DateTimeOffset StudyEnd,
    string SelectionReason,string IdentityScope="Binance USD-M perpetual instrument; on-chain identity is separate");
public sealed record HistoricalArchive(string Symbol,string Month,string Url,string Sha256,int Bytes,DateTimeOffset DownloadedAt,
    IReadOnlyList<HourCandle> Candles);
public sealed record ForwardOutcome(int Days,bool Complete,decimal? CloseReturnPercent,decimal? MaximumRisePercent,
    decimal? MaximumFallPercent,decimal? MaximumDrawdownPercent);
public sealed record ResearchTrigger(DateTimeOffset At,decimal ReferenceClose,decimal VolumeRatio,decimal Return24h,
    decimal TakerBuyShare,IReadOnlyList<ForwardOutcome> Outcomes);
public sealed record HistoricalSampleResult(HistoricalSample Sample,string Status,int Bars,int MissingHourlyIntervals,
    IReadOnlyList<ResearchTrigger> Triggers,IReadOnlyList<string> MissingArchives,IReadOnlyList<string> Limitations);
public sealed record HistoricalResearchReport(string Version,DateTimeOffset GeneratedAt,IReadOnlyList<HistoricalSampleResult> Samples,
    IReadOnlyList<string> Limitations);
