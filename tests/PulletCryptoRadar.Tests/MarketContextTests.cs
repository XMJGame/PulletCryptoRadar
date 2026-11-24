using System.Text.Json;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Features;
using PulletCryptoRadar.Infrastructure;

namespace PulletCryptoRadar.Tests;

public sealed class MarketContextTests
{
    private static readonly DateTimeOffset Now = new(2026,11,3,8,0,0,TimeSpan.Zero);
    private static readonly Guid Asset = Guid.NewGuid();

    [Fact]
    public void FeatureSnapshotCannotUseFutureOrExpiredMarketContext()
    {
        var context = new MarketContext("test",Now,MarketState.StrongBear,40,40,1,.1m,-3,[]);
        var builder = new FeatureSnapshotBuilder();
        Assert.Equal(MarketState.StrongBear,builder.Build(Asset,Now,[],context).MarketState);
        Assert.Equal(MarketState.Unknown,builder.Build(Asset,Now,[],context with { AsOf = Now.AddSeconds(1) }).MarketState);
        Assert.Equal(MarketState.Unknown,builder.Build(Asset,Now.AddMinutes(4),[],context).MarketState);
    }

    [Fact]
    public void FourHourAggregationIsAlignedAndIgnoresIncompleteTail()
    {
        var input = Enumerable.Range(0,303).Select(index =>
        {
            var open = Now.AddHours(index-300);
            var price = 100m+index;
            return new HourCandle(Asset,open,open.AddHours(1),Now.AddHours(3),price,price+1,price-1,price,100);
        }).ToArray();
        var result = new FourHourFeatureEngine().Calculate(Asset,input,Now.AddHours(3));
        Assert.Equal(399m,Assert.Single(result,metric=>metric.Metric==Metric.CloseH4).Value);
        Assert.All(result,metric=>Assert.Equal(Now,metric.SourceTime));
        var broken = input.ToList();
        broken.RemoveAt(20);
        Assert.Throws<ArgumentException>(()=>new FourHourFeatureEngine().Calculate(Asset,broken,Now.AddHours(3)));
    }

    [Theory]
    [InlineData(40,1,MarketState.StrongBull)]
    [InlineData(25,1,MarketState.Bull)]
    [InlineData(5,-1,MarketState.StrongBear)]
    [InlineData(16,-1,MarketState.Bear)]
    [InlineData(20,0,MarketState.Mixed)]
    public void VersionedRegimeUsesBreadthAndBothBenchmarks(int positive, int trend, MarketState expected)
    {
        var universe = Universe();
        var tickers = universe.Select((item,index)=>new MarketTicker(item.Symbol,index<positive?2:-2,100,Now)).ToArray();
        var context = new MarketStateEngine().Calculate(universe,Now,tickers,Trend(trend),Trend(trend),Now);
        Assert.Equal(expected,context.State);
        Assert.Equal(1m,context.Coverage);
        Assert.Equal(positive/40m,context.PositiveBreadth24h);
    }

    [Fact]
    public void MissingCoverageOrBenchmarkCannotBecomeBullish()
    {
        var universe=Universe();
        var tickers=universe.Take(20).Select(item=>new MarketTicker(item.Symbol,10,100,Now)).ToArray();
        var context=new MarketStateEngine().Calculate(universe,Now,tickers,Trend(1),Trend(1),Now);
        Assert.Equal(MarketState.Unknown,context.State);
        Assert.Equal(.5m,context.Coverage);
        Assert.Contains("InsufficientMarketCoverage",context.Reasons);
        tickers=universe.Select(item=>new MarketTicker(item.Symbol,10,100,Now)).ToArray();
        context=new MarketStateEngine().Calculate(universe,Now,tickers,Trend(1),null,Now);
        Assert.Equal(MarketState.Unknown,context.State);
        Assert.Contains("BtcEth4hUnavailable",context.Reasons);
    }

    [Fact]
    public void FutureDuplicatesAndStaleQuotesDoNotInflateCoverage()
    {
        var universe=Universe();
        var quotes=universe.Take(30).Select(item=>new MarketTicker(item.Symbol,1,100,Now)).ToList();
        quotes.AddRange(quotes);
        quotes.AddRange(universe.Skip(30).Select(item=>new MarketTicker(item.Symbol,1,100,Now.AddSeconds(1))));
        var result=new MarketStateEngine().Calculate(universe,Now,quotes,Trend(1),Trend(1),Now);
        Assert.Equal(30,result.FreshQuotes);
        Assert.Equal(.75m,result.Coverage);
        Assert.Equal(MarketState.Unknown,result.State);
    }

    [Fact]
    public void UniverseFiltersContractsAndStablecoins()
    {
        object Symbol(string symbol,string baseAsset,string contract,string status="TRADING",string quote="USDT") =>
            new {symbol,baseAsset,contractType=contract,status,quoteAsset=quote,marginAsset=quote,onboardDate=Now.ToUnixTimeMilliseconds()};
        var body=JsonSerializer.Serialize(new { symbols=new[]
        {
            Symbol("BTCUSDT","BTC","PERPETUAL"),Symbol("BTCUSD","BTC","PERPETUAL",quote:"USD"),
            Symbol("USDCUSDT","USDC","PERPETUAL"),Symbol("OLDUSDT","OLD","PERPETUAL","SETTLING"),
            Symbol("BTCUSDT_261225","BTC","CURRENT_QUARTER")
        }});
        Assert.Equal("BTCUSDT",Assert.Single(BinanceMarketProvider.ParseUniverse(body)).Symbol);
    }

    private static MarketInstrument[] Universe() => Enumerable.Range(0,40).Select(index=>
        new MarketInstrument("ASSET"+index+"USDT","ASSET"+index,Now.AddDays(-30))).ToArray();
    private static FeatureSnapshot Trend(int direction)
    {
        ResolvedMetric Field(Metric metric,decimal value) => new(metric,ResolutionStatus.SingleSource,value,"Binance",true,
            [new(Asset,metric,value,"Binance","fixture",Now,Now,ObservationStatus.Valid,TimeQuality.ProviderTime)]);
        return new("test",Asset,Now,MarketState.Unknown,
            [Field(Metric.Ema20H4,100+direction),Field(Metric.Ema60H4,100),Field(Metric.CloseH4,100+direction*2)]);
    }
}
