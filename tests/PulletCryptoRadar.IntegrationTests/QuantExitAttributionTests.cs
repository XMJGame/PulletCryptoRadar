using System.Text.Json;
using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;
namespace PulletCryptoRadar.IntegrationTests;

public sealed class QuantExitAttributionTests
{
    private static void Close(decimal expected,decimal? actual)=>Assert.True(actual is {} value&&Math.Abs(expected-value)<.000000000001m,$"Expected {expected}; actual {actual}");
    private static QuantLifecycle Review(PaperState state,IReadOnlyList<PaperTrade> trades,IReadOnlyList<QuantFundingEvent>? funding=null,bool cover=true)
    {
        var id=state.Positions.FirstOrDefault()?.Id??trades[0].PositionId!.Value;
        var coverage=cover?new QuantFundingCoverage(id,trades.FirstOrDefault()?.Symbol??state.Positions[0].Symbol,"ObservedWindow",state.UpdatedAt,state.UpdatedAt,"Fixture"):null;
        return QuantLifecycleEngine.Build(id,state.Positions.FirstOrDefault(p=>p.Id==id),trades,funding??[],coverage,state.UpdatedAt);
    }
    private static (PaperState State,PaperTrade Trade) Stop(bool shortSide=false)
    {
        var (s,f)=QuantLiveTests.Open(shortSide);var p=s.Positions[0];var batch=QuantPaperEngine.Tick(s,[],[],QuantLiveTests.Input(f,s.UpdatedAt.AddSeconds(5),p.StopPrice+(shortSide?.1m:-.1m)));
        return(batch.State,Assert.Single(batch.Trades));
    }
    private static (PaperState State,PaperTrade[] Trades) ThreeLegs(bool shortSide=false)
    {
        var (s,f)=QuantLiveTests.Open(shortSide);var p=s.Positions[0];var at=s.UpdatedAt.AddSeconds(5);var trades=new List<PaperTrade>();
        foreach(var price in new[]{p.TakePrice+(shortSide?-.1m:.1m),p.Target2+(shortSide?-.1m:.1m)})
        {var next=QuantPaperEngine.Tick(s,trades,[],QuantLiveTests.Input(f,at,price));trades.AddRange(next.Trades);s=next.State;at=at.AddSeconds(3);}
        // Isolated attribution fixture: a previously tightened trailing stop is now crossed.
        s=s with{Positions=[s.Positions[0] with{StopPrice=(p.EntryPrice+p.TakePrice)/2}]};
        var final=QuantPaperEngine.Tick(s,trades,[],QuantLiveTests.Input(f,at,p.EntryPrice));trades.AddRange(final.Trades);s=final.State;
        Assert.Equal(3,trades.Count);return(s,trades.ToArray());
    }
    [Theory][InlineData(false)][InlineData(true)]public void OriginalBudgetAndCompleteLongShortStopDifferencesConserve(bool shortSide)
    {
        var (s,t)=Stop(shortSide);var result=Review(s,[t]);Assert.Equal("Complete",result.Status);var a=result.Attribution!;
        Assert.Equal("Reconciled",a.Status);Assert.Equal("Verified",a.Budget.Status);Close(t.Quant!.Plan.EstimatedRisk,a.Budget.Sum);Close(0,a.Budget.Difference);
        Close(-t.NetPnl-t.Quant.Plan.EstimatedRisk,a.SignedRiskDifference);Close(a.SignedRiskDifference!.Value,a.BridgeSum);Close(0,a.BridgeDifference);
        var leg=Assert.Single(a.Legs);Assert.Equal("Recorded",leg.ObservationStatus);Assert.Equal("Verified",leg.BookStatus);Assert.True(leg.HalfSpread>0&&leg.Residual>0);
        Close(leg.OriginalStopToMid!.Value,leg.ReferenceShift+leg.ReferenceToObserved+leg.ObservedToMid);Close(0,leg.BridgeDifference);
        Close(0,a.Parts.Single(p=>p.Key=="EntryFeeDifference").Value);
    }
    [Theory][InlineData(false)][InlineData(true)]public void SavedDepthAndHalfSpreadAreSeparatedFromConfiguredResidual(bool shortSide)
    {
        var (s,f)=QuantLiveTests.Open(shortSide);var p=s.Positions[0];var input=QuantLiveTests.Input(f,s.UpdatedAt.AddSeconds(5),p.StopPrice+(shortSide?.1m:-.1m));
        var evidence=input.Execution[p.Symbol];var book=evidence.Book!;var best=shortSide?book.Asks[0].Price:book.Bids[0].Price;
        var levels=new BookLevel[]{new(best,p.Quantity/2),new(best+(shortSide?.02m:-.02m),p.Quantity)};
        var modifiedBook=shortSide?book with{Asks=levels}:book with{Bids=levels};
        input=input with{Execution=new Dictionary<string,ExecutionPlanningInputs>{{p.Symbol,evidence with{Book=modifiedBook}}}};
        var batch=QuantPaperEngine.Tick(s,[],[],input);var trade=Assert.Single(batch.Trades);var a=Review(batch.State,[trade]).Attribution!;var leg=Assert.Single(a.Legs);
        Assert.Equal("Verified",leg.BookStatus);Close(p.Quantity*.01m,leg.Depth);Close(p.Quantity*(book.Asks[0].Price-book.Bids[0].Price)/2,leg.HalfSpread);
        Close(trade.Quantity*leg.Vwap!.Value*trade.Quant!.Costs.ResidualSlippageRate,leg.Residual);Close(0,a.BridgeDifference);
    }
    [Fact]public void HistoricalStopDoesNotInventActualStopTriggerPriceOrLatencyLoss()
    {
        var (s,t)=Stop();t=t with{Quant=t.Quant! with{ExitObservation=null,ExitComputedAt=null}};var a=Review(s,[t]).Attribution!;
        Assert.Equal("Reconciled",a.Status);Assert.Equal(1,a.MissingObservations);var leg=Assert.Single(a.Legs);
        Assert.Null(leg.Reference);Assert.Null(leg.ObservedPrice);Assert.Null(leg.ReferenceShift);Assert.Null(leg.ReferenceToObserved);Assert.Null(leg.ObservedToMid);
        Assert.NotNull(leg.OriginalStopToMid);Close(0,a.BridgeDifference);
    }
    [Fact]public void InvalidObservationCannotSupplyTriggerDecompositionEvenWhenCostBridgeIsKnown()
    {
        var (s,t)=Stop();t=t with{Quant=t.Quant! with{ExitObservation=t.Quant.ExitObservation! with{ReceivedAt=s.UpdatedAt.AddSeconds(1)}}};var a=Review(s,[t]).Attribution!;
        Assert.Equal(1,a.InvalidObservations);Assert.Null(a.Legs[0].ObservedPrice);Assert.Null(a.Legs[0].Reference);Assert.NotNull(a.Legs[0].OriginalStopToMid);Close(0,a.BridgeDifference);
    }
    [Fact]public void MissingVwapUsesOnlyAggregateLedgerPriceDifference()
    {
        var (s,t)=Stop();t=t with{ExitQuote=t.ExitQuote! with{Vwap=null}};var a=Review(s,[t]).Attribution!;
        Assert.Equal("Reconciled",a.Status);Assert.Equal("LedgerAggregate",a.BridgeBasis);Assert.Equal("Missing",a.Legs[0].BookStatus);
        Assert.Null(a.Legs[0].HalfSpread);Assert.Null(a.Legs[0].Depth);Assert.Null(a.Legs[0].Residual);Assert.Contains(a.Parts,p=>p.Key=="StopToFill");Close(0,a.BridgeDifference);
    }
    [Theory][InlineData("crossed")][InlineData("depth")][InlineData("vwap")][InlineData("side")]public void ContradictoryBookIsNotUsedToManufactureDetailedComponents(string problem)
    {
        var (s,t)=Stop();var quote=t.ExitQuote!;
        quote=problem switch{
            "crossed"=>quote with{Book=quote.Book! with{Bids=[new(quote.Book.Asks[0].Price+1,10000)]}},
            "depth"=>quote with{Book=quote.Book! with{Bids=[new(quote.Book.Bids[0].Price,t.Quantity/2)]}},
            "vwap"=>quote with{Vwap=quote.Vwap+.01m},
            _=>quote with{Side="Buy"}};
        var a=Review(s,[t with{ExitQuote=quote}]).Attribution!;Assert.Equal("Inconsistent",a.Status);Assert.Equal("Inconsistent",a.Legs[0].BookStatus);Assert.Null(a.Legs[0].HalfSpread);
    }
    [Fact]public void PlanMismatchAndUnsupportedVersionsDoNotBorrowCurrentParameters()
    {
        var (s,t)=Stop();var changed=t with{Quant=t.Quant! with{Plan=t.Quant.Plan with{EstimatedRisk=t.Quant.Plan.EstimatedRisk+1}}};var a=Review(s,[changed]).Attribution!;
        Assert.Equal("Inconsistent",a.Status);Assert.Equal("Inconsistent",a.Budget.Status);Assert.Null(a.SignedRiskDifference);Assert.Null(a.BridgeSum);
        var unknown=t with{Quant=t.Quant! with{Costs=t.Quant.Costs with{Version="Future-Cost"}},ExitQuote=t.ExitQuote! with{Version="Future-Cost"}};a=Review(s,[unknown]).Attribution!;Assert.Equal("Unsupported",a.Budget.Status);Assert.Null(a.Budget.Sum);Assert.Null(a.BridgeSum);Assert.Null(a.Legs[0].HalfSpread);
    }
    [Fact]public void OpenPositionAndPartialProfitNeverBecomeFinalRiskDifference()
    {
        var (s,f)=QuantLiveTests.Open();var a=Review(s,[]).Attribution!;Assert.Equal("NoExitSamples",a.Status);Assert.Null(a.FinalNet);Assert.Null(a.SignedRiskDifference);
        var p=s.Positions[0];var partial=QuantPaperEngine.Tick(s,[],[],QuantLiveTests.Input(f,s.UpdatedAt.AddSeconds(5),p.TakePrice+.1m));a=Review(partial.State,partial.Trades).Attribution!;
        Assert.Equal("AwaitCompletion",a.Status);Assert.Null(a.BridgeSum);Assert.Null(a.LossBeyondPlan);Assert.Equal("OriginalTarget1",a.Legs[0].ReferenceBasis);Assert.True(a.Legs[0].ReferenceShift<0);
    }
    [Fact]public void AwaitFundingAndMissingExitQuoteKeepWholeTradeUnconfirmed()
    {
        var (s,t)=Stop();var a=Review(s,[t],cover:false).Attribution!;Assert.Equal("AwaitFunding",a.Status);Assert.Null(a.FinalNet);Assert.Null(a.BridgeSum);
        a=Review(s,[t with{ExitQuote=null}]).Attribution!;Assert.Equal("Incomplete",a.Status);Assert.Null(a.BridgeSum);Assert.Null(a.FinalNet);
    }
    [Theory][InlineData(false)][InlineData(true)]public void TargetAndTailPoliciesRemainSignedDifferencesRatherThanPureSlippage(bool shortSide)
    {
        var (s,trades)=ThreeLegs(shortSide);var a=Review(s,trades).Attribution!;Assert.Equal("Reconciled",a.Status);Assert.Equal(3,a.Legs.Count);
        Assert.True(a.FinalNet>0&&a.SignedRiskDifference<0);Assert.Equal(0,a.LossBeyondPlan);Close(0,a.BridgeDifference);Assert.Contains(a.Parts,p=>p.Value<0);
        Assert.Equal("OriginalTarget1",a.Legs[0].ReferenceBasis);Assert.Equal("OriginalTarget2",a.Legs[1].ReferenceBasis);Assert.Equal("RecordedStop",a.Legs[2].ReferenceBasis);
        Assert.True(a.Legs[2].ReferenceShift<0);Close(a.Legs[2].OriginalStopToMid!.Value,a.Legs[2].ReferenceShift+a.Legs[2].ReferenceToObserved+a.Legs[2].ObservedToMid);
    }
    [Theory][InlineData(.4)][InlineData(-.4)]public void LateFundingChargeOrCreditChangesTheBridgeOnceWithoutRewritingTrades(decimal amount)
    {
        var (s,t)=Stop();var at=s.UpdatedAt.AddMinutes(12);s=s with{UpdatedAt=at};var original=QuantRules.HashValue(t);
        var payment=new QuantFundingEvent("late",at,0,amount,new(ExecutionFundingEngine.Version,t.PositionId!.Value,t.Symbol,t.Direction,t.OpenedAt.AddSeconds(1),"Regular",t.Quantity,amount/100,100,amount,at));
        var before=Review(s,[t]).Attribution!;var after=Review(s,[t],[payment,payment]).Attribution!;
        Assert.Equal("Reconciled",after.Status);Close(before.FinalNet!.Value-amount,after.FinalNet);Close(before.SignedRiskDifference!.Value+amount,after.SignedRiskDifference);Close(0,after.BridgeDifference);Assert.Equal(original,QuantRules.HashValue(t));
    }
    [Fact]public void DuplicateLegIsCountedOnceAndConflictingDuplicateNeverConfirmsRiskDifference()
    {
        var (s,t)=Stop();Assert.Single(Review(s,[t,t]).Attribution!.Legs);
        var a=Review(s,[t,t with{NetPnl=t.NetPnl+1}]).Attribution!;Assert.Equal("Inconsistent",a.Status);Assert.Null(a.SignedRiskDifference);Assert.Null(a.BridgeSum);
    }
    [Theory][InlineData("TimeLimit")][InlineData("LiquidationRisk")]public void TimeAndRiskExitsUseRecordedReferencesWithoutPretendingTheyAreExchangeFillPrices(string reason)
    {
        var (s,f)=QuantLiveTests.Open();var p=s.Positions[0];var at=reason=="TimeLimit"?p.OpenedAt.AddHours(73):s.UpdatedAt.AddSeconds(5);
        var input=QuantLiveTests.Input(f,at,reason=="TimeLimit"?p.EntryPrice:.1m);var batch=QuantPaperEngine.Tick(s,[],[],input);var t=Assert.Single(batch.Trades);
        Assert.Equal(reason,t.Quant!.PendingExit);var a=Review(batch.State,[t]).Attribution!;Assert.Equal("Reconciled",a.Status);Assert.Equal(reason=="TimeLimit"?"RecordedTimePrice":"RecordedRiskMark",a.Legs[0].ReferenceBasis);Close(0,a.BridgeDifference);
    }
    [Fact]public void WaitingAndLimitedDepthExitKeepOneLockedObservationAcrossLegs()
    {
        var (s,f)=QuantLiveTests.Open();var p=s.Positions[0];var at=s.UpdatedAt.AddSeconds(5);var input=QuantLiveTests.Input(f,at,p.StopPrice-.1m);
        var wait=QuantPaperEngine.Tick(s,[],[],input with{Execution=new Dictionary<string,ExecutionPlanningInputs>{{p.Symbol,input.Execution[p.Symbol] with{Book=null}}}});
        var partial=QuantPaperEngine.Tick(wait.State,[],[],QuantLiveTests.Input(f,at.AddSeconds(3),p.EntryPrice,p.Quantity/2));var first=Assert.Single(partial.Trades);
        var final=QuantPaperEngine.Tick(partial.State,partial.Trades,[],QuantLiveTests.Input(f,at.AddSeconds(8),p.EntryPrice));var second=Assert.Single(final.Trades);
        Assert.Equal(first.Quant!.ExitObservation,second.Quant!.ExitObservation);var a=Review(final.State,[first,second]).Attribution!;
        Assert.Equal("Reconciled",a.Status);Assert.Equal(2,a.RecordedObservations);Close(0,a.BridgeDifference);
    }
    [Fact]public void SerializationAndDailyReviewExposeSameAttributionWithoutChangingSource()
    {
        var (s,t)=Stop();var id=t.PositionId!.Value;s=s with{QuantFunding=[new(id,t.Symbol,"ObservedWindow",s.UpdatedAt,s.UpdatedAt,"Fixture")]};
        var original=QuantRules.HashValue(new{s,t});var date=PaperEngine.Day(s.UpdatedAt);
        var daily=QuantDailyEngine.Build(date,s.UpdatedAt,s,[t],[],[],[],[],new([],false),new Dictionary<Guid,IReadOnlyList<PaperHoldingSnapshot>>());
        var review=QuantReviewEngine.Build(date,s.UpdatedAt,s,[t],[],daily);var a=Assert.Single(review.Daily.Trades).Attribution!;
        Assert.Equal(QuantRules.HashValue(daily.Paths[0].Lifecycle!.Attribution),QuantRules.HashValue(a));Assert.Equal(QuantRules.HashValue(a),QuantRules.HashValue(JsonSerializer.Deserialize<QuantExitAttribution>(JsonSerializer.Serialize(a))!));
        Assert.Equal(original,QuantRules.HashValue(new{s,t}));
    }
}
