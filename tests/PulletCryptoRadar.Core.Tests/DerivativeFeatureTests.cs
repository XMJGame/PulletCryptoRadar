using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Core.Tests;

public sealed class DerivativeFeatureTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 4, 0, 0, TimeSpan.Zero);
    private static MarketView Market() => new(new(1, "TESTUSDT", "TEST", Now.AddDays(-30), true), 20, null, 0, 20, null,
        Now, Now, Now, 500, 100, 100, new() { Returns = new() { [15] = new(5, DataStatus.Valid, Now, "Fixture") } });
    private static DerivativeRuntimeSnapshot Snapshot(params DerivativeSample[] samples) => new("TESTUSDT",
        samples.GroupBy(x => x.Kind).ToDictionary(x => x.Key, x => (IReadOnlyList<DerivativeSample>)x.OrderBy(p => p.SourceTime).ToArray()), [], null, null);
    private static DerivativeSample Oi(int offset, decimal quantity, decimal value = 100) => new("TESTUSDT", DerivativeKind.OpenInterest5m, Now.AddMinutes(offset), Now, quantity, value);
    private static DerivativeFeatures Calculate(DerivativeRuntimeSnapshot raw) => new DerivativeFeatureEngine().Calculate(Market(), raw, Now);
    [Fact]
    public void ChangesUseQuantityRatherThanRisingNotionalPrice()
    {
        var raw = Snapshot(Oi(-15, 100, 100), Oi(-10, 100, 200), Oi(-5, 100, 300), Oi(0, 100, 400));
        var d = Calculate(raw); Assert.Equal(0, d.OiChanges[15].Value); Assert.Null(d.OiRobustZ.Value);
        Assert.Equal("Neutral", d.PriceOiStructure); Assert.Equal(400, d.OpenInterestUsd.Value);
    }
    [Fact]
    public void MissingBucketCannotBeSilentlyBridged()
    {
        var d = Calculate(Snapshot(Oi(-15, 100), Oi(-5, 110), Oi(0, 120)));
        Assert.Null(d.OiChanges[15].Value); Assert.Equal("Unknown", d.PriceOiStructure);
        Assert.Equal((120m / 110 - 1) * 100, d.OiChanges[5].Value);
    }
    [Fact]
    public void BaselineExcludesLatestChangeAndNeedsThirtySamples()
    {
        var points = Enumerable.Range(0, 34).Select(i => Oi((i - 33) * 5, 100 + i + i % 3)).ToArray();
        var d = Calculate(Snapshot(points)); Assert.Equal(32, d.OiBaselineSamples); Assert.NotNull(d.OiRobustZ.Value);
        Assert.Null(Calculate(Snapshot(points.TakeLast(20).ToArray())).OiRobustZ.Value);
    }
    [Fact]
    public void StaleHistoryDoesNotRemainValid()
    {
        var d = Calculate(Snapshot(Oi(-30, 100), Oi(-25, 110), Oi(-20, 120), Oi(-15, 130)));
        Assert.Equal(DataStatus.Stale, d.OpenInterestQuantity.Status); Assert.Null(d.OiChanges[15].Value);
    }
    [Fact]
    public void LiveOiUsdRequiresFreshMarkPrice()
    {
        var raw = Snapshot(new DerivativeSample("TESTUSDT", DerivativeKind.CurrentOpenInterest, Now, Now, Quantity: 100));
        var engine = new DerivativeFeatureEngine(); Assert.Equal(2000, engine.Calculate(Market(), raw, Now).OpenInterestUsd.Value);
        Assert.Null(engine.Calculate(Market() with { FundingSourceTime = Now.AddMinutes(-1) }, raw, Now).OpenInterestUsd.Value);
    }
    [Fact]
    public void TakerZeroVolumeIsUnknownAndTopPositionCanStartAtZero()
    {
        var points = Enumerable.Range(0, 4).Select(i => new DerivativeSample("TESTUSDT", DerivativeKind.TopPosition5m,
            Now.AddMinutes((i - 3) * 5), Now, LongShare: i * .1m)).ToArray();
        var d = Calculate(Snapshot([.. points, new("TESTUSDT", DerivativeKind.Taker5m, Now, Now, BuyQuantity: 0, SellQuantity: 0)]));
        Assert.Null(d.TakerBuyShare.Value); Assert.Equal(30, d.TopPositionChange15m.Value);
    }
    [Fact]
    public void ObservedLiquidationTotalsRequireUninterruptedWindow()
    {
        var raw = Snapshot() with { CoverageStart = Now.AddMinutes(-16), LastStreamActivity = Now,
            Liquidations = [new("TESTUSDT", "SELL", 10, 20, Now.AddMinutes(-1), Now), new("TESTUSDT", "BUY", 5, 20, Now, Now)] };
        var d = Calculate(raw); Assert.True(d.LiquidationCoverageComplete); Assert.Equal(200, d.ObservedLongLiquidation15mUsd.Value);
        Assert.Equal(100, d.ObservedShortLiquidation15mUsd.Value); Assert.Null(d.LiquidationRobustZ.Value);
        Assert.Null(Calculate(raw with { CoverageStart = Now.AddMinutes(-10) }).ObservedLongLiquidation15mUsd.Value);
        Assert.Null(Calculate(raw with { LastStreamActivity = Now.AddSeconds(-46) }).ObservedLongLiquidation15mUsd.Value);
    }
    [Fact]
    public void ConfirmedEmptyObservationWindowIsZeroButDisconnectedIsUnknown()
    {
        var raw = Snapshot() with { CoverageStart = Now.AddHours(-1), LastStreamActivity = Now };
        Assert.Equal(0, Calculate(raw).ObservedLongLiquidation15mUsd.Value);
        Assert.Null(Calculate(raw with { CoverageStart = null }).ObservedLongLiquidation15mUsd.Value);
    }
}
