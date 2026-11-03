using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;

namespace PulletCryptoRadar.IntegrationTests;
public sealed class MarketIdentityTests
{
    [Theory][InlineData(null)][InlineData("")][InlineData(" ")]
    public void AnonymousCandlesCannotEnterAValidMarketHistory(string? symbol)
    {
        var now=DateTimeOffset.UtcNow;var candle=new Candle(symbol!,5,now.AddMinutes(-5),1,1,1,1,10,now,now);
        Assert.False(candle.IsValid);
    }
    [Fact]public void RootEventSymbolCannotRelabelCandlesFromAnotherInstrument()
    {
        var now=DateTimeOffset.UtcNow;var runtime=new MarketRuntimeStore();runtime.Register([new(1,"REALUSDT","REAL",now,true)]);
        var correct=new Candle("REALUSDT",5,now.AddMinutes(-5),1,1,1,1,10,now,now);var wrong=correct with{Symbol="COPYUSDT"};
        runtime.Apply(new HistoryEvent("REALUSDT",[correct,wrong with{OpenTime=now.AddMinutes(-10)},correct with{Symbol=null!}],now));
        runtime.Apply(new CandleEvent(wrong));runtime.Apply(new CandleEvent(correct with{Symbol=null!}));
        Assert.Equal("REALUSDT",Assert.Single(runtime.GetCandles(1,5)).Symbol);
    }
}
