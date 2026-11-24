using System.Text.Json;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Features;
using PulletCryptoRadar.DataFoundation;
using PulletCryptoRadar.Infrastructure;

namespace PulletCryptoRadar.Tests;

public sealed class DerivativeFeatureTests
{
    private static readonly Guid Asset = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026,11,3,8,0,0,TimeSpan.Zero);
    private static readonly AssetIdentity Identity = new(Asset,"BTC","Bitcoin","BTCUSDT",null,null,[],Now);
    private static Observation Input(Metric metric, decimal value, string method) =>
        new(Asset,metric,value,"Binance",method,Now,Now,ObservationStatus.Valid,TimeQuality.ProviderTime);
    private static Observation Mark => Input(Metric.MarkPrice,100,"USDT-perpetual-mark");
    private static Observation Quantity => Input(Metric.OpenInterestQuantity,12,"USDT-perpetual-base-quantity");
    private static Observation Funding => Input(Metric.FundingRaw,-.001m,"USDT-perpetual-raw-current-interval");
    private static Observation Calculate(Metric metric, params Observation[] inputs) =>
        Assert.Single(new DerivativeFeatureEngine().Calculate(Asset,inputs,Now), item => item.Metric == metric);

    [Fact]
    public void ConvertsNativeBaseQuantityUsingSynchronizedMark()
    {
        var value = Calculate(Metric.OpenInterestUsd,Mark,Quantity);
        Assert.Equal(1200m,value.Value);
        Assert.True(new CanonicalResolver().Resolve(Asset,Metric.OpenInterestUsd,[value],Now,
            TimeSpan.FromSeconds(90),"Binance").UsableForScoring);
    }

    [Theory]
    [InlineData("skew")]
    [InlineData("future")]
    [InlineData("unit")]
    [InlineData("foreign")]
    [InlineData("stale")]
    public void RejectsUnsafeOiConversion(string condition)
    {
        var quantity = condition switch
        {
            "skew" => Quantity with { SourceTime = Now.AddSeconds(-11) },
            "future" => Quantity with { SourceTime = Now.AddSeconds(1) },
            "unit" => Quantity with { Methodology = "contracts-unknown-multiplier" },
            "foreign" => Quantity with { AssetId = Guid.NewGuid() },
            _ => Quantity with { SourceTime = Now.AddSeconds(-91) }
        };
        Assert.Null(Calculate(Metric.OpenInterestUsd,Mark,quantity).Value);
    }

    [Theory]
    [InlineData(1,-.008)]
    [InlineData(4,-.002)]
    [InlineData(8,-.001)]
    public void PreservesFundingSignAndUnverifiedScheduleTime(int hours, double expected)
    {
        var body = $$"""[{"symbol":"BTCUSDT","fundingIntervalHours":{{hours}}}]""";
        var interval = Assert.Single(BinanceMarketProvider.ParseFundingInfo(Identity,body,Now));
        var normalized = Calculate(Metric.Funding8h,Funding,interval);
        Assert.Equal((decimal)expected,normalized.Value);
        var resolved = new CanonicalResolver().Resolve(Asset,Metric.Funding8h,[normalized],Now,
            TimeSpan.FromSeconds(90),"Binance");
        Assert.Equal(ResolutionStatus.UnverifiedTime,resolved.Status);
        Assert.False(resolved.UsableForScoring);
        Assert.Null(normalized.SourceTime);
    }

    [Fact]
    public void DefaultScheduleRequiresSuccessfulFullResponseAndRejectsMalformedRows()
    {
        var interval = Assert.Single(BinanceMarketProvider.ParseFundingInfo(Identity,"[]",Now));
        Assert.Equal(8m,interval.Value);
        Assert.Null(Calculate(Metric.Funding8h,Funding).Value);
        Assert.NotNull(Calculate(Metric.Funding8h,Funding,interval with { ReceivedTime = Now.AddSeconds(-91) }).Value);
        Assert.Null(Calculate(Metric.Funding8h,Funding,interval with { ReceivedTime = Now.AddMinutes(-5) }).Value);
        Assert.Throws<InvalidDataException>(() => BinanceMarketProvider.ParseFundingInfo(Identity,
            """[{"symbol":"BTCUSDT","fundingIntervalHours":0}]""",Now));
        Assert.Throws<InvalidDataException>(() => BinanceMarketProvider.ParseFundingInfo(Identity,
            """[{"symbol":"BTCUSDT","fundingIntervalHours":4},{"symbol":"BTCUSDT","fundingIntervalHours":8}]""",Now));
    }

    [Fact]
    public void ComputesClosedTakerShareAndHandlesMissingOrZeroVolume()
    {
        var candles = Enumerable.Range(0,61).Select(index => new HourCandle(Asset,Now.AddHours(index-61),
            Now.AddHours(index-60),Now,100,101,99,100,100,65)).ToArray();
        Observation Share() => Assert.Single(new HourFeatureEngine().Calculate(Asset,candles,Now),
            item => item.Metric == Metric.TakerBuyShareH1);
        Assert.Equal(.65m,Share().Value);
        candles[^1] = candles[^1] with { TakerBuyQuoteVolume = 0 };
        Assert.Equal(0m,Share().Value);
        candles[^1] = candles[^1] with { TakerBuyQuoteVolume = null };
        Assert.Null(Share().Value);
        candles[^1] = candles[^1] with { QuoteVolume = 0, TakerBuyQuoteVolume = 0 };
        Assert.Null(Share().Value);
        candles[^1] = candles[^1] with { TakerBuyQuoteVolume = 1 };
        Assert.Throws<ArgumentException>(() => Share());
    }

    [Fact]
    public void ParserValidatesTakerQuoteUnitsAndExcludesOpenBar()
    {
        object[] Bar(DateTimeOffset open, string buy) => [open.ToUnixTimeMilliseconds(),"100","101","99","100",
            "1",open.AddHours(1).ToUnixTimeMilliseconds()-1,"100",1,"1",buy,"0"];
        var rows = new[] { Bar(Now.AddHours(-1),"65"), Bar(Now,"99") };
        var candle = Assert.Single(BinanceMarketProvider.ParseCandles(Identity,JsonSerializer.Serialize(rows),Now));
        Assert.Equal(65m,candle.TakerBuyQuoteVolume);
        Assert.Throws<InvalidDataException>(() => BinanceMarketProvider.ParseCandles(Identity,
            JsonSerializer.Serialize(new[] { Bar(Now.AddHours(-1),"101") }),Now));
    }
}
