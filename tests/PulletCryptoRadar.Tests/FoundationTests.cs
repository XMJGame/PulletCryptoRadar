using Microsoft.Data.Sqlite;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.DataFoundation;
using PulletCryptoRadar.Features;
using PulletCryptoRadar.Infrastructure;

namespace PulletCryptoRadar.Tests;

public sealed class FoundationTests
{
    private static readonly Guid Asset = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 8, 0, 0, TimeSpan.Zero);
    private static Observation Sample(string source, decimal value, int ageMinutes = 1) =>
        new(Asset, Metric.MarketCap, value, source, "circulating", Now.AddMinutes(-ageMinutes),
            Now, ObservationStatus.Valid, TimeQuality.ProviderTime);

    [Fact]
    public void TradingSeriesCannotUseAnotherVenueAsBinance()
    {
        Assert.Throws<ArgumentException>(() => new CanonicalResolver().Resolve(Asset, Metric.OpenInterestUsd,
            [], Now, TimeSpan.FromMinutes(5), "Binance", "Gate"));
    }

    [Fact]
    public void ConflictHasNoScoringValueAndKeepsBothSources()
    {
        var result = new CanonicalResolver().Resolve(Asset, Metric.MarketCap,
            [Sample("Gecko", 30), Sample("CMC", 55)], Now, TimeSpan.FromMinutes(30), "Gecko", "CMC", true);
        Assert.Equal(ResolutionStatus.Conflict, result.Status);
        Assert.Null(result.Value);
        Assert.False(result.UsableForScoring);
        Assert.Equal(2, result.Evidence.Count);
    }

    [Fact]
    public void LatestInvalidObservationDoesNotReviveAnOlderValue()
    {
        var invalid = Sample("Gecko", 1) with { Status = ObservationStatus.Invalid };
        var older = Sample("Gecko", 30) with { ReceivedTime = Now.AddSeconds(-5) };
        var result = new CanonicalResolver().Resolve(Asset, Metric.MarketCap,
            [older, invalid, Sample("CMC", 55)], Now, TimeSpan.FromMinutes(30), "Gecko", "CMC");
        Assert.Equal("CMC", result.Provider);
    }

    [Fact]
    public void ProviderSwitchDoesNotBecomeGrowth()
    {
        var previous = Sample("Gecko", 80) with { ReceivedTime = Now.AddMinutes(-1) };
        var current = Sample("CMC", 84, 0);
        Assert.Null(CanonicalResolver.Change(previous, current));
        Assert.Equal(5m, CanonicalResolver.Change(previous, current with { Provider = "Gecko" }));
    }

    [Fact]
    public void FeaturesRejectFutureEvidenceAndMissingMetricsStayUnknown()
    {
        var result = new ResolvedMetric(Metric.Price, ResolutionStatus.SingleSource, 1, "Binance", true,
            [Sample("Binance", 1) with { ReceivedTime = Now.AddSeconds(1) }]);
        Assert.Throws<ArgumentException>(() => new FeatureSnapshotBuilder().Build(Asset, Now, [result]));
        var empty = new FeatureSnapshotBuilder().Build(Asset, Now, []);
        Assert.Equal(MarketState.Unknown, empty.MarketState);
        Assert.Empty(empty.Metrics);
    }

    [Fact]
    public void NewAccountRestartsWithoutResetAndDuplicateContractRollsBackIdentity()
    {
        var folder = Path.Combine(Path.GetTempPath(), "radar-v2-" + Guid.NewGuid());
        try
        {
            var store = new FoundationStore(folder);
            store.Initialize(Now);
            var account = store.Account();
            Assert.Equal(1000m, account.Cash);
            var restarted = new FoundationStore(folder);
            restarted.Initialize(Now.AddHours(1));
            Assert.Equal(account, restarted.Account());
            var identity = new AssetIdentity(Asset, "ARIA", "AriaAI", "ARIAUSDT", "aria-ai", 38102,
                [new(ChainId.Bsc, "0x5d3a12c42e5372b2cc3264ab3cdcf660a1555238", 18)], Now);
            store.Register(identity);
            Assert.Throws<SqliteException>(() => store.Register(identity with { AssetId = Guid.NewGuid() }));
            Assert.Single(store.Assets());
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                foreach (var file in Directory.GetFiles(folder)) File.Delete(file);
                Directory.Delete(folder);
            }
        }
    }
}
