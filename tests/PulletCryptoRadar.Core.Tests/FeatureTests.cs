using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Core.Tests;

public sealed class FeatureTests
{
    [Fact]
    public void StructureIncludesPriorLowAndRejectsHistoryThatEndsTooEarly()
    {
        var end = Start.AddMinutes(120);
        var bars = Enumerable.Range(0, 120).Select(x => Bar(x)).ToArray();
        var market = new MarketView(new(1, "TESTUSDT", "TEST", Start.AddDays(-30), true), 90, null, null, null, null,
            end, end, null, 120, 0, 0, new());
        var engine = new FeatureEngine();
        var features = engine.Calculate(market, bars, [], [], end);
        var priorLow = bars.Where(x => x.EndTime < end && x.EndTime >= end.AddHours(-1)).Min(x => x.Low);
        Assert.Equal((90m / priorLow - 1) * 100, features.DistanceToLow[60].Value);
        var incomplete = engine.Calculate(market, bars.SkipLast(2).ToArray(), [], [], end);
        Assert.Null(incomplete.DistanceToHigh[60].Value); Assert.Null(incomplete.DistanceToLow[60].Value);
    }
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static Candle Bar(int index, int interval = 1) => new("TESTUSDT", interval, Start.AddMinutes(index * interval),
        100 + index, 102 + index, 99 + index, 101 + index, 1000 + index, Start.AddMinutes((index + 1) * interval).AddMilliseconds(-1), Start.AddMinutes((index + 1) * interval));
    [Fact]
    public void RingEvictsOldestAndKeepsChronologicalOrder()
    {
        var ring = new RollingWindow(3); foreach (var i in Enumerable.Range(0, 5)) ring.Add(Bar(i));
        Assert.Equal(new[] { 2, 3, 4 }.Select(x => Bar(x).OpenTime), ring.Snapshot().Select(x => x.OpenTime));
    }
    [Fact]
    public void LateBackfillDoesNotEvictNewest()
    {
        var ring = new RollingWindow(3); ring.AddRange([Bar(5), Bar(6), Bar(7)]); ring.AddRange([Bar(0), Bar(1), Bar(6)]);
        Assert.Equal(3, ring.Count); Assert.Equal(Bar(7), ring.Snapshot()[^1]);
    }
    [Fact]
    public void DuplicateTimestampReplacesOnlyIfNewer()
    {
        var ring = new RollingWindow(3); var bar = Bar(1); ring.Add(bar);
        ring.Add(bar with { Close = 99, SourceTime = bar.SourceTime.AddSeconds(-1) });
        Assert.Equal(bar, Assert.Single(ring.Snapshot()));
        ring.Add(bar with { Close = 102, SourceTime = bar.SourceTime.AddSeconds(1) });
        Assert.Equal(102, Assert.Single(ring.Snapshot()).Close);
    }
    [Fact] public void InvalidCandleIsRejected() { var ring = new RollingWindow(3); ring.Add(Bar(1) with { Close = -1 }); Assert.Empty(ring.Snapshot()); }
    [Fact] public void ZeroCapacityRejected() => Assert.Throws<ArgumentOutOfRangeException>(() => new RollingWindow(0));
    [Theory] [InlineData(1)] [InlineData(3)] [InlineData(5)] [InlineData(15)] [InlineData(60)]
    public void ReturnUsesCorrectTimestamp(int horizon)
    {
        var bars = Enumerable.Range(0, 120).Select(x => Bar(x)).ToArray(); var now = Start.AddMinutes(120).AddSeconds(20);
        var result = FeatureEngine.Return(225, now, bars, horizon, now);
        var expected = (225m / bars[119 - horizon].Close - 1) * 100;
        Assert.Equal(expected, result.Value); Assert.Equal(DataStatus.Valid, result.Status);
    }
    [Fact]
    public void GapReturnsUnknownInsteadOfInventingPrice()
    {
        var bars = Enumerable.Range(0, 120).Where(x => x != 117).Select(x => Bar(x)).ToArray(); var now = Start.AddMinutes(120);
        Assert.Null(FeatureEngine.Return(225, now, bars, 5, now).Value);
    }
    [Fact] public void MissingPriceIsNotZero() => Assert.Null(FeatureEngine.Return(null, Start, [Bar(1)], 1, Start).Value);
    [Fact]
    public void StalePriceRetainsItsOwnAnchor()
    {
        var bars = Enumerable.Range(0, 120).Select(x => Bar(x)).ToArray(); var source = Start.AddMinutes(120);
        var result = FeatureEngine.Return(225, source, bars, 5, source.AddMinutes(10));
        Assert.Equal(DataStatus.Stale, result.Status); Assert.Equal((225m / Bar(114).Close - 1) * 100, result.Value);
    }
    [Fact]
    public void CurrentBarIsExcludedFromAnomalyBaseline()
    {
        var bars = Enumerable.Range(0, 50).Select(x => Bar(x, 5)).ToArray(); var latest = bars[^1];
        var now = latest.EndTime;
        var m = new MarketView(new(1, "TESTUSDT", "TEST", Start.AddDays(-20), true), latest.Close, 10000, 1, null, null, now, now, null, 0, 50, 0, new());
        var result = new FeatureEngine().Calculate(m, [], bars, [], now);
        Assert.Equal(48, result.BaselineSamples);
        var expected = RobustStatistics.RobustZ((double)latest.QuoteVolume, bars.SkipLast(1).Select(x => (double)x.QuoteVolume).ToArray());
        Assert.Equal(expected, result.VolumeRobustZ.Value);
        Assert.Equal(100, result.VolumePercentile.Value);
    }
}
