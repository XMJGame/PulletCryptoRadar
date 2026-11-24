using PulletCryptoRadar.Core;
using PulletCryptoRadar.Host;

namespace PulletCryptoRadar.Tests;

public sealed class ConfirmationStageTests
{
    [Fact]
    public void HistoricalConfirmedWideStopIsRejectedBySamePaperMarginAdmissionAsRuntime()
    {
        var start=new DateTimeOffset(2025,9,1,0,0,0,TimeSpan.Zero);var received=start.AddMonths(1);
        var asset=BinanceIdentity.AssetId("TESTUSDT");
        var bars=Enumerable.Range(0,304).Select(i=>
        {
            var close=100+i*.1m;var open=close-.05m;var high=close+1;var low=close-1;var volume=1000m;
            if(i==300){close=130;open=129.9m;high=130.3m;low=123;volume=3000;}
            if(i==301){close=131;open=130.8m;high=131.4m;low=129;volume=3000;}
            var at=start.AddHours(i-300);
            return new HourCandle(asset,at,at.AddHours(1),received,open,high,low,close,volume,volume*.6m);
        }).ToArray();
        var oi=Enumerable.Range(-288,337).Select(i=>new OpenInterestPoint(asset,start.AddMinutes(i*5),received,
            100000+i*100,130*(100000+i*100))).ToArray();
        var data=new QuantResearchDataset("TESTUSDT",start,start.AddHours(4),"Fixture",bars,bars,oi,
            [new(start.AddHours(-8),8,.0001m)],[],[]);
        var legacy=new QuantReplayEngine().Run(data,MarketState.Mixed);
        Assert.Single(legacy.Trades);Assert.Equal(1,legacy.Confirmations);
        var guarded=new QuantReplayEngine().Run(data,MarketState.Mixed,marginPolicy:new());
        Assert.Empty(guarded.Trades);Assert.Equal(legacy.Confirmations,guarded.Confirmations);
        Assert.Equal(1,guarded.Reasons["PaperLiquidationBufferInsufficient"]);
        Assert.Equal(1000,guarded.FinalCash);
    }
}
