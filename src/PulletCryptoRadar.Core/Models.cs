namespace PulletCryptoRadar.Core;

public enum DataStatus { Valid, Stale, Missing, Invalid }
public enum MappingStatus { Unknown, SymbolMatched, Ambiguous, ContractVerified, ManualVerified }

public sealed record FeatureValue<T>(T? Value, DataStatus Status, DateTimeOffset? SourceTime, string Source) where T : struct
{
    public static FeatureValue<T> Missing(string source = "FeatureEngine") => new(null, DataStatus.Missing, null, source);
}

// Asset identity belongs to the local database; an exchange instrument is a separate mapping.
public sealed record Asset(int Id, string Symbol, string Name, string? CoinGeckoId, MappingStatus MappingStatus);
public sealed record Instrument(int AssetId, string Symbol, string BaseAsset, DateTimeOffset OnboardTime, bool Active);
public sealed record SymbolDefinition(string Symbol, string BaseAsset, DateTimeOffset OnboardTime);
public sealed record Candle(string Symbol, int IntervalMinutes, DateTimeOffset OpenTime, decimal Open,
    decimal High, decimal Low, decimal Close, decimal QuoteVolume, DateTimeOffset SourceTime, DateTimeOffset ReceivedTime)
{
    public DateTimeOffset EndTime => OpenTime.AddMinutes(IntervalMinutes);
    public bool IsValid => IntervalMinutes is 1 or 5 or 15 or 60 && Open > 0 && Close > 0 && Low > 0
        && High >= Low && High >= Math.Max(Open, Close) && Low <= Math.Min(Open, Close) && QuoteVolume >= 0;
}

public abstract record MarketEvent(string Symbol, DateTimeOffset SourceTime, DateTimeOffset ReceivedTime);
public sealed record TickerEvent(string InstrumentSymbol, decimal Price, decimal QuoteVolume24h,
    decimal Return24h, DateTimeOffset Time, DateTimeOffset Received) : MarketEvent(InstrumentSymbol, Time, Received);
public sealed record MarkPriceEvent(string InstrumentSymbol, decimal MarkPrice, decimal FundingRate,
    DateTimeOffset NextFundingTime, DateTimeOffset Time, DateTimeOffset Received) : MarketEvent(InstrumentSymbol, Time, Received);
public sealed record CandleEvent(Candle Candle) : MarketEvent(Candle.Symbol, Candle.SourceTime, Candle.ReceivedTime);
public sealed record HistoryEvent(string InstrumentSymbol, IReadOnlyList<Candle> Candles, DateTimeOffset Time)
    : MarketEvent(InstrumentSymbol, Time, Time);

public sealed record AssetFeatures
{
    public OnChainFeatures OnChain { get;init; }=new();
    public CapitalFeatures Capital { get; init; } = new();
    public DerivativeFeatures Derivatives { get; init; } = new();
    public Dictionary<int, FeatureValue<decimal>> Returns { get; init; } = [];
    public FeatureValue<double> PriceRobustZ5m { get; init; } = FeatureValue<double>.Missing();
    public FeatureValue<double> PriceRobustZ15m { get; init; } = FeatureValue<double>.Missing();
    public FeatureValue<double> VolumeRobustZ { get; init; } = FeatureValue<double>.Missing();
    public FeatureValue<double> PricePercentile { get; init; } = FeatureValue<double>.Missing();
    public FeatureValue<double> VolumePercentile { get; init; } = FeatureValue<double>.Missing();
    public Dictionary<int, FeatureValue<decimal>> DistanceToHigh { get; init; } = [];
    public Dictionary<int, FeatureValue<decimal>> DistanceToLow { get; init; } = [];
    public int BaselineSamples { get; init; }
    public bool NewListing { get; init; }
    public string PercentileBasis { get; init; } = "Historical";
}

public sealed record MarketView(Instrument Instrument, decimal? Price, decimal? Volume24h, decimal? ExchangeReturn24h,
    decimal? MarkPrice, decimal? FundingRate, DateTimeOffset? PriceSourceTime, DateTimeOffset? PriceReceivedTime,
    DateTimeOffset? FundingSourceTime, int MinuteSamples, int FiveMinuteSamples, int FifteenMinuteSamples,
    AssetFeatures Features);

public sealed record ProviderHealth(string Name, DateTimeOffset? LastSuccess, DateTimeOffset? LastFailure,
    double? LatencyMs, long ErrorCount, string? Error, int FreshnessSeconds)
{
    public string Status(DateTimeOffset now) => LastSuccess is null ? (LastFailure is null ? "Unknown" : "Unavailable")
        : LastFailure > LastSuccess ? "Degraded" : now - LastSuccess > TimeSpan.FromSeconds(FreshnessSeconds) ? "Stale" : "Healthy";
}
