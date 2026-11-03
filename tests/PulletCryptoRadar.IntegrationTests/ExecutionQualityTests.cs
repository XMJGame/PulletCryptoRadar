using Microsoft.Extensions.Options;
using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;
namespace PulletCryptoRadar.IntegrationTests;

public sealed class ExecutionQualityTests
{
    private sealed class Clock(DateTimeOffset at):TimeProvider {public DateTimeOffset At=at;public override DateTimeOffset GetUtcNow()=>At;}
    private static readonly DateTimeOffset At=new(2026,11,1,11,5,0,TimeSpan.FromHours(8));
    [Fact]public void FailedSymbolRetainsItsPhaseCountsAndRetryAcrossRestartEvenOutsideTheRotationPool()
    {
        using var temp=new Temp();var options=Options.Create(new StorageOptions{Directory=temp.Root});var clock=new Clock(At);var tracker=new HourlyWarmupTracker(new HourlyWarmupRepository(options),clock);
        var plan=new HourlyWarmupPlan(true,67,1,1,"invalid received time");var attempt=tracker.Begin("APRUSDT",At.AddMinutes(-5),plan);
        clock.At=At.AddSeconds(2);tracker.Update(attempt,"Failed","PersistHistory",plan,67,66,"test write failure",At.AddMinutes(10));
        tracker=new(new HourlyWarmupRepository(options),clock);var row=Assert.Single(tracker.Status().Records);Assert.Equal("PersistHistory",row.Phase);Assert.Equal(67,row.FetchLimit);Assert.Equal(67,row.ReturnedRows);Assert.Equal(66,row.AcceptedRows);
        Assert.False(tracker.Due("APRUSDT",clock.At));Assert.Empty(tracker.RetrySymbols(clock.At));clock.At=At.AddMinutes(10);
        Assert.True(tracker.Due("APRUSDT",clock.At));Assert.Equal("APRUSDT",Assert.Single(tracker.RetrySymbols(clock.At)));
        var next=tracker.Begin("APRUSDT",At.AddMinutes(-5),plan);tracker.Update(next,"Ready","Complete",new(false,0,0,0,"passed"),67,67,"passed",At.AddMinutes(55));
        Assert.Empty(tracker.RetrySymbols(At.AddMinutes(56)));Assert.True(tracker.Due("APRUSDT",At.AddMinutes(56)));
    }
    [Fact]public void OldAttemptCannotOverwriteANewerAttemptAndHashedFilenamesDoNotUseTheSymbolAsAPath()
    {
        using var temp=new Temp();var clock=new Clock(At);var tracker=new HourlyWarmupTracker(new HourlyWarmupRepository(Options.Create(new StorageOptions{Directory=temp.Root})),clock);
        var plan=new HourlyWarmupPlan(false,0,0,0,"passed");var old=tracker.Begin("../TOKEN",At.AddMinutes(-5),plan);clock.At=At.AddSeconds(1);tracker.Begin("../TOKEN",At.AddMinutes(-5),plan);
        Assert.Throws<InvalidOperationException>(()=>tracker.Update(old,"Ready","Complete",plan,0,0,"old",At.AddHours(1)));Assert.Single(Directory.GetFiles(Path.Combine(temp.Root,"HourlyWarmup"),"*.json"));
    }
    [Theory][InlineData(false)][InlineData(true)]public void ExitRecordsTheActualTriggerAndLocalObservationWithoutInventingANetworkLatency(bool shortSide)
    {
        var (state,f)=QuantLiveTests.Open(shortSide);var p=state.Positions[0];var at=state.UpdatedAt.AddSeconds(5);var input=QuantLiveTests.Input(f,at,shortSide?p.StopPrice+.1m:p.StopPrice-.1m);
        var batch=QuantPaperEngine.Tick(state,[],[],input);var trade=Assert.Single(batch.Trades);var observation=trade.Quant!.ExitObservation!;
        Assert.Equal("Stop",observation.Reason);Assert.Equal(at,observation.ObservedAt);Assert.Equal(input.Markets[0].PriceReceivedTime,observation.ReceivedAt);Assert.Equal(p.StopPrice,observation.StopAtObservation);Assert.Equal(p.LastQuoteTime,observation.PreviousQuoteSourceAt);
        var timing=QuantExitTimingEngine.Build(trade);Assert.Equal("Recorded",timing.Status);Assert.Equal(0,timing.ReceiveToObservationMs);Assert.Equal(0,timing.ObservationToComputationMs);
        Assert.Equal(0,timing.BookReceiveToComputationMs);Assert.True(timing.BookCachedAtObservation);
    }
    [Fact]public void WaitingExitPreservesFirstObservationAndAPartialFillDoesNotResetItsClock()
    {
        var (state,f)=QuantLiveTests.Open();var p=state.Positions[0];var at=state.UpdatedAt.AddSeconds(5);var input=QuantLiveTests.Input(f,at,p.StopPrice-.1m);
        var wait=QuantPaperEngine.Tick(state,[],[],input with{Execution=new Dictionary<string,ExecutionPlanningInputs>{{p.Symbol,input.Execution[p.Symbol] with{Book=null}}}});
        var first=wait.State.Positions[0].Quant!.ExitObservation!;var partial=QuantPaperEngine.Tick(wait.State,[],[],QuantLiveTests.Input(f,at.AddSeconds(3),p.EntryPrice,p.Quantity/2));
        var leg=Assert.Single(partial.Trades);Assert.Equal(first,leg.Quant!.ExitObservation);Assert.Equal(3000,QuantExitTimingEngine.Build(leg).ObservationToComputationMs);
        Assert.Equal(first,partial.State.Positions[0].Quant!.ExitObservation);var final=QuantPaperEngine.Tick(partial.State,partial.Trades,[],QuantLiveTests.Input(f,at.AddSeconds(8),p.EntryPrice));
        Assert.Equal(8000,QuantExitTimingEngine.Build(Assert.Single(final.Trades)).ObservationToComputationMs);
    }
    [Fact]public void CompletedTargetClearsItsObservationAndALaterStopHasANewObservation()
    {
        var (state,f)=QuantLiveTests.Open();var p=state.Positions[0];var at=state.UpdatedAt.AddSeconds(5);
        var target=QuantPaperEngine.Tick(state,[],[],QuantLiveTests.Input(f,at,p.TakePrice+.1m));Assert.Equal("Target1",Assert.Single(target.Trades).Quant!.ExitObservation!.Reason);Assert.Null(target.State.Positions[0].Quant!.ExitObservation);
        var stop=QuantPaperEngine.Tick(target.State,target.Trades,[],QuantLiveTests.Input(f,at.AddSeconds(3),p.StopPrice-.1m));Assert.Equal("Stop",Assert.Single(stop.Trades).Quant!.ExitObservation!.Reason);Assert.Equal(at.AddSeconds(3),stop.Trades[0].Quant!.ExitObservation!.ObservedAt);
    }
    [Fact]public void OlderExitHasOnlyPartialTimingAndFutureLocalTimesNeverCreateNegativeLatency()
    {
        var (state,f)=QuantLiveTests.Open();var at=state.UpdatedAt.AddSeconds(5);var trade=Assert.Single(QuantPaperEngine.Tick(state,[],[],QuantLiveTests.Input(f,at,1)).Trades);
        var old=trade with{Quant=trade.Quant! with{ExitObservation=null}};var timing=QuantExitTimingEngine.Build(old);Assert.Equal("HistoricalPartial",timing.Status);Assert.Null(timing.ReceiveToObservationMs);Assert.Null(timing.ObservationToComputationMs);
        var invalid=trade with{Quant=trade.Quant! with{ExitObservation=trade.Quant.ExitObservation! with{ReceivedAt=at.AddSeconds(1)}}};timing=QuantExitTimingEngine.Build(invalid);Assert.Equal("Invalid",timing.Status);Assert.Null(timing.ReceiveToObservationMs);
    }
    [Fact]public void ProductionClockMeasuresObservationAndComputationInsteadOfReusingTheMarketSnapshotTimestamp()
    {
        var (state,f)=QuantLiveTests.Open();var at=state.UpdatedAt.AddSeconds(5);var calls=0;
        var result=QuantPaperEngine.Tick(state,[],[],QuantLiveTests.Input(f,at,1),()=>at.AddSeconds(++calls==1?2:3));
        var trade=Assert.Single(result.Trades);var timing=QuantExitTimingEngine.Build(trade);Assert.Equal(at,trade.ClosedAt);Assert.Equal(at.AddSeconds(3),timing.ComputedAt);
        Assert.Equal(2000,timing.ReceiveToObservationMs);Assert.Equal(1000,timing.ObservationToComputationMs);Assert.Equal(3000,timing.BookReceiveToComputationMs);
    }
    private sealed class Temp:IDisposable
    {
        public string Root {get;}=Path.Combine(Path.GetTempPath(),"Pullet-Execution-Quality-"+Guid.NewGuid().ToString("N"));
        public void Dispose(){var full=Path.GetFullPath(Root);if(!full.StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException();if(Directory.Exists(full))Directory.Delete(full,true);}
    }
}
