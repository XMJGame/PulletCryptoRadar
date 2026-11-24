using System.Text.Json;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Features;
using PulletCryptoRadar.Infrastructure;

namespace PulletCryptoRadar.Tests;

public sealed class HourFeatureTests
{
    private static readonly Guid Asset = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026,11,3,8,0,0,TimeSpan.Zero);

    private static HourCandle[] Candles() => Enumerable.Range(0,61).Select(index =>
    {
        var close = Now.AddHours(index - 60);
        return new HourCandle(Asset, close.AddHours(-1), close, Now, 100 + index,
            101 + index, 99 + index, 100 + index, index == 60 ? 200 : 100);
    }).ToArray();

    [Fact]
    public void ComputesDeterministicHourlyFeaturesWithoutOpenCandle()
    {
        var values = new HourFeatureEngine().Calculate(Asset, Candles(), Now);
        decimal? Value(Metric metric) => Assert.Single(values, item => item.Metric == metric).Value;
        Assert.Equal(150.5m, Value(Metric.Ema20H1));
        Assert.Equal(130.5m, Value(Metric.Ema60H1));
        Assert.Equal(2m, Value(Metric.Atr14H1));
        Assert.Equal(2m, Value(Metric.VolumeRatioH1));
        Assert.Equal((160m / 136m - 1) * 100, Value(Metric.Return24hClosed));
        Assert.All(values, value => Assert.Equal(Now, value.SourceTime));
    }

    [Fact]
    public void RejectsFutureReceiptsAndMissingHours()
    {
        var candles = Candles();
        candles[^1] = candles[^1] with { ReceivedAt = Now.AddSeconds(1) };
        Assert.Throws<ArgumentException>(() => new HourFeatureEngine().Calculate(Asset, candles, Now));
        candles = Candles();
        candles[5] = candles[5] with { OpenAt = candles[5].OpenAt.AddMinutes(1), ClosedAt = candles[5].ClosedAt.AddMinutes(1) };
        Assert.Throws<ArgumentException>(() => new HourFeatureEngine().Calculate(Asset, candles, Now));
        Assert.Empty(new HourFeatureEngine().Calculate(Asset, Candles().Take(60).ToArray(), Now));
    }

    [Fact]
    public void ZeroPreviousVolumeProducesMissingRatio()
    {
        var candles = Candles().Select(candle => candle with { QuoteVolume = 0 }).ToArray();
        var ratio = Assert.Single(new HourFeatureEngine().Calculate(Asset, candles, Now),
            observation => observation.Metric == Metric.VolumeRatioH1);
        Assert.Null(ratio.Value);
        Assert.Equal(ObservationStatus.Missing, ratio.Status);
    }

    [Fact]
    public void ParserExcludesUnclosedBarAndRejectsNegativeVolume()
    {
        var identity = new AssetIdentity(Asset,"BTC","BTC","BTCUSDT",null,null,[],Now);
        object[] Bar(DateTimeOffset open, string volume = "100") =>
            [open.ToUnixTimeMilliseconds(),"100","101","99","100","1",
                open.AddHours(1).ToUnixTimeMilliseconds()-1,volume,1,"1","1","0"];
        var rows = new[] { Bar(Now.AddHours(-2)), Bar(Now.AddHours(-1)), Bar(Now) };
        var parsed = BinanceMarketProvider.ParseCandles(identity, JsonSerializer.Serialize(rows), Now.AddMinutes(5));
        Assert.Equal(2, parsed.Count);
        Assert.Equal(Now, parsed[^1].ClosedAt);
        Assert.Throws<InvalidDataException>(() => BinanceMarketProvider.ParseCandles(identity,
            JsonSerializer.Serialize(new[] { Bar(Now.AddHours(-1), "-1") }), Now));
    }

    [Fact]
    public void SourceHealthKeepsLastSuccessAndReportsFailureAndAge()
    {
        var directory = Path.Combine(Path.GetTempPath(), "v2-health-" + Guid.NewGuid());
        try
        {
            var store = new FoundationStore(directory);
            store.Initialize(Now);
            store.Register(new(Asset,"BTC","BTC","BTCUSDT",null,null,[],Now));
            ProviderRead Read(DateTimeOffset at, CollectionFailure failure) =>
                new(new(Guid.NewGuid(),"Binance","BTCUSDT","/ticker",at,200,failure,"{}"),[]);
            FeatureSnapshot Snapshot(DateTimeOffset at) => new("test",Asset,at,MarketState.Unknown,[]);
            store.Save(Snapshot(Now), [Read(Now,CollectionFailure.None)]);
            Assert.Equal("Healthy", Assert.Single(store.SourceHealth(Now)).State);
            Assert.Equal("Stale", Assert.Single(store.SourceHealth(Now.AddMinutes(3))).State);
            store.Save(Snapshot(Now.AddSeconds(1)),[Read(Now.AddSeconds(1),CollectionFailure.Timeout)]);
            var status = Assert.Single(store.SourceHealth(Now.AddSeconds(1)));
            Assert.Equal("Degraded", status.State);
            Assert.Equal(Now, status.LastSuccess);
            Assert.Equal(1, status.ConsecutiveFailures);
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
}
