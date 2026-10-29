using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Application;

public sealed class MappingService(IMappingRepository repository, IMappingPersistence persistence, ICapitalRuntimeStore capital,
    IMarketRuntimeStore market, AssetMappingEngine mappingEngine, CapitalFeatureEngine features, TimeProvider time)
{
    private readonly SemaphoreSlim _writes = new(1, 1); private bool _restored;
    private int _marketRefresh;
    public bool TakeRefreshRequest() => Interlocked.Exchange(ref _marketRefresh, 0) != 0;
    public IReadOnlyList<AssetMapping> History(int assetId) => repository.ReadHistory(assetId);
    public async Task RestoreAsync(CancellationToken ct)
    {
        await _writes.WaitAsync(ct);
        try
        {
            if (_restored) return;
            var state = repository.ReadState(); if (state.Catalog is { } c) capital.SetCatalog(c);
            capital.SetMappings(state.Mappings); capital.SetMarkets(state.Markets); _restored = true;
        }
        finally { _writes.Release(); }
    }
    public IReadOnlyList<MappingView> GetOverview() => market.GetAll().Select(m =>
    {
        var input = capital.Get(m.Instrument.AssetId);
        return new MappingView(m.Instrument, input.Mapping, features.Calculate(m, input, time.GetUtcNow()), input.CatalogContainsId);
    }).ToArray();
    public IReadOnlyList<MarketCoin> Search(string query)
    {
        query = query.Trim(); if (query.Length is < 1 or > 100) return [];
        return (capital.Catalog?.Coins ?? []).Where(c => c.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
            || c.Symbol.Equals(query, StringComparison.OrdinalIgnoreCase) || c.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.Id.Equals(query, StringComparison.OrdinalIgnoreCase)).ThenBy(c => c.Id, StringComparer.Ordinal).Take(100).ToArray();
    }
    public IReadOnlyList<MarketCoin> Candidates(int id)
    {
        var ids = capital.GetMapping(id)?.CandidateIds.ToHashSet(StringComparer.Ordinal) ?? [];
        return (capital.Catalog?.Coins ?? []).Where(c => ids.Contains(c.Id)).OrderBy(c => c.Id, StringComparer.Ordinal).Take(100).ToArray();
    }
    public async Task ApplyCatalogAsync(CoinCatalog catalog, CancellationToken ct)
    {
        if (catalog.Coins.Count == 0) throw new InvalidDataException("CoinGecko 目录为空");
        await _writes.WaitAsync(ct);
        try { await persistence.SaveCatalogAsync(catalog, ct); capital.SetCatalog(catalog); }
        finally { _writes.Release(); }
        await ReconcileAsync(ct);
    }
    public async Task ReconcileAsync(CancellationToken ct)
    {
        await _writes.WaitAsync(ct);
        try
        {
            if (capital.Catalog is not { } catalog) return;
            var persisted = repository.ReadState().Mappings.ToDictionary(m => m.AssetId); capital.SetMappings(persisted.Values.ToArray());
            var changes = new List<MappingChange>();
            foreach (var m in market.GetAll())
            {
                persisted.TryGetValue(m.Instrument.AssetId, out var old);
                var next = mappingEngine.Suggest(m.Instrument, catalog, old, time.GetUtcNow());
                if (!ReferenceEquals(next, old)) changes.Add(new(next, old?.Revision ?? 0));
            }
            if (changes.Count == 0) return;
            await persistence.SaveMappingsAsync(changes, ct); capital.SetMappings(changes.Select(c => c.Mapping).ToArray());
            Interlocked.Exchange(ref _marketRefresh, 1);
        }
        finally { _writes.Release(); }
    }
    public async Task<AssetMapping> SetManualAsync(int assetId, ManualMappingRequest request, CancellationToken ct)
    {
        await _writes.WaitAsync(ct);
        try
        {
            var instrument = market.Get(assetId)?.Instrument ?? throw new KeyNotFoundException("资产不存在");
            var catalog = capital.Catalog ?? throw new InvalidOperationException("目录尚未加载，不能确认未知身份");
            var old = repository.ReadState().Mappings.FirstOrDefault(m => m.AssetId == assetId);
            var next = mappingEngine.Manual(instrument, catalog, old, request, time.GetUtcNow());
            await persistence.SaveMappingsAsync([new(next, request.ExpectedRevision)], ct); capital.SetMappings([next]);
            Interlocked.Exchange(ref _marketRefresh, 1); return next;
        }
        finally { _writes.Release(); }
    }
    public async Task SaveMarketsAsync(IReadOnlyList<MarketCapitalSnapshot> markets, CancellationToken ct)
    {
        await persistence.SaveMarketsAsync(markets, ct); capital.SetMarkets(markets);
    }
    public object Status() => new { CatalogReceivedTime = capital.Catalog?.ReceivedTime, CatalogCount = capital.Catalog?.Coins.Count ?? 0,
        ApiKeyConfigured = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PULLET_COINGECKO_API_KEY")) };
}
