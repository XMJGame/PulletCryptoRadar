using System.Collections.Concurrent;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Infrastructure;

public sealed class CapitalRuntimeStore : ICapitalRuntimeStore
{
    private sealed record CatalogState(CoinCatalog Catalog, IReadOnlySet<string> Ids);
    private CatalogState? _catalog;
    private readonly ConcurrentDictionary<int, AssetMapping> _mappings = new();
    private readonly ConcurrentDictionary<string, MarketCapitalSnapshot> _markets = new(StringComparer.Ordinal);
    public CoinCatalog? Catalog => Volatile.Read(ref _catalog)?.Catalog;
    public void SetCatalog(CoinCatalog catalog)
    {
        if (Catalog?.ReceivedTime > catalog.ReceivedTime) return;
        Volatile.Write(ref _catalog, new(catalog, catalog.Coins.Select(c => c.Id).ToHashSet(StringComparer.Ordinal)));
    }
    public void SetMappings(IReadOnlyList<AssetMapping> mappings)
    {
        foreach (var m in mappings) _mappings.AddOrUpdate(m.AssetId, m, (_, old) => m.Revision >= old.Revision ? m : old);
    }
    public void SetMarkets(IReadOnlyList<MarketCapitalSnapshot> markets)
    {
        foreach (var m in markets) _markets.AddOrUpdate(m.CoinGeckoId, m, (_, old) => m.SourceTime > old.SourceTime
            || m.SourceTime == old.SourceTime && m.ReceivedTime >= old.ReceivedTime ? m : old);
    }
    public AssetMapping? GetMapping(int assetId) => _mappings.GetValueOrDefault(assetId);
    public CapitalInput Get(int assetId)
    {
        var mapping = GetMapping(assetId); var id = mapping?.CoinGeckoId;
        return new(mapping, id is null ? null : _markets.GetValueOrDefault(id), id is not null && Volatile.Read(ref _catalog)?.Ids.Contains(id) == true);
    }
}
