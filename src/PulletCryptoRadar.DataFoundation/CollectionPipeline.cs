using PulletCryptoRadar.Core;
using PulletCryptoRadar.Features;

namespace PulletCryptoRadar.DataFoundation;

public sealed class CollectionPipeline(IMarketDataProvider provider, IFoundationEvidenceStore store, TimeProvider clock,
    ISupplementaryProvider? supplementary = null)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, SemaphoreSlim> gates = new();
    private async Task<IDisposable> EnterAsync(Guid assetId, CancellationToken token)
    {
        var gate = gates.GetOrAdd(assetId,_ => new SemaphoreSlim(1,1));
        await gate.WaitAsync(token);
        return new ReleaseGate(gate);
    }
    private sealed class ReleaseGate(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }

    public async Task<FeatureSnapshot> CollectAsync(AssetIdentity asset, CancellationToken token)
    {
        using var lease = await EnterAsync(asset.AssetId,token);
        var reads = new List<ProviderRead>();
        foreach (var capability in new[] { "Ticker", "Mark", "OpenInterest", "FundingInfo" })
            reads.Add(await provider.ReadAsync(asset, capability, token));
        return Save(asset, reads);
    }

    public async Task<FeatureSnapshot> CollectSupplementaryAsync(AssetIdentity asset, CancellationToken token,bool includeOwnership = true)
    {
        using var lease = await EnterAsync(asset.AssetId,token);
        if (supplementary is null) throw new InvalidOperationException("Supplementary provider is not configured.");
        var reads = new List<ProviderRead>();
        var capabilities = new List<string>();
        if (asset.CoinGeckoId is not null) capabilities.Add("Gecko");
        if (asset.CoinMarketCapId is not null) capabilities.Add("Cmc");
        if (ResearchNetworks.Single(asset) is not null)
        {
            capabilities.Add("Dex");
            if(includeOwnership) capabilities.AddRange(["Distribution","Clusters"]);
        }
        foreach (var capability in capabilities)
            reads.Add(await supplementary.ReadAsync(asset, capability, token));
        return Save(asset, reads);
    }

    public async Task<FeatureSnapshot> CollectCandlesAsync(AssetIdentity asset, CancellationToken token)
    {
        using var lease = await EnterAsync(asset.AssetId,token);
        var read = await provider.ReadAsync(asset, "Candles", token);
        if (read.Candles is { Count: > 0 } candles)
            read = read with { Observations = new HourFeatureEngine().Calculate(asset.AssetId, candles, clock.GetUtcNow())
                .Concat(new FourHourFeatureEngine().Calculate(asset.AssetId, candles, clock.GetUtcNow())).ToArray() };
        return Save(asset, [read]);
    }

    public async Task<FeatureSnapshot> CollectOpenInterestHistoryAsync(AssetIdentity asset, CancellationToken token)
    {
        using var lease = await EnterAsync(asset.AssetId,token);
        var read = await provider.ReadAsync(asset, "OpenInterestHistory", token);
        if (read.OpenInterestHistory is { } points)
            read = read with { Observations = new OpenInterestFeatureEngine().Calculate(asset.AssetId, points, clock.GetUtcNow()) };
        return Save(asset, [read]);
    }

    private FeatureSnapshot Save(AssetIdentity asset, IReadOnlyList<ProviderRead> reads)
    {
        var asOf = clock.GetUtcNow();
        var previous = store.Latest(asset.AssetId)?.Metrics.SelectMany(metric => metric.Evidence) ?? [];
        var rawObservations = previous.Concat(reads.SelectMany(read => read.Observations))
            .GroupBy(item => (item.Provider, item.Metric))
            .Select(group => group.OrderByDescending(item => item.ReceivedTime).First()).ToArray();
        var observations = rawObservations.Where(item => item.Metric is not (Metric.OpenInterestUsd or Metric.Funding8h))
            .Concat(new DerivativeFeatureEngine().Calculate(asset.AssetId, rawObservations, asOf)).ToArray();
        var resolver = new CanonicalResolver();
        var metrics = new[] { Metric.Price, Metric.Volume24h, Metric.MarkPrice, Metric.FundingRaw, Metric.OpenInterestQuantity,
                Metric.OpenInterestUsd, Metric.Funding8h, Metric.FundingIntervalHours }
            .Select(metric => resolver.Resolve(asset.AssetId, metric, observations, asOf,
                metric == Metric.FundingIntervalHours ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(90), "Binance")).ToList();
        foreach (var metric in new[] { Metric.MarketCap, Metric.Fdv, Metric.CirculatingSupply })
            metrics.Add(resolver.Resolve(asset.AssetId, metric, observations, asOf,
                TimeSpan.FromHours(8), "CoinGecko", "CoinMarketCap", compare: true));
        metrics.Add(resolver.Resolve(asset.AssetId, Metric.DexLiquidity, observations, asOf,
            TimeSpan.FromHours(8), "DEXScreener"));
        foreach (var metric in new[] { Metric.Gini, Metric.Hhi, Metric.Nakamoto, Metric.Top10, Metric.TotalClusterPercent })
            metrics.Add(resolver.Resolve(asset.AssetId, metric, observations, asOf, TimeSpan.FromHours(8), "InsightX"));
        foreach (var metric in new[] { Metric.Ema20H1, Metric.Ema60H1, Metric.Atr14H1, Metric.Return24hClosed, Metric.VolumeRatioH1, Metric.TakerBuyShareH1 })
            metrics.Add(resolver.Resolve(asset.AssetId, metric, observations, asOf, TimeSpan.FromMinutes(75), "Binance"));
        foreach (var metric in new[] { Metric.Ema20H4, Metric.Ema60H4, Metric.CloseH4 })
            metrics.Add(resolver.Resolve(asset.AssetId, metric, observations, asOf, TimeSpan.FromHours(4.25), "Binance"));
        foreach (var metric in OpenInterestFeatureEngine.Metrics)
            metrics.Add(resolver.Resolve(asset.AssetId, metric, observations, asOf, TimeSpan.FromMinutes(10), "Binance"));
        var snapshot = new FeatureSnapshotBuilder().Build(asset.AssetId, asOf, metrics, store.MarketContext());
        store.Save(snapshot, reads);
        return snapshot;
    }
}
