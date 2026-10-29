namespace PulletCryptoRadar.Core;

// Alternative venues never overwrite a Binance price, candle or derivative series.
public sealed record VenueQuote(string Exchange, string Symbol, string BaseAsset, string Kind,
    decimal? Price, decimal? Volume24hQuote, decimal? Return24h, decimal? FundingRate8h,
    DateTimeOffset SourceTime, DateTimeOffset ReceivedTime, string TimeBasis,
    FeatureValue<decimal> OpenInterestUsd, FeatureValue<decimal> OiChange15m,
    FeatureValue<double> OiRobustZ, FeatureValue<decimal> LongShare,
    FeatureValue<decimal> TopPositionChange15m);
public sealed record VenueBinding(int AssetId, string Exchange, string? Symbol, long Revision, DateTimeOffset UpdatedAt);
public sealed record VenueBindingRequest(string Exchange, string Symbol, long ExpectedRevision, bool ConfirmIdentity, bool Revoke = false);
public sealed record SpeculativeInput(MarketView Market, string Exchange, string NativeSymbol, bool VenueIdentityVerified,
    FeatureValue<decimal> Price, FeatureValue<decimal> Volume24hUsd, FeatureValue<decimal> Return1h,
    FeatureValue<decimal> Return24h, FeatureValue<decimal> FundingRate8h,
    FeatureValue<decimal> OpenInterestUsd, FeatureValue<decimal> OiChange15m,
    FeatureValue<double> OiRobustZ, FeatureValue<decimal> LongShare, FeatureValue<decimal> TopPositionChange15m,
    FeatureValue<double> PriceRobustZ, FeatureValue<double> VolumeRobustZ, string SourceNote);
public sealed record SpeculativeAssessment(string StrategyVersion, double? AnomalyScore, double? RiskScore,
    int AnomalyCoverage, int RiskCoverage, string Stage, bool IsCandidate, DataStatus Status,
    IReadOnlyList<ScoreComponent> AnomalyComponents, IReadOnlyList<ScoreComponent> RiskComponents,
    IReadOnlyList<string> MissingFamilies, string Reason);
public sealed record SpeculativeRow(SpeculativeInput Input, SpeculativeAssessment Assessment, bool IsSelectedSource)
{
    public MoveAssessment? BinanceUp { get; init; }
    public MoveAssessment? BinanceDown { get; init; }
}
public sealed record SpeculativeSnapshot(Guid Id, DateTimeOffset AsOf, string StrategyVersion, string ParametersJson,
    IReadOnlyList<SpeculativeRow> Rows);
public sealed record SpeculativeAudit(Guid Id, DateTimeOffset AsOf, string Status, int Rows, int Mismatches, string Reason);
public interface IAlternativeMarketProvider
{
    Task<IReadOnlyList<VenueQuote>> GetGateAsync(IReadOnlyList<string> priorityBases, CancellationToken ct);
    Task<IReadOnlyList<VenueQuote>> GetCoinbaseAsync(IReadOnlyList<string> bases, CancellationToken ct);
}
public interface IAlternativeMarketStore
{
    void Set(IReadOnlyList<VenueQuote> quotes);
    IReadOnlyList<VenueQuote> GetAll();
}
public interface ISpeculativeRepository
{
    IReadOnlyList<VenueBinding> Bindings();
    IReadOnlyList<SpeculativeSnapshot> History(int limit);
}
public interface ISpeculativePersistence
{
    Task SaveAsync(SpeculativeSnapshot snapshot, CancellationToken ct);
    Task BindAsync(VenueBinding binding, long expectedRevision, CancellationToken ct);
}
