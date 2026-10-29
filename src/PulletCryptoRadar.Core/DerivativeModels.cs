namespace PulletCryptoRadar.Core;

public enum DerivativeKind { CurrentOpenInterest, OpenInterest5m, GlobalLongShort5m, TopAccount5m, TopPosition5m, Taker5m }
public sealed record DerivativeSample(string Symbol, DerivativeKind Kind, DateTimeOffset SourceTime, DateTimeOffset ReceivedTime,
    decimal? Quantity = null, decimal? NotionalUsd = null, decimal? Ratio = null, decimal? LongShare = null,
    decimal? ShortShare = null, decimal? BuyQuantity = null, decimal? SellQuantity = null);
public sealed record DerivativeEvent(string InstrumentSymbol, IReadOnlyList<DerivativeSample> Samples, DateTimeOffset Time)
    : MarketEvent(InstrumentSymbol, Time, Time);
public sealed record LiquidationObservation(string Symbol, string Side, decimal Quantity, decimal AveragePrice,
    DateTimeOffset SourceTime, DateTimeOffset ReceivedTime)
{
    public string Key => $"{Symbol}:{SourceTime.ToUnixTimeMilliseconds()}:{Side}";
    public decimal NotionalUsd => Quantity * AveragePrice;
}
public sealed record LiquidationEvent(LiquidationObservation Observation)
    : MarketEvent(Observation.Symbol, Observation.SourceTime, Observation.ReceivedTime);
public sealed record LiquidationCoverageEvent(bool Connected, DateTimeOffset SessionStart, DateTimeOffset Time)
    : MarketEvent("", Time, Time);
public sealed record DerivativeRuntimeSnapshot(string Symbol, IReadOnlyDictionary<DerivativeKind, IReadOnlyList<DerivativeSample>> Series,
    IReadOnlyList<LiquidationObservation> Liquidations, DateTimeOffset? CoverageStart, DateTimeOffset? LastStreamActivity);

public sealed record DerivativeFeatures
{
    public FeatureValue<decimal> OpenInterestQuantity { get; init; } = FeatureValue<decimal>.Missing();
    public FeatureValue<decimal> OpenInterestUsd { get; init; } = FeatureValue<decimal>.Missing();
    public Dictionary<int, FeatureValue<decimal>> OiChanges { get; init; } = [];
    public FeatureValue<double> OiRobustZ { get; init; } = FeatureValue<double>.Missing();
    public FeatureValue<double> OiPercentile { get; init; } = FeatureValue<double>.Missing();
    public FeatureValue<decimal> GlobalLongShare { get; init; } = FeatureValue<decimal>.Missing();
    public FeatureValue<decimal> TopAccountLongShare { get; init; } = FeatureValue<decimal>.Missing();
    public FeatureValue<decimal> TopPositionLongShare { get; init; } = FeatureValue<decimal>.Missing();
    public FeatureValue<decimal> TopPositionChange15m { get; init; } = FeatureValue<decimal>.Missing();
    public FeatureValue<decimal> TakerBuyShare { get; init; } = FeatureValue<decimal>.Missing();
    public FeatureValue<decimal> TakerBuySellRatio { get; init; } = FeatureValue<decimal>.Missing();
    public FeatureValue<double> TakerRobustZ { get; init; } = FeatureValue<double>.Missing();
    public FeatureValue<decimal> ObservedLongLiquidation15mUsd { get; init; } = FeatureValue<decimal>.Missing();
    public FeatureValue<decimal> ObservedShortLiquidation15mUsd { get; init; } = FeatureValue<decimal>.Missing();
    public string PriceOiStructure { get; init; } = "Unknown";
    public int OiBaselineSamples { get; init; }
    public bool LiquidationCoverageComplete { get; init; }
    public FeatureValue<double> LiquidationRobustZ { get; init; } = FeatureValue<double>.Missing();
    public string LiquidationCaveat { get; init; } = "Binance 1s 抽样快照；仅为本机观测值，不是完整强平总额";
}

public interface IDerivativeRestProvider
{
    Task<DerivativeSample> GetOpenInterestAsync(string symbol, CancellationToken ct);
    Task<IReadOnlyList<DerivativeSample>> GetSeriesAsync(string symbol, DerivativeKind kind, int limit, CancellationToken ct);
}
public interface IDerivativeRuntimeStore
{
    void Apply(MarketEvent marketEvent);
    DerivativeRuntimeSnapshot Get(string symbol);
}
