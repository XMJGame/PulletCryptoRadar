using System.Text.Json;
using Microsoft.Extensions.Options;
using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;
namespace PulletCryptoRadar.IntegrationTests;

public sealed class QuantReviewTests
{
    private static (PaperState State,PaperTrade Trade) Closed(bool shortSide=false)
    {
        var (state,f)=QuantLiveTests.Open(shortSide);var p=state.Positions[0];var at=state.UpdatedAt.AddHours(1);var price=shortSide?p.StopPrice+.1m:p.StopPrice-.1m;
        var result=QuantPaperEngine.Tick(state,[],[],QuantLiveTests.Input(f,at,price));var trade=Assert.Single(result.Trades);
        return(result.State with{QuantFunding=[new(p.Id,p.Symbol,"ObservedWindow",at,at,"fixture")]},trade);
    }
    private static QuantReview Review(PaperState state,IReadOnlyList<PaperTrade>? trades=null,IReadOnlyList<QuantFundingEvent>? funding=null,DateTimeOffset? now=null,DateOnly? date=null,QuantDailyReport? report=null)
    {
        var at=now??state.UpdatedAt;var day=date??DateOnly.FromDateTime(at.ToOffset(TimeSpan.FromHours(8)).DateTime);
        report??=QuantDailyEngine.Build(day,at,state,trades??[],funding??[],[],[],[],new([],false),new Dictionary<Guid,IReadOnlyList<PaperHoldingSnapshot>>());
        return QuantReviewEngine.Build(day,at,state,trades??[],funding??[],report);
    }
    [Fact]public void OpenPositionsAndZeroSamplesKeepRatesAndExpectancyUnknown()
    {
        var (state,f)=QuantLiveTests.Open();var result=Review(state);Assert.Equal(1,result.Daily.OpenPositions);Assert.Equal(0,result.Daily.Metrics.Confirmed);
        Assert.Null(result.Daily.Metrics.WinRate);Assert.Null(result.Daily.Metrics.AverageNet);Assert.Null(result.Daily.Metrics.AverageR);Assert.Null(result.Daily.Metrics.ProfitFactor);
        Assert.Null(Assert.Single(result.Daily.Trades).NetPnl);Assert.Equal("Collecting",result.Readiness.Status);
    }
    [Theory][InlineData(false)][InlineData(true)]public void ConfirmedLossUsesOneWholePositionRiskAndActualCosts(bool shortSide)
    {
        var (state,t)=Closed(shortSide);var result=Review(state,[t,t]);var metrics=result.Daily.Metrics;var row=Assert.Single(result.Daily.Trades);
        Assert.Equal(1,metrics.Confirmed);Assert.Equal(1,metrics.Losses);Assert.Equal(0,metrics.WinRate);Assert.Equal(t.NetPnl,metrics.NetPnl);
        Assert.Equal(t.Fees+t.FundingCost,metrics.NetCost);Assert.Equal(t.GrossPnl-metrics.NetCost,metrics.NetPnl);Assert.Equal(t.NetPnl/t.Quant!.Plan.EstimatedRisk,row.NetR);
        Assert.Equal(Math.Max(0,-t.NetPnl-row.PlannedRisk),row.LossBeyondPlan);Assert.Equal(100*row.NetCost/Math.Abs(t.GrossPnl),row.CostToAbsoluteGrossPercent);
        foreach(var dimension in new[]{"Direction","Strategy","Environment"})Assert.Equal(metrics.NetPnl,result.Daily.Groups.Where(g=>g.Dimension==dimension).Sum(g=>g.Metrics.NetPnl));
    }
    [Fact]public void AFinalFlagWithoutFundingOrACompleteQuantityDoesNotEnterPerformanceMetrics()
    {
        var (state,t)=Closed();var result=Review(state with{QuantFunding=[]},[t]);Assert.Equal(1,result.Daily.AwaitFunding);Assert.Equal(0,result.Daily.Metrics.Confirmed);Assert.Null(result.Daily.Trades[0].NetPnl);
        t=t with{Quantity=t.Quantity/2};result=Review(state,[t]);Assert.Equal(1,result.Daily.Inconsistent);Assert.Equal(0,result.Daily.Metrics.Confirmed);Assert.Equal("CheckEvidence",result.Readiness.Status);
    }
    [Fact]public void PartiallyClosedOpenTradeIsNotAWholeTradeWinner()
    {
        var (state,f)=QuantLiveTests.Open();var p=state.Positions[0];var partial=QuantPaperEngine.Tick(state,[],[],QuantLiveTests.Input(f,state.UpdatedAt.AddSeconds(5),p.TakePrice+.1m));
        Assert.True(Assert.Single(partial.Trades).NetPnl>0);var review=Review(partial.State,partial.Trades);Assert.Equal(1,review.Daily.OpenPositions);Assert.Equal(0,review.Daily.Metrics.Confirmed);Assert.Null(review.Daily.Metrics.WinRate);
    }
    [Fact]public void LateCreditChangesTheWholeTradeOnItsExitDayWhileTheBookingDayRemainsSeparate()
    {
        var (state,t)=Closed();var day=DateOnly.FromDateTime(t.ClosedAt.ToOffset(TimeSpan.FromHours(8)).DateTime);var booked=QuantDailyEngine.Start(day.AddDays(1)).AddHours(1);
        var credit=-2m;var fund=new QuantFundingEvent("late",booked,0,credit,new(ExecutionFundingEngine.Version,t.PositionId!.Value,t.Symbol,t.Direction,t.OpenedAt.AddMinutes(20),"Regular",t.Quantity,-.001m,100,credit,booked));
        var review=Review(state,[t],[fund,fund],booked,day);Assert.Equal(1,review.Daily.Metrics.Confirmed);Assert.Equal(t.NetPnl-credit,review.Daily.Metrics.NetPnl);Assert.Equal(t.FundingCost+credit,review.Daily.Metrics.Funding);
        Assert.Equal(0,Review(state,[t],[fund],booked,day.AddDays(1)).Daily.Metrics.Confirmed);
    }
    [Fact]public void ShanghaiWeekStartsOnMondayAndFinalExitAtMidnightBelongsOnlyToTheNewDay()
    {
        var (state,t)=Closed();var monday=new DateOnly(2026,11,2);var at=QuantDailyEngine.Start(monday);
        t=t with{ClosedAt=at,ExitQuote=t.ExitQuote! with{Book=t.ExitQuote!.Book! with{TransactionAt=at.AddSeconds(-1),ReceivedAt=at.AddSeconds(-1)}}};state=state with{UpdatedAt=at,QuantFunding=[state.QuantFunding![0] with{KnownThrough=at,CheckedAt=at}]};
        var review=Review(state,[t],now:at.AddSeconds(1),date:monday);Assert.Equal(at,review.Week.From);Assert.Equal(1,review.Daily.Metrics.Confirmed);
        Assert.Equal(0,Review(state,[t],now:at.AddSeconds(1),date:monday.AddDays(-1)).Daily.Metrics.Confirmed);
        Assert.Equal(0,Review(state,[t],now:at.AddSeconds(1),date:monday.AddDays(-1)).Week.Metrics.Confirmed);
    }
    [Fact]public void CompleteLedgerStatisticsAreIndependentOfTheThirtyPathAndHundredRowDisplayLimits()
    {
        var (state,t)=Closed();var trades=Enumerable.Range(0,105).Select(_=>t with{Id=Guid.NewGuid(),PositionId=Guid.NewGuid()}).ToArray();
        state=state with{QuantFunding=trades.Select(r=>new QuantFundingCoverage(r.PositionId!.Value,r.Symbol,"ObservedWindow",r.ClosedAt,state.UpdatedAt,"fixture")).ToArray()};
        var day=DateOnly.FromDateTime(state.UpdatedAt.ToOffset(TimeSpan.FromHours(8)).DateTime);var daily=QuantDailyEngine.Build(day,state.UpdatedAt,state,trades,[],[],[],[],new([],false),new Dictionary<Guid,IReadOnlyList<PaperHoldingSnapshot>>());Assert.Equal(30,daily.Paths.Count);
        var review=Review(state,trades,report:daily);Assert.Equal(105,review.Daily.Metrics.Confirmed);Assert.Equal(100,review.Daily.Trades.Count);Assert.True(review.Daily.TradesLimited);Assert.Equal(105,review.Readiness.ConfirmedClosed);
    }
    [Fact]public void OldVersionsPreActivationAndFutureExitsAreExcludedWithoutChangingTheLedger()
    {
        var (state,t)=Closed();var hash=QuantRules.HashValue(new{state,t});var rows=new[]{t,t with{Id=Guid.NewGuid(),Version="Legacy"},t with{Id=Guid.NewGuid(),OpenedAt=state.QuantControl!.ActivatedAt.AddSeconds(-1)},t with{Id=Guid.NewGuid(),ClosedAt=state.UpdatedAt.AddSeconds(1)}};
        var result=Review(state,rows);Assert.Equal(1,result.Daily.Metrics.Confirmed);Assert.Equal(hash,QuantRules.HashValue(new{state,t}));
    }
    [Fact]public void NoLossesAndZeroGrossDoNotProduceInfinityAndCostRatioUsesSumOfAbsoluteGross()
    {
        var row=new QuantReviewTrade(Guid.NewGuid(),"TEST","Breakout20","Long","Uptrend",DateTimeOffset.UtcNow,null,false,"Complete",1,1,.1m,0,.1m,.9m,.9m,10,0,0,"fixture");
        Assert.Null(QuantReviewEngine.Metrics([row]).ProfitFactor);
        var loss=row with{GrossPnl=-1,NetPnl=-1.1m,NetR=-1.1m};var summary=QuantReviewEngine.Metrics([row,loss]);Assert.Equal(10,summary.CostToAbsoluteGrossPercent);Assert.Equal(.9m/1.1m,summary.ProfitFactor);
        var zero=row with{GrossPnl=0,NetPnl=-.1m};Assert.Null(QuantReviewEngine.Metrics([zero]).CostToAbsoluteGrossPercent);
    }
    [Fact]public void HistoricalExitHasOnlyItsKnownBookIntervalAndPercentilesDoNotInventObservationTimes()
    {
        var (state,t)=Closed();t=t with{Quant=t.Quant! with{ExitObservation=null,ExitComputedAt=null}};var result=Review(state,[t]);
        Assert.Equal(1,result.Quality.HistoricalTiming);Assert.Equal(0,result.Quality.RecordedTiming);Assert.Equal(0,result.Quality.Timings.Single(s=>s.Metric=="ReceiveToObservation").Samples);
        Assert.Equal(1,result.Quality.Timings.Single(s=>s.Metric=="BookReceiveToComputation").Samples);Assert.Null(result.Quality.Timings[0].P95Ms);
    }
    [Fact]public void MeetingMinimumDaysAndCountOnlyRequestsAReviewAndNeverDeclaresProfitability()
    {
        var (state,t)=Closed();var trades=Enumerable.Range(0,100).Select(_=>t with{Id=Guid.NewGuid(),PositionId=Guid.NewGuid()}).ToArray();
        state=state with{QuantFunding=trades.Select(r=>new QuantFundingCoverage(r.PositionId!.Value,r.Symbol,"ObservedWindow",r.ClosedAt,state.UpdatedAt,"fixture")).ToArray()};
        var now=state.QuantControl!.ActivatedAt.AddDays(31);var day=DateOnly.FromDateTime(t.ClosedAt.ToOffset(TimeSpan.FromHours(8)).DateTime);
        var daily=QuantDailyEngine.Build(day,now,state,trades,[],[],[],[],new([],false),new Dictionary<Guid,IReadOnlyList<PaperHoldingSnapshot>>()) with{AccountStale=false,Hours=[]};
        Assert.Equal("ReviewRequired",Review(state,trades,now:now,date:day,report:daily).Readiness.Status);
        Assert.Equal("Collecting",Review(state,trades.Take(99).ToArray(),now:now,date:day,report:daily).Readiness.Status);
    }
    [Fact]public void ReviewArchiveRoundTripRetainsDefinitionsAndExcludesMissingRiskFromRMetrics()
    {
        var (state,t)=Closed();t=t with{Quant=t.Quant! with{Plan=t.Quant.Plan with{EstimatedRisk=0}}};var review=Review(state,[t]);Assert.Equal(1,review.Daily.Incomplete);Assert.Equal(0,review.Daily.Metrics.Confirmed);
        Assert.Equal(QuantRules.HashValue(review),QuantRules.HashValue(JsonSerializer.Deserialize<QuantReview>(JsonSerializer.Serialize(review))!));
    }
}
