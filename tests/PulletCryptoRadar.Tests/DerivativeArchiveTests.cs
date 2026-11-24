using System.Globalization;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Host;
using PulletCryptoRadar.Infrastructure;
using PulletCryptoRadar.Trading;

namespace PulletCryptoRadar.Tests;

public sealed class DerivativeArchiveTests
{
    private static readonly DateTimeOffset Month=new(2025,9,1,0,0,0,TimeSpan.Zero);
    private static readonly DateTimeOffset Now=new(2026,11,4,0,0,0,TimeSpan.Zero);
    [Fact]
    public void ScientificFundingAndVariableIntervalsRemainSignedAndOriginalTimesPreserved()
    {
        var csv=$"calc_time,funding_interval_hours,last_funding_rate\n{Month.ToUnixTimeMilliseconds()},1,-8.7E-7\n{Month.AddHours(4).ToUnixTimeMilliseconds()},4,0.0005\n";
        var result=BinanceHistoricalArchive.ParseFunding(Month,csv,Now);
        Assert.Equal(-.00000087m,result[0].Rate);Assert.Equal(Month,result[0].At);Assert.Equal(4,result[1].IntervalHours);
        Assert.Throws<InvalidDataException>(()=>BinanceHistoricalArchive.ParseFunding(Month.AddMonths(1),csv,Now));
        Assert.Throws<InvalidDataException>(()=>BinanceHistoricalArchive.ParseFunding(Month,csv,Month.AddMinutes(-1)));
    }
    [Fact]
    public void OiIdentityAndSourceTimestampAreValidatedAndMissingBucketsAreNotFilled()
    {
        const string header="create_time,symbol,sum_open_interest,sum_open_interest_value\n";
        var csv=header+"2025-09-01 00:00:00,TESTUSDT,1.2E3,120000\n2025-09-01 00:10:00,TESTUSDT,1300,130000\n";
        var result=BinanceHistoricalArchive.ParseMetrics("TESTUSDT",Month,csv,Now);
        Assert.Equal(2,result.Count);Assert.Equal(1200,result[0].Quantity);Assert.Equal(Month,result[0].SourceTime);
        Assert.Equal(TimeSpan.FromMinutes(10),result[1].SourceTime-result[0].SourceTime);
        Assert.Throws<InvalidDataException>(()=>BinanceHistoricalArchive.ParseMetrics("OTHERUSDT",Month,csv,Now));
        Assert.Throws<InvalidDataException>(()=>BinanceHistoricalArchive.ParseMetrics("TESTUSDT",Month,csv.Replace("00:10:00","00:11:00"),Now));
        Assert.Throws<InvalidDataException>(()=>BinanceHistoricalArchive.ParseMetrics("TESTUSDT",Month,csv.Replace("00:10:00","00:00:00"),Now));
    }
    [Fact]
    public void VirtualOiAvailabilityNeverIncludesBucketsWhoseAssumedReleaseIsLaterThanDecision()
    {
        var asset=Guid.NewGuid();var points=Enumerable.Range(0,5).Select(i=>new OpenInterestPoint(asset,Month.AddMinutes(i*5),Now,100,10000)).ToArray();
        var known=QuantReplayEngine.KnownOi(points,Month.AddMinutes(12)).ToArray();
        Assert.Equal(2,known.Length);Assert.Equal(Month.AddMinutes(5),known[^1].SourceTime);
        Assert.Empty(QuantReplayEngine.KnownOi(points,Month.AddMinutes(4)));
    }
    [Fact]
    public void MillisecondFundingTimesUseExplicitNearbyOpeningReferenceWithoutChangingSettlementTime()
    {
        var bar=new HourCandle(Guid.NewGuid(),Month,Month.AddHours(1),Now,100,101,99,100,0,0);
        Assert.Equal(bar,QuantReplayEngine.FundingMark([bar],Month.AddMilliseconds(3)));
        Assert.Null(QuantReplayEngine.FundingMark([bar],Month.AddSeconds(2)));
        Assert.Null(QuantReplayEngine.FundingMark([bar],Month.AddMilliseconds(-1)));
        var csv=$"calc_time,funding_interval_hours,last_funding_rate\n{Month.AddMilliseconds(3).ToUnixTimeMilliseconds()},4,0.0001";
        Assert.Equal(Month.AddMilliseconds(3),Assert.Single(BinanceHistoricalArchive.ParseFunding(Month,csv,Now)).At);
    }
    [Theory]
    [InlineData(TradeSide.Long)]
    [InlineData(TradeSide.Short)]
    public void SameBarStopAndTargetAlwaysChooseAdverseStopFirst(TradeSide side)
    {
        var folder=Path.Combine(Path.GetTempPath(),"replay-exit-test-"+Guid.NewGuid());
        try
        {
            PaperLifecycleVerification.Run(folder,side,false);
            var position=new FoundationStore(folder).Portfolio().Positions.Single();
            position=position with {StopPrice=position.InitialStop,FirstTargetDone=false,SecondTargetDone=false,RemainingQuantity=position.InitialQuantity};
            var bar=new HourCandle(position.AssetId,Month,Month.AddHours(1),Now,100,110,90,105,1000,600);
            var path=QuantReplayEngine.ConservativeExitPath(position,bar);
            Assert.Single(path);Assert.Equal(side==TradeSide.Long?Math.Min(position.StopPrice,100):Math.Max(position.StopPrice,100),path[0]);
        }
        finally{if(Directory.Exists(folder))Directory.Delete(folder,true);}
    }
    [Fact]
    public void ReplayRejectsWrongAssetAndRecordsWarmupWithoutInventingTrades()
    {
        var bars=Enumerable.Range(0,24).Select(i=>new HourCandle(BinanceIdentity.AssetId("TESTUSDT"),Month.AddHours(i),Month.AddHours(i+1),Now,
            100,101,99,100,1000,600)).ToArray();
        var data=new QuantResearchDataset("TESTUSDT",Month,Month.AddDays(1),"Test",bars,[],[],[],[],[]);
        var result=new QuantReplayEngine().Run(data,MarketState.Mixed);
        Assert.Empty(result.Trades);Assert.Equal(1000,result.FinalCash);Assert.Equal(23,result.CandleMissingBars);
        Assert.Throws<InvalidDataException>(()=>new QuantReplayEngine().Run(data with {Symbol="OTHERUSDT"},MarketState.Mixed));
        Assert.Throws<ArgumentException>(()=>new QuantReplayEngine().Run(data,MarketState.Unknown));
    }
    [Theory]
    [InlineData(TradeSide.Long)]
    [InlineData(TradeSide.Short)]
    public void ExecutionFrictionIsAdverseOnBothSidesWithoutSilentlyWideningQuotedSpread(TradeSide side)
    {
        var asset=Guid.NewGuid();var entry=QuantReplayEngine.AssumedBook(asset,100,Now,side,false,1,1);
        var exit=QuantReplayEngine.AssumedBook(asset,100,Now,side,true,1,2);
        var spread=(entry.Asks[0].Price-entry.Bids[0].Price)/((entry.Asks[0].Price+entry.Bids[0].Price)/2);
        Assert.Equal(.0002m,spread);
        Assert.True(side==TradeSide.Long?entry.Asks[0].Price>100:entry.Bids[0].Price<100);
        Assert.True(side==TradeSide.Long?exit.Bids[0].Price<100:exit.Asks[0].Price>100);
    }
    [Theory]
    [InlineData(TradeSide.Long,1)]
    [InlineData(TradeSide.Short,1)]
    [InlineData(TradeSide.Long,2)]
    [InlineData(TradeSide.Short,2)]
    public void IntrabarTargetPathMustActuallyTriggerScaleOutAfterExecutionFriction(TradeSide side,int multiplier)
    {
        var folder=Path.Combine(Path.GetTempPath(),"replay-target-test-"+Guid.NewGuid());
        try
        {
            PaperLifecycleVerification.Run(folder,side,false);
            var position=new FoundationStore(folder).Portfolio().Positions.Single();
            position=position with {StopPrice=position.InitialStop,FirstTargetDone=false,SecondTargetDone=false,
                RemainingQuantity=position.InitialQuantity,ClosedAt=null,LastObservedAt=position.OpenedAt,LastBookUpdateId=0};
            var sign=side==TradeSide.Long?1:-1;var distance=Math.Abs(position.EntryPrice-position.InitialStop);
            var near=position.EntryPrice-sign*.1m*distance;var far=position.EntryPrice+sign*1.8m*distance;
            var at=position.OpenedAt.AddHours(2);
            var bar=new HourCandle(position.AssetId,at.AddHours(-1),at,at,position.EntryPrice,
                Math.Max(near,far),Math.Min(near,far),position.EntryPrice+sign*.25m*distance,1000,600);
            var path=QuantReplayEngine.ConservativeExitPath(position,bar,multiplier);Assert.Equal(2,path.Count);
            var portfolio=new PaperPortfolio(Guid.NewGuid(),1000,1000,0,position.OpenedAt,[position]);
            var book=QuantReplayEngine.AssumedBook(position.AssetId,path[0],at,side,true,multiplier,1);
            var transition=new PaperPortfolioEngine().Observe(portfolio,position.PositionId,book,Guid.NewGuid(),at);
            Assert.True(transition.Portfolio.Positions.Single().FirstTargetDone);
            Assert.True(transition.Portfolio.Positions.Single().RemainingQuantity<position.InitialQuantity);
            var rawTarget=position.EntryPrice+sign*1.5m*distance;
            var touching=bar with {High=Math.Max(near,rawTarget),Low=Math.Min(near,rawTarget)};
            Assert.Single(QuantReplayEngine.ConservativeExitPath(position,touching,multiplier));
        }
        finally{if(Directory.Exists(folder))Directory.Delete(folder,true);}
    }
}
