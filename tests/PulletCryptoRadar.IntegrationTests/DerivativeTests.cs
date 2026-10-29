using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;
using PulletCryptoRadar.Providers.Binance;

namespace PulletCryptoRadar.IntegrationTests;

public sealed class DerivativeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 4, 0, 0, TimeSpan.Zero);
    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();
    [Fact]
    public void CurrentOiRetainsExactQuantityAndRejectsWrongSymbol()
    {
        var json = Json($$"""{"symbol":"TESTUSDT","openInterest":"0.123456789012345678901","time":{{Now.ToUnixTimeMilliseconds()}}} """);
        var s = DerivativeParser.Current(json, "TESTUSDT", Now);
        Assert.Equal(.123456789012345678901m, s.Quantity); Assert.Equal(Now, s.SourceTime);
        Assert.Throws<InvalidDataException>(() => DerivativeParser.Current(json, "BTCUSDT", Now));
    }
    [Fact]
    public void TakerStartsAreConvertedToClosedPeriodEndsAndOpenPeriodsExcluded()
    {
        var json = Json($$"""[{"buyVol":"3","sellVol":"1","timestamp":{{Now.AddMinutes(-5).ToUnixTimeMilliseconds()}}},{"buyVol":"9","sellVol":"1","timestamp":{{Now.ToUnixTimeMilliseconds()}}}]""");
        var s = Assert.Single(DerivativeParser.Series(json, "TESTUSDT", DerivativeKind.Taker5m, Now));
        Assert.Equal(Now, s.SourceTime); Assert.Equal(3, s.Ratio); Assert.Equal(3, s.BuyQuantity);
    }
    [Fact]
    public void SeriesRetainsUnknownNotionalAndDoesNotInventRatioWhenSellIsZero()
    {
        var oi = Assert.Single(DerivativeParser.Series(Json($$"""[{"sumOpenInterest":"10","timestamp":{{Now.ToUnixTimeMilliseconds()}}}]"""), "TESTUSDT", DerivativeKind.OpenInterest5m, Now));
        Assert.Null(oi.NotionalUsd); Assert.Equal(10, oi.Quantity);
        var flow = Assert.Single(DerivativeParser.Series(Json($$"""[{"buyVol":"10","sellVol":"0","timestamp":{{Now.AddMinutes(-5).ToUnixTimeMilliseconds()}}}]"""), "TESTUSDT", DerivativeKind.Taker5m, Now));
        Assert.Null(flow.Ratio); Assert.Equal(0, flow.SellQuantity);
    }
    [Fact]
    public void RatioFractionsAreNotPercentagesAndMalformedSharesAreExcluded()
    {
        var json = Json($$"""[{"longAccount":"0.6","shortAccount":"0.4","longShortRatio":"1.5","timestamp":{{Now.ToUnixTimeMilliseconds()}}},{"longAccount":"60","shortAccount":"40","timestamp":{{Now.AddMinutes(-5).ToUnixTimeMilliseconds()}}}]""");
        var s = Assert.Single(DerivativeParser.Series(json, "TESTUSDT", DerivativeKind.TopPosition5m, Now));
        Assert.Equal(.6m, s.LongShare); Assert.Equal(1.5m, s.Ratio);
    }
    [Fact]
    public void LiquidationsUseCumulativeFilledQuantityAndActualAveragePrice()
    {
        var l = DerivativeParser.Liquidation(Json("""{"o":{"s":"TESTUSDT","S":"SELL","q":"100","p":"12","ap":"10","z":"3","T":0}}"""), Now);
        Assert.NotNull(l); Assert.Equal(30, l.NotionalUsd); Assert.Equal("SELL", l.Side);
    }
    [Fact]
    public void UnknownExecutionPriceDoesNotBecomeRequestedPrice()
    {
        Assert.Null(DerivativeParser.Liquidation(Json("""{"o":{"s":"TESTUSDT","S":"SELL","q":"100","p":"12","ap":"0","z":"3","T":0}}"""), Now));
    }
    [Fact]
    public void PendingAcknowledgmentDoesNotDropNewerConcurrentCorrection()
    {
        var store = new DerivativeRuntimeStore();
        var first = new DerivativeSample("TESTUSDT", DerivativeKind.CurrentOpenInterest, Now, Now, Quantity: 10);
        store.Apply(new DerivativeEvent(first.Symbol, [first], Now)); var batch = store.Pending();
        var next = first with { Quantity = 11, ReceivedTime = Now.AddSeconds(1) };
        store.Apply(new DerivativeEvent(first.Symbol, [next], next.ReceivedTime)); store.Acknowledge(batch);
        Assert.Equal(next, Assert.Single(store.Pending().Samples));
        store.Acknowledge(store.Pending()); Assert.Empty(store.Pending().Samples);
    }
    [Fact]
    public void RuntimeIgnoresFutureAndWrongSymbolAndPreservesNewestCorrection()
    {
        var store = new DerivativeRuntimeStore();
        var first = new DerivativeSample("TESTUSDT", DerivativeKind.OpenInterest5m, Now, Now, Quantity: 10);
        store.Apply(new DerivativeEvent(first.Symbol, [first, first with { SourceTime = Now.AddMinutes(1) }, first with { Symbol = "OTHERUSDT" }], Now));
        store.Apply(new DerivativeEvent(first.Symbol, [first with { Quantity = 9, ReceivedTime = Now.AddMinutes(-1) }], Now));
        Assert.Equal(10, Assert.Single(store.Get(first.Symbol).Series[first.Kind]).Quantity);
    }
    [Fact]
    public void LiquidationsDeduplicateCumulativeFillsAndDisconnectResetsCoverage()
    {
        var store = new DerivativeRuntimeStore(); var l = new LiquidationObservation("TESTUSDT", "SELL", 2, 10, Now, Now);
        store.Apply(new LiquidationCoverageEvent(true, Now.AddHours(-1), Now));
        store.Apply(new LiquidationEvent(l)); store.Apply(new LiquidationEvent(l)); store.Apply(new LiquidationEvent(l with { Quantity = 3 }));
        Assert.Equal(30, Assert.Single(store.Get(l.Symbol).Liquidations).NotionalUsd);
        store.Apply(new LiquidationCoverageEvent(false, Now.AddHours(-1), Now)); Assert.Null(store.Get(l.Symbol).CoverageStart);
        store.Apply(new LiquidationCoverageEvent(true, Now.AddMinutes(-1), Now.AddSeconds(1)));
        Assert.Equal(Now.AddMinutes(-1), store.Get(l.Symbol).CoverageStart);
    }
    [Fact]
    public void RetentionEvictionReducesLocalObservationCoverage()
    {
        var store = new DerivativeRuntimeStore(); store.Apply(new LiquidationCoverageEvent(true, Now.AddDays(-1), Now));
        for (var i = 0; i < 4100; i++) store.Apply(new LiquidationEvent(new("TESTUSDT", "SELL", 1, 1, Now.AddSeconds(i - 4100), Now)));
        var raw = store.Get("TESTUSDT"); Assert.Equal(4096, raw.Liquidations.Count);
        Assert.True(raw.CoverageStart > Now.AddSeconds(-4100));
    }
    [Fact]
    public void DerivativePersistenceIsIdempotentAndDecimalExactAcrossRestore()
    {
        var root = Path.Combine(Path.GetTempPath(), "PulletCryptoRadar-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var db = new SqliteDatabase(Options.Create(new StorageOptions { Directory = root, BackupDirectory = Path.Combine(root, "Backup") }));
            db.Initialize(); var store = new DerivativeRuntimeStore();
            var s = new DerivativeSample("TESTUSDT", DerivativeKind.OpenInterest5m, Now, Now, Quantity: .123456789012345678901m);
            store.Apply(new DerivativeEvent(s.Symbol, [s], Now)); db.SaveDerivatives(store.Pending()); db.SaveDerivatives(store.Pending());
            Assert.Equal(s, Assert.Single(new DerivativeRepository(db).Read(s.Symbol)));
            using var c = db.Open(true); Assert.Equal(1, c.QuerySingle<int>("SELECT count(*) FROM DerivativeSnapshot"));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
