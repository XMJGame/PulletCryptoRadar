using System.Text.Json;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.DataFoundation;
using PulletCryptoRadar.Infrastructure;

namespace PulletCryptoRadar.Tests;

public sealed class SupplementaryTests
{
    private static readonly DateTimeOffset Now = new(2026,11,3,8,0,0,TimeSpan.Zero);
    private const string Contract = "0x5d3a12c42e5372b2cc3264ab3cdcf660a1555238";
    private static readonly AssetIdentity Asset = new(Guid.NewGuid(), "ARIA", "AriaAI", "ARIAUSDT", "aria-ai", 38102,
        [new(ChainId.Bsc, Contract, 18)], Now);

    [Fact]
    public void SameTokenCapConflictKeepsBothSuppliesAndHasNoCanonicalCap()
    {
        const string gecko = """
            [{"id":"aria-ai","market_cap":21000000,"fully_diluted_valuation":114000000,
              "circulating_supply":183000000,"last_updated":"2026-11-03T07:59:00Z"}]
            """;
        const string cmc = """
            {"data":[{"id":38102,"circulating_supply":439999667,"self_reported_circulating_supply":183000000,
              "quote":[{"symbol":"USD","market_cap":50000000,"fully_diluted_market_cap":114000000,
                "last_updated":"2026-11-03T07:59:00Z"}]}]}
            """;
        var one = Parse("Gecko", gecko);
        var two = Parse("Cmc", cmc);
        var result = new CanonicalResolver().Resolve(Asset.AssetId, Metric.MarketCap,
            one.Observations.Concat(two.Observations), Now, TimeSpan.FromHours(8), "CoinGecko", "CoinMarketCap", true);
        Assert.Equal(ResolutionStatus.Conflict, result.Status);
        Assert.Null(result.Value);
        Assert.False(result.UsableForScoring);
        Assert.Equal(183000000m, Assert.Single(one.Observations, metric => metric.Metric == Metric.CirculatingSupply).Value);
        Assert.Equal(439999667m, Assert.Single(two.Observations, metric => metric.Metric == Metric.CirculatingSupply).Value);
        Assert.Contains("self_reported_circulating_supply", two.Evidence.Body);
    }

    [Fact]
    public void QuoteSideLiquidityRequiresTrustedCounterassetAndKeepsDirectionUnknown()
    {
        var body = $$"""
            [
              {"chainId":"bsc","pairAddress":"0x1111111111111111111111111111111111111111","dexId":"pancakeswap",
               "baseToken":{"address":"0xbb4CdB9CBd36B01bD1cBaEBF2De08d9173bc095c"},
               "quoteToken":{"address":"{{Contract}}"},"liquidity":{"usd":100},"priceUsd":"999"},
              {"chainId":"bsc","pairAddress":"0x2222222222222222222222222222222222222222","dexId":"other",
               "baseToken":{"address":"{{Contract}}"},"quoteToken":{"address":"0x3333333333333333333333333333333333333333"},
               "liquidity":{"usd":10000000},"priceUsd":"999"}
            ]
            """;
        var result = Parse("Dex", body);
        Assert.Equal(2, result.Pools!.Count);
        Assert.Equal(100m, Assert.Single(result.Observations).Value);
        Assert.Null(result.Pools[0].BasePriceUsd);
        Assert.False(result.Pools[0].TargetIsBase);
        Assert.False(result.Pools[1].VerifiedCounterAsset);
        Assert.Equal(TimeQuality.HttpDate, result.Observations[0].TimeQuality);
    }

    [Theory]
    [InlineData("""{"gini":1.1,"hhi":0,"nakamoto":1,"top_10_holder_concentration":10}""")]
    [InlineData("""{"gini":0,"hhi":0,"nakamoto":1.5,"top_10_holder_concentration":10}""")]
    [InlineData("""{"gini":0,"hhi":0,"nakamoto":1,"top_10_holder_concentration":101}""")]
    public void RejectsInvalidOwnershipMetrics(string body) =>
        Assert.Throws<InvalidDataException>(() => Parse("Distribution", body));

    [Fact]
    public void MissingProviderTimeDoesNotAcquireReplayQuality()
    {
        var result = Parse("Distribution", """{"gini":0,"hhi":0,"nakamoto":0,"top_10_holder_concentration":0}""");
        Assert.All(result.Observations, observation =>
        {
            Assert.Null(observation.SourceTime);
            Assert.Equal(TimeQuality.ReceivedOnly, observation.TimeQuality);
        });
        var resolved = new CanonicalResolver().Resolve(Asset.AssetId, Metric.Gini, result.Observations, Now,
            TimeSpan.FromHours(8), "InsightX");
        Assert.False(resolved.UsableForScoring);
        Assert.Equal(ResolutionStatus.Missing, resolved.Status);
        Assert.Null(resolved.Value);
        Assert.All(result.Observations,item => Assert.Equal(ObservationStatus.Missing,item.Status));
    }

    [Fact]
    public void MetadataScheduleDoesNotRepeatAfterRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "v2-metadata-" + Guid.NewGuid());
        try
        {
            var store = new FoundationStore(directory);
            store.Initialize(Now);
            Assert.True(store.ReserveJob("Metadata:Aria", Now, TimeSpan.FromHours(6)));
            Assert.False(new FoundationStore(directory).ReserveJob("Metadata:Aria", Now.AddHours(1), TimeSpan.FromHours(6)));
            Assert.True(store.ReserveJob("Metadata:Aria", Now.AddHours(6), TimeSpan.FromHours(6)));
            for (var i=0; i<4; i++) Assert.True(store.Reserve(Guid.NewGuid(), "InsightX", Now, 100));
            Assert.False(new FoundationStore(directory).Reserve(Guid.NewGuid(), "InsightX", Now.AddSeconds(1), 100));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                foreach (var file in Directory.GetFiles(directory)) File.Delete(file);
                Directory.Delete(directory);
            }
        }
    }

    private static ProviderRead Parse(string capability, string body) =>
        SupplementaryProvider.Parse(Asset, capability, body, Now, Now, Guid.NewGuid());
}
