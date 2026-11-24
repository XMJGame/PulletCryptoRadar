namespace PulletCryptoRadar.Core;

public sealed record TradingLimits
{
    public int MaximumPositions { get; init; } = 6;
    public int MaximumLeverage { get; init; } = 10;
    public decimal MaximumMarginPerPosition { get; init; } = 100m;
    public decimal RiskPerTradeFraction { get; init; } = .0025m;
    public decimal MaximumOpenRiskFraction { get; init; } = .01m;
    public decimal MaximumDirectionalRiskFraction { get; init; } = .0075m;
    public decimal MaximumCorrelationRiskFraction { get; init; } = .006m;

    public void Validate()
    {
        if (MaximumPositions is < 1 or > 6 || MaximumLeverage is < 1 or > 10 ||
            MaximumMarginPerPosition is <= 0 or > 100 || RiskPerTradeFraction is <= 0 or > .01m ||
            MaximumOpenRiskFraction < RiskPerTradeFraction || MaximumOpenRiskFraction > .05m ||
            MaximumDirectionalRiskFraction is <= 0 or > .05m || MaximumCorrelationRiskFraction is <= 0 or > .05m)
            throw new ArgumentException("Invalid trading limits.");
    }
}

public enum CollectionFailure { None, Missing, Invalid, RateLimited, Forbidden, Timeout, Transport, Quota, MissingKey, Unsupported }
public sealed record RawEvidence(Guid Id, string Provider, string Symbol, string Endpoint,
    DateTimeOffset ReceivedTime, int? HttpStatus, CollectionFailure Failure, string? Body,DateTimeOffset? ResponseDate = null,
    string? FailureDetail = null,DateTimeOffset? RetryAt = null,RequestPriority Priority = RequestPriority.Core);
public sealed record DexPool(string Chain, string Pair, string Dex, string BaseAddress, string QuoteAddress,
    bool TargetIsBase, decimal? LiquidityUsd, decimal? BasePriceUsd, bool VerifiedCounterAsset);
public sealed record WalletCluster(decimal Percent, IReadOnlyList<string> Addresses, IReadOnlyList<string> Tags);
public sealed record ProviderRead(RawEvidence Evidence, IReadOnlyList<Observation> Observations,
    IReadOnlyList<DexPool>? Pools = null, IReadOnlyList<WalletCluster>? Clusters = null,
    IReadOnlyList<HourCandle>? Candles = null, IReadOnlyList<MarketInstrument>? Universe = null,
    IReadOnlyList<MarketTicker>? Tickers = null, IReadOnlyList<OpenInterestPoint>? OpenInterestHistory = null,
    OrderBook? OrderBook = null, IReadOnlyList<FundingSettlement>? FundingSettlements = null,
    IReadOnlyList<ContractRules>? ContractRules = null, MarketMark? MarketMark = null);

public interface ISupplementaryProvider
{
    Task<ProviderRead> ReadAsync(AssetIdentity asset, string capability, CancellationToken token);
}

public interface IMarketDataProvider
{
    Task<ProviderRead> ReadAsync(AssetIdentity asset, string capability, CancellationToken token);
}

public interface IRequestLedger
{
    bool Reserve(Guid requestId, string provider, DateTimeOffset now, int maximumBytes);
    void Complete(Guid requestId, int actualBytes, DateTimeOffset? cooldownUntil = null);
    RequestDecision TryReserve(Guid requestId,string provider,DateTimeOffset now,int maximumBytes,RequestPriority priority) =>
        new(Reserve(requestId,provider,now,maximumBytes),"LegacyLedger",null,priority);
    void CompleteWithReason(Guid requestId,int actualBytes,DateTimeOffset? cooldownUntil,string? reason) =>
        Complete(requestId,actualBytes,cooldownUntil);
}

public interface IFoundationEvidenceStore
{
    void Save(FeatureSnapshot snapshot, IReadOnlyList<ProviderRead> reads);
    FeatureSnapshot? Latest(Guid assetId);
    MarketContext? MarketContext() => null;
}
