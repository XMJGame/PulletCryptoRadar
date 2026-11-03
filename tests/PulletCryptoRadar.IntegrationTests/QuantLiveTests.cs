using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;
namespace PulletCryptoRadar.IntegrationTests;
public sealed class QuantLiveTests
{
    private static readonly ExecutionCostModel Costs=new();
    internal static QuantPaperInput Input(QuantRehearsalInput f,DateTimeOffset at,decimal? price=null,decimal depth=10000)
    {
        var market=f.Markets[0];var value=price??market.Price!.Value;
        market=market with{Price=value,MarkPrice=value,PriceSourceTime=at.AddSeconds(-1),PriceReceivedTime=at,FundingSourceTime=at.AddSeconds(-1),MarkReceivedTime=at};
        var book=f.Execution["TESTUSDT"].Book! with{UpdateId=at.ToUnixTimeMilliseconds(),TransactionAt=at.AddMilliseconds(-20),OutputAt=at.AddMilliseconds(-10),ReceivedAt=at,Bids=[new(ExecutionCostEngine.Floor(value-.01m,.01m),depth)],Asks=[new(ExecutionCostEngine.Ceiling(value+.01m,.01m),depth)]};
        return new(at,[market],f.Shadow,f.Hourly.ToDictionary(p=>p.Key,p=>QuantSignalEngine.Frame(p.Key,p.Value,at,f.Cohort!.Parameters)),new Dictionary<string,ExecutionPlanningInputs>{{"TESTUSDT",f.Execution["TESTUSDT"] with{Book=book,Rules=f.Execution["TESTUSDT"].Rules! with{ObservedAt=at}}}},new Dictionary<string,FundingRateWindow?>(),f.ArchivedSignals);
    }
    internal static PaperState State(QuantRehearsalInput f)=>f.Paper.State with{QuantControl=QuantPaperEngine.Control(f.Cohort!.StartedAt,f.Cohort.Parameters,Costs)};
    internal static (PaperState State,QuantRehearsalInput Fixture) Open(bool shortSide=false)
    {
        var f=QuantRehearsalTests.ReadyInput(shortSide);var pending=QuantPaperEngine.Tick(State(f),[],[],Input(f,f.CapturedAt));Assert.Single(pending.State.Pending);Assert.Empty(pending.State.Positions);
        var fill=QuantPaperEngine.Tick(pending.State,[],[],Input(f,f.CapturedAt.AddSeconds(2)));Assert.Single(fill.State.Positions);Assert.Empty(fill.State.Pending);return(fill.State,f);
    }
    [Theory][InlineData(false)][InlineData(true)]public void SeparateIntentThenSubsequentBookFillsOnceAndSurvivesSerializedRestart(bool shortSide)
    {
        var (state,f)=Open(shortSide);var restored=JsonSerializer.Deserialize<PaperState>(JsonSerializer.Serialize(state))!;
        var replay=QuantPaperEngine.Tick(restored,[],[],Input(f,f.CapturedAt.AddSeconds(3)));Assert.Single(replay.State.Positions);Assert.Empty(replay.State.Pending);Assert.Single(replay.State.QuantUsedSignals!);
        Assert.Equal(QuantRules.HashValue(state.Positions[0].Intent),QuantRules.HashValue(replay.State.Positions[0].Intent));Assert.Equal(state.Cash,replay.State.Cash);
    }
    [Fact]public void ActivationBoundaryPreviewAndMissingArchivedInputsCannotProduceIntents()
    {
        var f=QuantRehearsalTests.ReadyInput();var input=Input(f,f.CapturedAt);
        Assert.Empty(QuantPaperEngine.Tick(State(f) with{QuantControl=QuantPaperEngine.Control(f.CapturedAt,f.Cohort!.Parameters,Costs)},[],[],input).State.Pending);
        Assert.Empty(QuantPaperEngine.Tick(State(f),[],[],input with{Archived=new Dictionary<string,QuantInputBundle>()}).State.Pending);
        var snapshot=f.Shadow! with{Assessments=f.Shadow!.Assessments.Select(a=>a with{Mode="CurrentPreview"}).ToArray()};Assert.Empty(QuantPaperEngine.Tick(State(f),[],[],input with{Snapshot=snapshot}).State.Pending);
    }
    [Theory][InlineData(false)][InlineData(true)]public void StopExitsUseNewBookRetainActualGapLossAndNeverInventInsuranceCredit(bool shortSide)
    {
        var (state,f)=Open(shortSide);var p=state.Positions[0];var at=f.CapturedAt.AddSeconds(5);var value=shortSide?10000m:1m;
        var result=QuantPaperEngine.Tick(state,[],[],Input(f,at,value));Assert.Empty(result.State.Positions);var trade=Assert.Single(result.Trades);
        Assert.Equal(QuantPaperEngine.Version,trade.Version);Assert.Equal(0,trade.SettlementAdjustment);Assert.Equal(trade.GrossPnl-trade.Fees-trade.FundingCost,trade.NetPnl);Assert.True(trade.NetPnl<-p.InitialMargin);
        if(shortSide)Assert.True(result.State.Cash<0);
    }
    [Fact]public void PauseStillExecutesStopsWhileNoFreshBookKeepsTheExitLatched()
    {
        var (state,f)=Open();var at=f.CapturedAt.AddSeconds(5);var inputs=Input(f,at,1);var missing=inputs with{Execution=new Dictionary<string,ExecutionPlanningInputs>{{"TESTUSDT",inputs.Execution["TESTUSDT"] with{Book=null}}}};
        var waiting=QuantPaperEngine.Tick(state with{Enabled=false},[],[],missing);Assert.Single(waiting.State.Positions);Assert.NotNull(waiting.State.Positions[0].Quant!.PendingExit);Assert.Empty(waiting.Trades);
        // Price recovered, but a triggered market exit remains latched.
        var filled=QuantPaperEngine.Tick(waiting.State,[],[],Input(f,at.AddSeconds(2)));Assert.Empty(filled.State.Positions);Assert.Single(filled.Trades);
    }
    [Fact]public void TwoTargetsAndTimeLimitConserveQuantityFeesAndSharedCash()
    {
        var (state,f)=Open();var original=state.Positions[0];var history=new List<PaperTrade>();var at=f.CapturedAt.AddSeconds(5);
        foreach(var target in new[]{original.TakePrice+.1m,original.Target2+.1m})
        {
            var result=QuantPaperEngine.Tick(state,history,[],Input(f,at,target));Assert.Single(result.Trades);state=result.State;history.AddRange(result.Trades);at=at.AddSeconds(3);
        }
        Assert.True(state.Positions[0].FirstTaken&&state.Positions[0].SecondTaken);
        at=original.OpenedAt.AddHours(72).AddSeconds(1);var close=QuantPaperEngine.Tick(state,history,[],Input(f,at,original.EntryPrice));history.AddRange(close.Trades);
        Assert.Empty(close.State.Positions);Assert.Equal(original.InitialQuantity,history.Sum(t=>t.Quantity));Assert.True(history[^1].FinalExit);Assert.Equal(close.State.Cash-1000,history.Sum(t=>t.NetPnl));
    }
    [Fact]public void InsufficientDepthPartiallyExitsAndWaitsForANewBookForTheRest()
    {
        var (state,f)=Open();var at=f.CapturedAt.AddSeconds(5);var qty=state.Positions[0].Quantity;
        var first=QuantPaperEngine.Tick(state,[],[],Input(f,at,1,qty/2));Assert.Single(first.Trades);Assert.Single(first.State.Positions);Assert.NotNull(first.State.Positions[0].Quant!.PendingExit);
        var second=QuantPaperEngine.Tick(first.State,first.Trades,[],Input(f,at.AddSeconds(2),1));Assert.Empty(second.State.Positions);Assert.Equal(qty,first.Trades.Sum(t=>t.Quantity)+second.Trades.Sum(t=>t.Quantity));
    }
    [Fact]public void LateFundingAfterCloseAdjustsCashExactlyOnceWithoutChangingTheClosedTrade()
    {
        var (state,f)=Open();var p=state.Positions[0];var fundingAt=p.OpenedAt.AddMinutes(20);var exitAt=fundingAt.AddSeconds(2);
        var closed=QuantPaperEngine.Tick(state,[],[],Input(f,exitAt,1));var tradeHash=QuantRules.HashValue(closed.Trades);var now=exitAt.AddMinutes(11);
        var window=new FundingRateWindow(p.Symbol,p.OpenedAt,exitAt,now,true,[new(p.Symbol,fundingAt,.001m,100,"Regular",now)]);
        var input=Input(f,now,1) with{Funding=new Dictionary<string,FundingRateWindow?>{{p.Symbol,window}}};
        var charge=QuantPaperEngine.Tick(closed.State,closed.Trades,[],input);var payment=Assert.Single(charge.FundingEvents);
        Assert.Equal(0,payment.OpenAllocated);Assert.Equal(p.InitialQuantity*.1m,payment.ClosedAllocated);Assert.Equal(closed.State.Cash-payment.ClosedAllocated,charge.State.Cash);Assert.Equal(tradeHash,QuantRules.HashValue(closed.Trades));
        var repeated=QuantPaperEngine.Tick(charge.State,closed.Trades,charge.FundingEvents,input with{At=now.AddSeconds(1)});Assert.Empty(repeated.FundingEvents);Assert.Equal(charge.State.Cash,repeated.State.Cash);
        var review=StrategyReview.Build(charge.State,closed.Trades,new Dictionary<int,MarketView>(),null,now,charge.FundingEvents);var lane=Assert.Single(review.Lanes,l=>l.Key=="Quant");Assert.Equal(closed.Trades.Sum(t=>t.NetPnl)-payment.ClosedAllocated,lane.RealizedNet);Assert.DoesNotContain(review.Lanes,l=>l.Key=="Legacy"&&l.SettlementLegs>0);
    }
    [Fact]public void LegacyAndQuantPositionsCloseThroughDifferentRulesInOneAtomicBatch()
    {
        var (state,f)=Open();var legacyIntent=new PaperIntent("old",Guid.Empty,2,"OLDUSDT",f.CapturedAt.AddDays(-1),null);
        var old=new PaperPosition(Guid.NewGuid(),"old",2,"OLDUSDT",f.CapturedAt.AddHours(-1),100,.1m,1,1,.005m,0,f.CapturedAt,100,f.CapturedAt,95,110,legacyIntent){InitialQuantity=.1m,InitialStop=95,InitialAtr=1,Target2=120};
        state=state with{Positions=state.Positions.Append(old).ToArray(),Cash=state.Cash-1};var input=Input(f,f.CapturedAt.AddSeconds(5),1);var market=input.Markets[0] with{Instrument=new(2,"OLDUSDT","OLD",f.CapturedAt.AddYears(-1),true),Price=94,MarkPrice=94};
        var result=QuantPaperEngine.Tick(state,[],[],input with{Markets=input.Markets.Append(market).ToArray()});Assert.Equal(2,result.Trades.Count);Assert.Empty(result.State.Positions);
        Assert.Contains(result.Trades,t=>t.Version==StructuralPaperEngine.Version&&t.Quant is null);Assert.Contains(result.Trades,t=>t.Version==QuantPaperEngine.Version&&t.Quant is not null);
    }
    [Fact]public void NullNewFieldsPreserveTheLegacyJsonShapeUsedByFrozenResearchHashes()
    {
        var f=QuantRehearsalTests.ReadyInput();var json=JsonSerializer.Serialize(f.Paper);Assert.DoesNotContain("QuantControl",json);Assert.DoesNotContain("QuantUsedSignals",json);Assert.DoesNotContain("QuantFunding",json);
    }
    [Fact]public void AtomicFundingDuplicateAndRevisionConflictRollbackCashAndEvents()
    {
        using var temp=new Database();var (state,f)=Open();state=state with{Revision=1};temp.Db.SavePaper(new(state,0,[]));
        var now=f.CapturedAt.AddMinutes(30);var p=state.Positions[0];var payment=new FundingAuditPayment(ExecutionFundingEngine.Version,p.Id,p.Symbol,p.Direction,now.AddMinutes(-1),"Regular",p.Quantity,.001m,100,p.Quantity*.1m,now);
        var e=new QuantFundingEvent("funding-key",now,0,payment.SignedCost,payment);var next=state with{Cash=state.Cash-payment.SignedCost,Revision=2,UpdatedAt=now};temp.Db.SavePaper(new(next,1,[]){FundingEvents=[e]});
        Assert.ThrowsAny<Exception>(()=>temp.Db.SavePaper(new(next with{Cash=next.Cash-payment.SignedCost,Revision=3},2,[]){FundingEvents=[e]}));
        var repo=new PaperRepository(temp.Db);Assert.Equal(next.Cash,repo.Restore()!.Cash);Assert.Single(repo.QuantFundingEvents());
        Assert.Throws<MappingConflictException>(()=>temp.Db.SavePaper(new(next,1,[])));Assert.Equal(next.Cash,repo.Restore()!.Cash);
        Assert.Throws<InvalidDataException>(()=>temp.Db.SavePaper(new(next with{Revision=3,QuantControl=next.QuantControl! with{ActivatedAt=now}},2,[])));
    }
    [Fact]public async Task AcceptedPersistenceDoesNotAbandonAcknowledgementWhenTheRequestIsCancelled()
    {
        var queue=new PersistenceQueue();var persistence=new PaperPersistence(queue);using var cancellation=new CancellationTokenSource();var f=QuantRehearsalTests.ReadyInput();var task=persistence.SaveAsync(new(State(f),2,[]),cancellation.Token);
        var command=Assert.IsType<SavePaperCommand>(await queue.Channel.Reader.ReadAsync());cancellation.Cancel();Assert.False(task.IsCompleted);command.Completion.SetResult();await task;
    }
    [Fact]public void TrailUsesClosedHoursAndCannotExecuteAgainstTheSameSourceQuoteThatCreatedTheStop()
    {
        var (state,f)=Open();var original=state.Positions[0];var history=new List<PaperTrade>();var at=f.CapturedAt.AddSeconds(5);
        foreach(var target in new[]{original.TakePrice+.1m,original.Target2+.1m}){var partial=QuantPaperEngine.Tick(state,history,[],Input(f,at,target));state=partial.State;history.AddRange(partial.Trades);at=at.AddSeconds(3);}
        var hour=DateTimeOffset.FromUnixTimeSeconds(original.OpenedAt.ToUnixTimeSeconds()/3600*3600).AddHours(1);at=hour.AddHours(1).AddSeconds(5);var value=original.EntryPrice+25;
        var bar=new Candle(original.Symbol,60,hour,value,value+5,value-1,value,1000,hour.AddHours(1).AddMilliseconds(-1),at);
        var input=Input(f,at,value);var frame=input.Frames[original.Symbol] with{Issue=null,Bars=[bar]};input=input with{Frames=new Dictionary<string,QuantFrame>{{original.Symbol,frame}}};
        var trailed=QuantPaperEngine.Tick(state,history,[],input);Assert.Empty(trailed.Trades);Assert.True(trailed.State.Positions[0].StopPrice>value);
        var same=QuantPaperEngine.Tick(trailed.State,history,[],input with{At=at.AddSeconds(2)});Assert.Empty(same.Trades);
        var next=QuantPaperEngine.Tick(same.State,history,[],Input(f,at.AddSeconds(4),value));Assert.Empty(next.State.Positions);Assert.Single(next.Trades);
    }
    [Fact]public void PartialBelowTheLotGridIsExplicitlySkippedWithoutInventingAThirtyPercentFill()
    {
        var (state,f)=Open();var old=state.Positions[0];var margin=old.EntryPrice*.001m/old.Quant!.Plan.Leverage;var fee=old.EntryPrice*.001m*Costs.FeeRate;
        var small=old with{Quantity=.001m,InitialQuantity=.001m,InitialMargin=margin,RemainingMargin=margin,OpenFee=fee};state=state with{Positions=[small],Cash=1000-margin-fee};
        var result=QuantPaperEngine.Tick(state,[],[],Input(f,f.CapturedAt.AddSeconds(5),old.TakePrice+.1m));Assert.Empty(result.Trades);Assert.Equal(.001m,result.State.Positions[0].Quantity);Assert.False(result.State.Positions[0].FirstTaken);Assert.True(result.State.Positions[0].Quant!.SkipFirst);
    }
    [Fact]public void ShortFundingCreditsCollateralAndConflictsStillAllowTheRiskExit()
    {
        var (state,f)=Open(true);var p=state.Positions[0];var at=p.OpenedAt.AddMinutes(12);var fundingAt=p.OpenedAt.AddSeconds(30);
        var window=new FundingRateWindow(p.Symbol,p.OpenedAt,at.AddMinutes(-2),at,true,[new(p.Symbol,fundingAt,.001m,100,"Regular",at)]);
        var input=Input(f,at) with{Funding=new Dictionary<string,FundingRateWindow?>{{p.Symbol,window}}};
        var credited=QuantPaperEngine.Tick(state,[],[],input);var payment=Assert.Single(credited.FundingEvents);Assert.True(payment.Evidence.SignedCost<0);Assert.True(credited.State.Positions[0].RemainingMargin>p.RemainingMargin);
        var changed=window with{Records=[window.Records[0] with{Rate=.002m}]};var exiting=Input(f,at.AddSeconds(2),10000) with{Funding=new Dictionary<string,FundingRateWindow?>{{p.Symbol,changed}}};
        var result=QuantPaperEngine.Tick(credited.State,[],credited.FundingEvents,exiting);Assert.Empty(result.FundingEvents);Assert.Empty(result.State.Positions);Assert.Single(result.Trades);Assert.Contains(result.State.QuantFunding!,c=>c.Status=="Conflict");
    }
    [Fact]public void CurrentInstrumentIdentityCannotBeSubstitutedForTheArchivedAssetId()
    {
        var f=QuantRehearsalTests.ReadyInput();var input=Input(f,f.CapturedAt);input=input with{Markets=input.Markets.Select(m=>m with{Instrument=m.Instrument with{AssetId=999}}).ToArray()};
        Assert.Empty(QuantPaperEngine.Tick(State(f),[],[],input).State.Pending);
    }
    [Fact]public async Task ActivationIsAtomicIdempotentAndResumeKeepsItsOriginalEffectiveTime()
    {
        using var temp=new Database();var f=QuantRehearsalTests.ReadyInput();var baseline=f.Paper.State with{Revision=1};temp.Db.SavePaper(new(baseline,0,[]));var clock=new Clock(f.CapturedAt);var repo=new PaperRepository(temp.Db);var persistence=new DirectPersistence(temp.Db);
        var service=new PaperService(repo,persistence,null!,null!,clock,null!,null!,null,new Coordinator(Input(f,f.CapturedAt)));
        await service.ActivateQuantAsync(f.Cohort!,Costs,default);var first=(await service.ExecutionContextAsync(default)).State;Assert.Equal(2,first.Revision);Assert.Equal(baseline.Cash,first.Cash);
        clock.At=clock.At.AddMinutes(5);await service.ActivateQuantAsync(f.Cohort!,Costs,default);Assert.Equal(2,(await service.ExecutionContextAsync(default)).State.Revision);
        await service.SetEnabledAsync(false,default);await service.SetEnabledAsync(true,default);var resumed=(await service.ExecutionContextAsync(default)).State;Assert.Equal(first.QuantControl,resumed.QuantControl);Assert.Equal(baseline.ExecutionStartedAt,resumed.ExecutionStartedAt);
    }
    [Fact]public async Task AmbiguousWriteFailureReloadsTheCommittedVersionRatherThanReapplyingIt()
    {
        using var temp=new Database();var f=QuantRehearsalTests.ReadyInput();var baseline=f.Paper.State with{Revision=1};temp.Db.SavePaper(new(baseline,0,[]));var persistence=new DirectPersistence(temp.Db){FailAfterCommit=true};
        var service=new PaperService(new PaperRepository(temp.Db),persistence,null!,null!,new Clock(f.CapturedAt),null!,null!,null,new Coordinator(Input(f,f.CapturedAt)));
        await Assert.ThrowsAsync<IOException>(()=>service.ActivateQuantAsync(f.Cohort!,Costs,default));var restored=(await service.ExecutionContextAsync(default)).State;Assert.Equal(2,restored.Revision);Assert.NotNull(restored.QuantControl);
        await service.ActivateQuantAsync(f.Cohort!,Costs,default);Assert.Equal(2,(await service.ExecutionContextAsync(default)).State.Revision);
    }
    [Fact]public async Task LegacyUnfilledOrdersPreventActivationWithoutBeingCancelledOrRepriced()
    {
        using var temp=new Database();var f=QuantRehearsalTests.ReadyInput();var pending=new PaperIntent("legacy",Guid.Empty,2,"OLDUSDT",f.CapturedAt,null);var baseline=f.Paper.State with{Revision=1,Pending=[pending]};temp.Db.SavePaper(new(baseline,0,[]));
        var service=new PaperService(new PaperRepository(temp.Db),new DirectPersistence(temp.Db),null!,null!,new Clock(f.CapturedAt),null!,null!,null,new Coordinator(Input(f,f.CapturedAt)));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ActivateQuantAsync(f.Cohort!,Costs,default));var state=(await service.ExecutionContextAsync(default)).State;Assert.Null(state.QuantControl);Assert.Equal(pending,Assert.Single(state.Pending));Assert.Equal(1,state.Revision);
    }
    private sealed class Coordinator(QuantPaperInput input):IQuantExecutionCoordinator
    {public Task<QuantPaperInput> CaptureAsync(PaperState state,IReadOnlyList<PaperTrade> trades,CancellationToken ct)=>Task.FromResult(input);}
    private sealed class Clock(DateTimeOffset at):TimeProvider
    {public DateTimeOffset At {get;set;}=at;public override DateTimeOffset GetUtcNow()=>At;}
    private sealed class DirectPersistence(SqliteDatabase database):IPaperPersistence
    {public bool FailAfterCommit {get;set;} public Task SaveAsync(PaperBatch batch,CancellationToken ct){database.SavePaper(batch);if(FailAfterCommit){FailAfterCommit=false;throw new IOException("fixture: acknowledgement lost");}return Task.CompletedTask;}}
    private sealed class Database:IDisposable
    {
        private readonly string root=Path.Combine(Path.GetTempPath(),"Pullet-Live-"+Guid.NewGuid().ToString("N"));public SqliteDatabase Db {get;}
        public Database(){Db=new(Options.Create(new StorageOptions{Directory=root}));Db.Initialize();}
        public void Dispose(){SqliteConnection.ClearAllPools();var full=Path.GetFullPath(root);if(!full.StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException();if(Directory.Exists(full))Directory.Delete(full,true);}
    }
}
