namespace PulletCryptoRadar.Core;

public sealed record MarketCoin(string Id, string Symbol, string Name, IReadOnlyDictionary<string, string> Platforms);
public sealed record CoinCatalog(DateTimeOffset ReceivedTime, IReadOnlyList<MarketCoin> Coins);
public sealed record MarketCapitalSnapshot(string CoinGeckoId, DateTimeOffset SourceTime, DateTimeOffset ReceivedTime,
    decimal? MarketCapUsd, decimal? FdvUsd, decimal? Volume24hUsd, decimal? CirculatingSupply, decimal? TotalSupply,
    decimal? MaxSupply, decimal? AthUsd);
public sealed record AssetMapping(int AssetId, string? CoinGeckoId, MappingStatus Status, IReadOnlyList<string> CandidateIds,
    decimal ContractMultiplier, string? Platform, string? ContractAddress, long Revision, DateTimeOffset UpdatedAt, string Reason);
public sealed record MappingChange(AssetMapping Mapping, long ExpectedRevision);
public sealed record ManualMappingRequest(string? CoinGeckoId, long ExpectedRevision, bool ConfirmIdentity,
    string? Platform = null, string? ContractAddress = null, string? Reason = null);
public sealed record CapitalInput(AssetMapping? Mapping, MarketCapitalSnapshot? Snapshot, bool CatalogContainsId);
public sealed record CapitalFeatures
{
    public string? CoinGeckoId { get; init; }
    public MappingStatus MappingStatus { get; init; }
    public long MappingRevision { get; init; }
    public bool TrustedIdentity { get; init; }
    public string MappingReason { get; init; } = "资产映射待确认";
    public FeatureValue<decimal> MarketCapUsd { get; init; } = FeatureValue<decimal>.Missing("CoinGecko");
    public FeatureValue<decimal> FdvUsd { get; init; } = FeatureValue<decimal>.Missing("CoinGecko");
    public FeatureValue<decimal> Volume24hUsd { get; init; } = FeatureValue<decimal>.Missing("CoinGecko spot aggregate");
    public FeatureValue<decimal> CirculatingSupply { get; init; } = FeatureValue<decimal>.Missing("CoinGecko");
    public FeatureValue<decimal> TotalSupply { get; init; } = FeatureValue<decimal>.Missing("CoinGecko");
    public FeatureValue<decimal> MaxSupply { get; init; } = FeatureValue<decimal>.Missing("CoinGecko");
    public FeatureValue<decimal> AthUsd { get; init; } = FeatureValue<decimal>.Missing("CoinGecko");
    public FeatureValue<decimal> OiMarketCapRatio { get; init; } = FeatureValue<decimal>.Missing("Verified identity required");
    public FeatureValue<decimal> VolumeMarketCapRatio { get; init; } = FeatureValue<decimal>.Missing("Verified identity required");
}
public sealed record MappingView(Instrument Instrument, AssetMapping? Mapping, CapitalFeatures Capital, bool CatalogContainsId);
public sealed record MappingState(CoinCatalog? Catalog, IReadOnlyList<AssetMapping> Mappings, IReadOnlyList<MarketCapitalSnapshot> Markets);
public interface IMarketCapitalProvider
{
    Task<CoinCatalog> GetCatalogAsync(CancellationToken ct);
    Task<IReadOnlyList<MarketCapitalSnapshot>> GetMarketsAsync(IReadOnlyList<string> ids, CancellationToken ct);
}
public interface ICapitalRuntimeStore
{
    CoinCatalog? Catalog { get; }
    void SetCatalog(CoinCatalog catalog);
    void SetMappings(IReadOnlyList<AssetMapping> mappings);
    void SetMarkets(IReadOnlyList<MarketCapitalSnapshot> markets);
    AssetMapping? GetMapping(int assetId);
    CapitalInput Get(int assetId);
}
public interface IMappingRepository { MappingState ReadState(); IReadOnlyList<AssetMapping> ReadHistory(int assetId); }
public interface IMappingPersistence
{
    Task SaveCatalogAsync(CoinCatalog catalog, CancellationToken ct);
    Task SaveMappingsAsync(IReadOnlyList<MappingChange> changes, CancellationToken ct);
    Task SaveMarketsAsync(IReadOnlyList<MarketCapitalSnapshot> markets, CancellationToken ct);
}
public sealed class MappingConflictException(string message) : Exception(message);
