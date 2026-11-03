using System.Text.Json;
using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;
namespace PulletCryptoRadar.IntegrationTests;

public sealed class QuantLifecycleTests
{
    private static QuantLifecycle Review(PaperState state,IReadOnlyList<PaperTrade> trades,IReadOnlyList<QuantFundingEvent>? funding=null,DateTimeOffset? at=null)
    {
        var id=state.Positions.FirstOrDefault(p=>p.Quant is not null)?.Id??trades[0].PositionId!.Value;
        return QuantLifecycleEngine.Build(id,state.Positions.FirstOrDefault(p=>p.Id==id),trades,funding??[],(state.QuantFunding??[]).FirstOrDefault(f=>f.PositionId==id),at??state.UpdatedAt);
    }
    private static (PaperState State,PaperTrade[] Trades,QuantRehearsalInput Fixture) Closed(bool shortSide=false)
    {
        var (state,f)=QuantLiveTests.Open(shortSide);var p=state.Positions[0];var history=new List<PaperTrade>();var at=state.UpdatedAt.AddSeconds(5);
        foreach(var value in new[]{p.TakePrice+(shortSide?-.1m:.1m),p.Target2+(shortSide?-.1m:.1m)})
        {
            var batch=QuantPaperEngine.Tick(state,history,[],QuantLiveTests.Input(f,at,value));Assert.Single(batch.Trades);history.AddRange(batch.Trades);state=batch.State;at=at.AddSeconds(3);
        }
        var last=QuantPaperEngine.Tick(state,history,[],QuantLiveTests.Input(f,p.OpenedAt.AddHours(72).AddSeconds(2),p.EntryPrice));Assert.Single(last.Trades);history.AddRange(last.Trades);
        return(last.State,history.ToArray(),f);
    }
    private static PaperState Covered(PaperState state,PaperTrade[] trades)=>state with{QuantFunding=[new(trades[0].PositionId!.Value,trades[0].Symbol,"ObservedWindow",trades[^1].ClosedAt,state.UpdatedAt,"fixture only")]};
    [Fact] public void LivePositionIsNotReportedAsACompletedTradeEvenWhenEveryCurrentCheckPasses()
    {
        var (state,f)=QuantLiveTests.Open();var before=QuantRules.HashValue(state);var result=Review(state,[]);
        Assert.Equal("Open",result.Status);Assert.Null(result.FinalNet);Assert.Equal(state.Positions[0].Quantity,result.RemainingQuantity);Assert.Equal(0,result.ExitedQuantity);
        Assert.DoesNotContain(result.Checks,c=>c.Status=="Fail");Assert.Contains(result.Checks,c=>c.Key=="SubsequentBooks"&&c.Status=="NotApplicable");Assert.Equal(before,QuantRules.HashValue(state));
    }
    [Theory][InlineData(false)][InlineData(true)] public void TwoPartialTargetsAndFinalExitReconcileActualQuantityMarginFeesAndBooks(bool shortSide)
    {
        var (state,trades,f)=Closed(shortSide);state=Covered(state,trades);var result=Review(state,trades);
        Assert.Equal("Complete",result.Status);Assert.Equal(3,result.Legs.Count);Assert.Equal(result.InitialQuantity,result.ExitedQuantity);Assert.Equal(0,result.RemainingQuantity);
        Assert.Equal(trades.Sum(t=>t.NetPnl),result.FinalNet);Assert.All(result.Checks,c=>Assert.Equal("Pass",c.Status));Assert.All(result.Legs,l=>Assert.True(l.BookAt>l.TriggerAfter&&l.BookReceivedAt>l.TriggerAfter));
    }
    [Fact] public void FinalExitWithPendingOrShortCoverageKeepsFinalNetUnknown()
    {
        var (state,trades,f)=Closed();var result=Review(state,trades);Assert.Equal("AwaitFunding",result.Status);Assert.Null(result.FinalNet);Assert.NotNull(result.ClosedAt);
        state=Covered(state,trades) with{QuantFunding=[Covered(state,trades).QuantFunding![0] with{KnownThrough=trades[^1].ClosedAt.AddSeconds(-1)}]};
        Assert.Equal("AwaitFunding",Review(state,trades).Status);
    }
    [Theory][InlineData(.4)][InlineData(-.4)] public void LateFundingChargeOrCreditAdjustsFinalNetOnceAfterFullClose(decimal cost)
    {
        var (state,trades,f)=Closed();var id=trades[0].PositionId!.Value;var at=state.UpdatedAt.AddMinutes(12);var fundingAt=trades[0].OpenedAt.AddMinutes(30);
        var payment=new QuantFundingEvent("fund",at,0,cost,new(ExecutionFundingEngine.Version,id,trades[0].Symbol,trades[0].Direction,fundingAt,"Regular",trades[0].Quant!.Plan.Quantity,cost/100,100,cost,at));
        state=Covered(state,trades) with{UpdatedAt=at};var before=QuantRules.HashValue(trades);var result=Review(state,trades,[payment,payment]);
        Assert.Equal("Complete",result.Status);Assert.Equal(trades.Sum(t=>t.NetPnl)-cost,result.FinalNet);Assert.Equal(cost,result.LateFundingAdjustment);Assert.Equal(before,QuantRules.HashValue(trades));
    }
    [Fact] public void OpenFundingAndCollateralAreNotMistakenForRealizedPnl()
    {
        var (state,f)=QuantLiveTests.Open();var p=state.Positions[0];var at=p.OpenedAt.AddMinutes(12);var fundingAt=p.OpenedAt.AddSeconds(30);
        var window=new FundingRateWindow(p.Symbol,p.OpenedAt,at.AddMinutes(-2),at,true,[new(p.Symbol,fundingAt,.001m,100,"Regular",at)]);
        var batch=QuantPaperEngine.Tick(state,[],[],QuantLiveTests.Input(f,at) with{Funding=new Dictionary<string,FundingRateWindow?>{{p.Symbol,window}}});
        var result=Review(batch.State,[],batch.FundingEvents);Assert.Equal("Open",result.Status);Assert.Equal(0,result.RealizedNet);Assert.True(result.OpenFunding>0);
        Assert.DoesNotContain(result.Checks,c=>c.Status=="Fail");Assert.Null(result.FinalNet);
    }
    [Fact] public void MissingPartialLegCannotBeHiddenByAFinalExitMarker()
    {
        var (state,trades,f)=Closed();state=Covered(state,trades);var result=Review(state,[trades[0],trades[2]]);
        Assert.Equal("Inconsistent",result.Status);Assert.Contains(result.Checks,c=>c.Key=="Quantity"&&c.Status=="Fail");Assert.Null(result.FinalNet);
    }
    [Fact] public void FundingDiscoveredAfterAPartialExitSplitsOpenAndClosedAmountsWithoutDoubleCounting()
    {
        var (state,f)=QuantLiveTests.Open();var p=state.Positions[0];var at=state.UpdatedAt.AddSeconds(5);
        var partial=QuantPaperEngine.Tick(state,[],[],QuantLiveTests.Input(f,at,p.TakePrice+.1m));var trades=partial.Trades.ToList();Assert.Single(trades);
        var fundingAt=p.OpenedAt.AddSeconds(1);at=p.OpenedAt.AddMinutes(12);
        var window=new FundingRateWindow(p.Symbol,p.OpenedAt,at.AddMinutes(-2),at,true,[new(p.Symbol,fundingAt,.001m,100,"Regular",at)]);
        var funded=QuantPaperEngine.Tick(partial.State,trades,[],QuantLiveTests.Input(f,at) with{Funding=new Dictionary<string,FundingRateWindow?>{{p.Symbol,window}}});
        var result=Review(funded.State,trades,funded.FundingEvents);Assert.Equal("Open",result.Status);Assert.True(result.OpenFunding>0&&result.LateFundingAdjustment>0);
        Assert.DoesNotContain(result.Checks,c=>c.Status=="Fail");Assert.Equal(-result.LateFundingAdjustment+trades.Sum(t=>t.NetPnl),result.RealizedNet);
        at=p.OpenedAt.AddHours(72).AddSeconds(2);var final=QuantPaperEngine.Tick(funded.State,trades,funded.FundingEvents,QuantLiveTests.Input(f,at,p.EntryPrice));trades.AddRange(final.Trades);
        var closed=Covered(final.State,trades.ToArray());result=Review(closed,trades,funded.FundingEvents);Assert.Equal("Complete",result.Status);
        Assert.Equal(0,result.OpenFunding);Assert.Equal(result.ObservedFunding,result.SettledFunding+result.LateFundingAdjustment);
        Assert.Equal("Inconsistent",Review(closed,trades,[]).Status);
    }
    [Fact] public void CompensatingLegNetErrorsAreDetectedEvenWhenTheTotalStillMatches()
    {
        var (state,trades,f)=Closed();state=Covered(state,trades);trades[0]=trades[0] with{NetPnl=trades[0].NetPnl+1};trades[1]=trades[1] with{NetPnl=trades[1].NetPnl-1};
        var result=Review(state,trades);Assert.Equal("Inconsistent",result.Status);Assert.Contains(result.Checks,c=>c.Key=="Net"&&c.Status=="Pass");Assert.Contains(result.Checks,c=>c.Key=="LegNet"&&c.Status=="Fail");
    }
    [Fact] public void MissingHistoricalExitQuoteIsUnknownRatherThanRebuiltAtCurrentRates()
    {
        var (state,trades,f)=Closed();state=Covered(state,trades);trades[0]=trades[0] with{ExitQuote=null};var result=Review(state,trades);
        Assert.Equal("Incomplete",result.Status);Assert.Null(result.ExitFees);Assert.Null(result.FinalNet);Assert.Contains(result.Checks,c=>c.Key=="EntryFees"&&c.Status=="Unknown");
    }
    [Fact] public void SameTriggerBookAndDuplicateConflictingRecordCannotPass()
    {
        var (state,trades,f)=Closed();state=Covered(state,trades);var t=trades[0];var quote=t.ExitQuote! with{Book=t.ExitQuote!.Book! with{TransactionAt=t.Quant!.ExitAfter!.Value}};
        var result=Review(state,[t with{ExitQuote=quote},trades[1],trades[2]]);Assert.Equal("Inconsistent",result.Status);Assert.Contains(result.Checks,c=>c.Key=="SubsequentBooks"&&c.Status=="Fail");
        result=Review(state,trades.Append(t with{NetPnl=t.NetPnl+1}).ToArray());Assert.Contains(result.Checks,c=>c.Key=="UniqueLedger"&&c.Status=="Fail");
    }
    [Fact] public void ConflictingFundingPreventsConfirmationAndFutureRecordsAreNotCounted()
    {
        var (state,trades,f)=Closed();state=Covered(state,trades);var coverage=state.QuantFunding![0];state=state with{QuantFunding=[coverage with{Status="Conflict"}]};
        Assert.Equal("Inconsistent",Review(state,trades).Status);
        state=state with{QuantFunding=[coverage]};var now=state.UpdatedAt;var payment=new QuantFundingEvent("future",now.AddSeconds(1),0,1,new("fixture",coverage.PositionId,coverage.Symbol,"Long",now,"Regular",1,.01m,100,1,now));
        Assert.Equal("Complete",Review(state,trades,[payment]).Status);Assert.Equal(0,Review(state,trades,[payment]).ObservedFunding);
    }
    [Fact] public void SerializationAndDailyProjectionPreserveTheSourceLedger()
    {
        var (state,trades,f)=Closed();state=Covered(state,trades);var hash=QuantRules.HashValue(new{state,trades});var date=DateOnly.FromDateTime(trades[^1].ClosedAt.ToOffset(TimeSpan.FromHours(8)).DateTime);
        var report=QuantDailyEngine.Build(date,state.UpdatedAt,state,trades,[],[],[],[],new([],false),new Dictionary<Guid,IReadOnlyList<PaperHoldingSnapshot>>());
        var result=Assert.Single(report.Paths).Lifecycle!;Assert.Equal("Complete",result.Status);Assert.Equal(QuantRules.HashValue(result),QuantRules.HashValue(JsonSerializer.Deserialize<QuantLifecycle>(JsonSerializer.Serialize(result))!));
        Assert.Equal(hash,QuantRules.HashValue(new{state,trades}));
    }
}
