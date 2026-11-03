using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Data.Sqlite;
using PulletCryptoRadar.Infrastructure;
using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.IntegrationTests;

public sealed class StructuralPaperTests
{
    private static readonly DateTimeOffset Now=new(2026,10,29,14,0,0,TimeSpan.Zero);
    private static PaperState State()=>new(){Revision=1,StartedAt=Now.AddDays(-1),ExecutionStartedAt=Now.AddMinutes(-1),UpdatedAt=Now.AddSeconds(-1),Enabled=true,Rules=StructuralPaperEngine.Rules()};
    private static MarketView Market(DateTimeOffset time,decimal price=100,int id=1,decimal? mark=null)=>new(new(id,$"TEST{id}USDT","TEST",Now.AddDays(-100),true),price,10000000,10,
        mark??price,0,time,time,time,300,300,300,new());
    private static Dictionary<int,MarketView> Markets(DateTimeOffset time,decimal price=100,int n=1)=>Enumerable.Range(1,n).ToDictionary(i=>i,i=>Market(time,price,i));
    private static ForwardDecision Decision(int id=1,string strategy="A")=>new($"d{id}:{strategy}",$"TEST{id}USDT",strategy,Now,Now,"Candidate","saved analysis",337,id,2,10,
        10,Now.AddMinutes(-5),Now,"OI confirmed",new(100.05m,96,1,10,6,106,112,"已确认历史压力","风险投影",2),PaperAnalysis.RulesHash);
    private static PaperState Pending(int n=1)=>StructuralPaperEngine.Tick(State(),Enumerable.Range(1,n).Select(i=>Decision(i)).ToArray(),Markets(Now,n:n),Now).State;
    private static PaperState Opened(int n=1)=>StructuralPaperEngine.Tick(Pending(n),[],Markets(Now.AddSeconds(20),n:n),Now.AddSeconds(20)).State;
    [Fact]public void ExperimentalModeDoesNotPretendResearchValidated()
    {Assert.False(State().Rules.ResearchValidated);Assert.True(State().Rules.ExperimentalSimulation);Assert.Equal(5,State().Rules.MaximumPositions);Assert.Equal(.20m,State().Rules.ReviewLossFraction);}
    [Fact]public void ExistingPositionSkipIsExplicitAndDoesNotCreateAnotherIntent()
    {
        var old=Opened();var now=Now.AddHours(1);var next=Decision() with{Id="new-hour",DecisionAt=now,CapturedAt=now};
        var result=StructuralPaperEngine.Tick(old,[next],Markets(now),now);
        Assert.Single(result.State.Positions);Assert.Empty(result.State.Pending);Assert.Empty(result.Trades);
        Assert.Contains(result.State.Decisions,d=>d.Action=="跳过"&&d.Reason.Contains("已有同币持仓"));
    }
    [Fact]public void WaitsForBothStrictlyLaterPriceAndMarkAndDoesNotBackfillOldSignals()
    {
        var state=Pending();Assert.Single(state.Pending);Assert.Empty(state.Positions);
        Assert.Empty(StructuralPaperEngine.Tick(state,[],Markets(Now),Now.AddSeconds(5)).State.Positions);
        var oldMark=Market(Now.AddSeconds(20)) with{FundingSourceTime=Now};
        Assert.Empty(StructuralPaperEngine.Tick(state,[],new Dictionary<int,MarketView>{{1,oldMark}},Now.AddSeconds(20)).State.Positions);
        Assert.Empty(StructuralPaperEngine.Tick(State() with{ExecutionStartedAt=Now.AddSeconds(1)},[Decision()],Markets(Now),Now).State.Pending);
    }
    [Fact]public void MissingContraryOrFutureOiIsRejectedAndNotMarkedConfirmed()
    {
        foreach(var d in new[]{Decision() with{Oi24hPercent=null},Decision() with{Oi24hPercent=-1},Decision() with{OiReceivedTime=Now.AddSeconds(1)},Decision() with{RulesHash="bad"}})
            Assert.Empty(StructuralPaperEngine.Tick(State(),[d],Markets(Now),Now).State.Pending);
    }
    [Fact]public void FivePositionsUseTenReusableBucketsWithSixUsdtRiskEach()
    {
        var pending=Pending(6);Assert.Equal(5,pending.Pending.Count);
        var s=StructuralPaperEngine.Tick(pending,[],Markets(Now.AddSeconds(20),n:6),Now.AddSeconds(20)).State;
        Assert.Equal(5,s.Positions.Count);Assert.Equal(5,s.Positions.Select(p=>p.Bucket).Distinct().Count());
        Assert.All(s.Positions,p=>{Assert.InRange(p.InitialMargin,0,100);Assert.InRange(p.PlannedRisk,0,6);Assert.Equal(96,p.StopPrice);});
        Assert.True(s.Positions.Sum(p=>StructuralPaperEngine.RemainingRisk(p,s.Rules))<=30.0000001m);Assert.True(s.Cash>500);
    }
    [Fact]public void QuantitySizedByStructureNotArbitraryFixedPercent()
    {var p=Assert.Single(Opened().Positions);Assert.Equal(96,p.StopPrice);Assert.Equal(106,p.TakePrice);Assert.Equal(112,p.Target2);Assert.NotEqual(p.EntryPrice*.985m,p.StopPrice);Assert.True(p.InitialMargin<100);}
    [Fact]public void StopLiquidationConflictAndChasingAreRejectedAtFillTime()
    {
        var pending=Pending();var gap=StructuralPaperEngine.Tick(pending,[],Markets(Now.AddSeconds(20),103),Now.AddSeconds(20));Assert.Empty(gap.State.Positions);
        var old=pending.Pending[0];var d=old.StructuralEvidence!;var bad=pending with{Pending=[old with{StructuralEvidence=d with{Plan=d.Plan! with{Stop=80}}}]};
        Assert.Empty(StructuralPaperEngine.Tick(bad,[],Markets(Now.AddSeconds(20)),Now.AddSeconds(20)).State.Positions);
    }
    [Fact]public void PartialExitsLeaveFortyPercentAndFeesReconcile()
    {
        var s=Opened();var p=s.Positions[0];var first=StructuralPaperEngine.Tick(s,[],Markets(Now.AddSeconds(40),107),Now.AddSeconds(40));
        Assert.Single(first.Trades);Assert.False(first.Trades[0].FinalExit);Assert.Equal(p.InitialQuantity*.7m,first.State.Positions[0].Quantity);
        var second=StructuralPaperEngine.Tick(first.State,[],Markets(Now.AddSeconds(60),113),Now.AddSeconds(60));
        Assert.Single(second.Trades);Assert.Equal(p.InitialQuantity*.4m,second.State.Positions[0].Quantity);
        var final=StructuralPaperEngine.Tick(second.State with{Enabled=false},[],Markets(Now.AddSeconds(80),105),Now.AddSeconds(80));
        Assert.Empty(final.State.Positions);Assert.True(Assert.Single(final.Trades).FinalExit);
        var all=first.Trades.Concat(second.Trades).Concat(final.Trades).ToArray();
        Assert.All(all,t=>Assert.Equal(t.GrossPnl-t.Fees-t.FundingCost+t.SettlementAdjustment,t.NetPnl,8));
        Assert.Equal(1000+all.Sum(t=>t.NetPnl),final.State.Cash,8);Assert.Single(all.Select(t=>t.PositionId).Distinct());
    }
    [Fact]public void GapAcrossBothTargetsFillsAtObservedQuoteAndNeverDoubleSells()
    {
        var p=Opened();var step=StructuralPaperEngine.Tick(p,[],Markets(Now.AddSeconds(40),120),Now.AddSeconds(40));
        Assert.Equal(2,step.Trades.Count);Assert.All(step.Trades,t=>Assert.Equal(120*.9995m,t.ExitPrice));
        var same=StructuralPaperEngine.Tick(step.State,[],Markets(Now.AddSeconds(40),120),Now.AddSeconds(45));Assert.Empty(same.Trades);
    }
    [Fact]public void PausedAccountStillExitsOnStructureStopWithGapSlippage()
    {
        var s=Opened() with{Enabled=false};var result=StructuralPaperEngine.Tick(s,[],Markets(Now.AddSeconds(100),94),Now.AddSeconds(100));
        var t=Assert.Single(result.Trades);Assert.Equal(94*.9995m,t.ExitPrice);Assert.True(t.QuoteGap);Assert.Empty(result.State.Positions);
    }
    [Fact]public void StaleAndFutureQuotesDoNotInventExitsOrOpenPositions()
    {
        var s=Opened();Assert.Empty(StructuralPaperEngine.Tick(s,[],Markets(Now.AddSeconds(20),80),Now.AddMinutes(3)).Trades);
        Assert.Empty(StructuralPaperEngine.Tick(s,[],Markets(Now.AddHours(1),80),Now.AddMinutes(3)).Trades);
    }
    [Fact]public void IsolatedLiquidationIsExplicitAndReconcilesAdjustment()
    {
        var s=Opened();var result=StructuralPaperEngine.Tick(s,[],Markets(Now.AddSeconds(40),50),Now.AddSeconds(40));
        var t=Assert.Single(result.Trades);Assert.Contains("强平",t.Reason);Assert.NotEqual(0,t.SettlementAdjustment);
        Assert.Equal(t.GrossPnl-t.Fees-t.FundingCost+t.SettlementAdjustment,t.NetPnl,8);
    }
    [Fact]public void TwentyPercentLossIsReviewReminderAndExperimentContinuesWithoutCapitalReset()
    {
        var s=State() with{Cash=800};var result=StructuralPaperEngine.Tick(s,[Decision()],Markets(Now),Now);
        Assert.True(result.State.ReviewThresholdReached);Assert.True(result.State.Enabled);Assert.Single(result.State.Pending);Assert.Equal(800,result.State.Cash);
        var restored=JsonSerializer.Deserialize<PaperState>(JsonSerializer.Serialize(result.State))!;
        Assert.True(StructuralPaperEngine.Tick(restored,[],Markets(Now.AddSeconds(20)),Now.AddSeconds(20)).State.Enabled);
    }
    [Fact]public void LowerEquityShrinksRiskAndStillAllowsValidEntries()
    {
        var result=StructuralPaperEngine.Tick(Pending() with{Cash=700},[],Markets(Now.AddSeconds(20)),Now.AddSeconds(20));
        var p=Assert.Single(result.State.Positions);Assert.InRange(p.PlannedRisk,0,4.2m);Assert.True(result.State.Enabled);Assert.True(result.State.ReviewThresholdReached);
    }
    [Fact]public void InsufficientCashDoesNotInventCapitalOrForceMinimumSize()
    {
        var result=StructuralPaperEngine.Tick(Pending() with{Cash=.001m},[],Markets(Now.AddSeconds(20)),Now.AddSeconds(20));
        Assert.Empty(result.State.Positions);Assert.Equal(.001m,result.State.Cash);Assert.True(result.State.Enabled);
    }
    [Fact]public void SimultaneousStrategiesDoNotOpenSameAssetTwiceAndRulesAreChecked()
    {
        var s=StructuralPaperEngine.Tick(State(),[Decision(strategy:"A"),Decision(strategy:"B")],Markets(Now),Now).State;
        Assert.Single(s.Pending);Assert.Equal("B",s.Pending[0].StructuralEvidence!.Strategy);
        Assert.Throws<InvalidOperationException>(()=>StructuralPaperEngine.Tick(State() with{Rules=StructuralPaperEngine.Rules() with{MaximumPositions=10}},[],Markets(Now),Now));
    }
    [Fact]public void PartialFundingAllocationAndEightHourChargeAreNotDoubleCounted()
    {
        var s=Opened();var t=Now.AddHours(8);var first=StructuralPaperEngine.Tick(s,[],Markets(t,107),t);
        var last=StructuralPaperEngine.Tick(first.State with{Enabled=false},[],Markets(t.AddSeconds(20),100),t.AddSeconds(20));
        var all=first.Trades.Concat(last.Trades).ToArray();Assert.Equal(2,all.Length);
        Assert.Equal(s.Positions[0].InitialQuantity*s.Positions[0].EntryPrice*s.Rules.FundingCost8h,all.Sum(x=>x.FundingCost),8);
        Assert.Equal(1000+all.Sum(x=>x.NetPnl),last.State.Cash,8);
    }
    private static PaperState ShortOpened()
    {
        var d=Decision() with{Direction="Short",Plan=new(99.95m,104,1,10,6,94,88,"历史支撑","风险投影",2)};
        var pending=StructuralPaperEngine.Tick(State(),[d],Markets(Now),Now).State;
        return StructuralPaperEngine.Tick(pending,[],Markets(Now.AddSeconds(20)),Now.AddSeconds(20)).State;
    }
    [Fact]public void ShortEntryStopAndLiquidationHaveOppositeDirectionToLong()
    {
        var s=ShortOpened();var p=Assert.Single(s.Positions);Assert.Equal("Short",p.Direction);Assert.Equal(99.95m,p.EntryPrice);
        Assert.True(PaperEngine.EstimatedLiquidation(p,s.Rules)>p.EntryPrice);
        var result=StructuralPaperEngine.Tick(s,[],Markets(Now.AddSeconds(40),105),Now.AddSeconds(40));var t=Assert.Single(result.Trades);
        Assert.Equal(105*1.0005m,t.ExitPrice);Assert.True(t.GrossPnl<0);Assert.Equal("Short",t.Direction);
    }
    [Fact]public void ShortTwoTargetsAndTailEarnFromDeclineAndReconcile()
    {
        var s=ShortOpened();var first=StructuralPaperEngine.Tick(s,[],Markets(Now.AddSeconds(40),93),Now.AddSeconds(40));
        var second=StructuralPaperEngine.Tick(first.State,[],Markets(Now.AddSeconds(60),87),Now.AddSeconds(60));
        var p=Assert.Single(second.State.Positions);Assert.Equal(s.Positions[0].InitialQuantity*.4m,p.Quantity,20);Assert.True(p.StopPrice<104);
        var last=StructuralPaperEngine.Tick(second.State with{Enabled=false},[],Markets(Now.AddSeconds(80),94),Now.AddSeconds(80));
        Assert.Empty(last.State.Positions);var trades=first.Trades.Concat(second.Trades).Concat(last.Trades).ToArray();
        Assert.All(trades,t=>{Assert.True(t.GrossPnl>0);Assert.Equal(t.GrossPnl-t.Fees-t.FundingCost+t.SettlementAdjustment,t.NetPnl,8);});
        Assert.Equal(1000+trades.Sum(t=>t.NetPnl),last.State.Cash,8);
    }
    [Fact]public void ShortLiquidationAndGapDoNotAssumeLongMath()
    {
        var result=StructuralPaperEngine.Tick(ShortOpened(),[],Markets(Now.AddSeconds(120),130),Now.AddSeconds(120));
        var t=Assert.Single(result.Trades);Assert.Contains("强平",t.Reason);Assert.True(t.QuoteGap);Assert.True(t.NetPnl<0);
        Assert.Equal(t.GrossPnl-t.Fees-t.FundingCost+t.SettlementAdjustment,t.NetPnl,8);
    }
    [Fact]public void AnalysisUsesKnownClosedCandlesToGenerateBearishStructure()
    {
        var end=Now;var rows=Enumerable.Range(0,337).Select(i=>new Candle("TEST1USDT",60,end.AddHours(i-337),101,103,99,100,100,
            end.AddHours(i-336).AddMilliseconds(-1),end.AddHours(i-336))).ToArray();
        rows[330]=rows[330] with{High=104};rows[^1]=rows[^1] with{QuoteVolume=150};
        var series=new Dictionary<DerivativeKind,IReadOnlyList<DerivativeSample>>{{DerivativeKind.OpenInterest5m,
            [new("TEST1USDT",DerivativeKind.OpenInterest5m,end.AddMinutes(-5).AddHours(-24),Now,Quantity:100),new("TEST1USDT",DerivativeKind.OpenInterest5m,end.AddMinutes(-5),Now,Quantity:110)]}};
        var oi=new DerivativeRuntimeSnapshot("TEST1USDT",series,[],null,null);
        var ds=PaperAnalysis.Analyze(Now.AddHours(-1),Market(Now),rows,oi,Now);
        Assert.Equal(4,ds.Count);var shortA=ds.Single(d=>d.Direction=="Short"&&d.Strategy=="A");
        Assert.Equal("Candidate",shortA.Status);Assert.NotNull(shortA.Plan);Assert.True(shortA.Plan.Stop>shortA.Plan.ReferenceEntry);Assert.True(shortA.Plan.Target2<shortA.Plan.Target1);
        Assert.Equal(10,shortA.Oi24hPercent);Assert.All(ds,d=>Assert.Equal(PaperAnalysis.RulesHash,d.RulesHash));
        rows[^1]=rows[^1] with{ReceivedTime=Now.AddSeconds(1)};
        Assert.All(PaperAnalysis.Analyze(Now.AddHours(-1),Market(Now),rows,oi,Now),d=>Assert.Null(d.Plan));
    }
    private sealed class Persistence(SqliteDatabase db):IPaperPersistence
    {public Task SaveAsync(PaperBatch batch,CancellationToken ct){ct.ThrowIfCancellationRequested();db.SavePaper(batch);return Task.CompletedTask;}}
    private sealed class Clock:TimeProvider
    {public DateTimeOffset Current=Now;public override DateTimeOffset GetUtcNow()=>Current;}
    private sealed class History:IAssetRepository
    {
        public Dictionary<int,IReadOnlyList<Candle>> Rows {get;}=[];
        public Task<IReadOnlyList<Candle>> ReadHourlyHistoryAsync(int id,int limit,CancellationToken ct)=>Task.FromResult(Rows.GetValueOrDefault(id)??(IReadOnlyList<Candle>)[]);
        public Task<IReadOnlyList<Instrument>> ReadInstrumentsAsync(CancellationToken ct)=>Task.FromResult<IReadOnlyList<Instrument>>([]);
    }
    private sealed class Radar:IRadarSnapshotSource{public RadarSnapshot GetSnapshot()=>null!;}
    [Fact]public void WarmupIncludesTheActualTwentyAnalysisSymbolsAndAnExistingOutlierPosition()
    {
        var markets=Enumerable.Range(1,30).Select(i=>Market(Now,id:i) with{ExchangeReturn24h=i}).ToArray();
        var selected=PaperUniverse.Select(markets,Now,20);Assert.Equal(20,selected.Count);
        var held=Opened().Positions[0];var state=State() with{Positions=[held],AnalysisSymbols=["TEST2USDT"]};
        var warm=PaperUniverse.Warmup(markets,state,Now,20);
        Assert.All(selected,m=>Assert.Contains(warm,x=>x.Instrument.Symbol==m.Instrument.Symbol));
        Assert.Contains(warm,m=>m.Instrument.Symbol==held.Symbol);Assert.Contains(warm,m=>m.Instrument.Symbol=="TEST2USDT");
        Assert.Equal(warm.Count,warm.Select(m=>m.Instrument.Symbol).Distinct().Count());
    }
    [Fact]public async Task ReadySymbolDoesNotFinalizeUnreadySymbolsAndHourlyDecisionsAreNeverRewritten()
    {
        var root=Path.Combine(Path.GetTempPath(),"Pullet-Incremental-"+Guid.NewGuid());
        try
        {
            var db=new SqliteDatabase(Options.Create(new StorageOptions{Directory=root}));db.Initialize();db.SavePaper(new(State(),0,[]));
            var market=new MarketRuntimeStore();market.Register([Market(Now).Instrument,Market(Now,id:2).Instrument]);
            void Quote(int id,DateTimeOffset t)
            {market.Apply(new TickerEvent($"TEST{id}USDT",100,10000000,10,t,t));market.Apply(new MarkPriceEvent($"TEST{id}USDT",100,0,t.AddHours(8),t,t));}
            Quote(1,Now);Quote(2,Now);var history=new History();
            IReadOnlyList<Candle> Bars(int id)=>Enumerable.Range(0,337).Select(i=>new Candle($"TEST{id}USDT",60,Now.AddHours(i-337),100,103,99,100,100,Now.AddHours(i-336).AddMilliseconds(-1),Now.AddHours(i-336))).ToArray();
            history.Rows[1]=Bars(1);var clock=new Clock();
            var repo=new PaperRepository(db);var service=new PaperService(repo,new Persistence(db),market,new Radar(),clock,history,new DerivativeRuntimeStore());
            await service.TickAsync(default);var first=repo.Restore()!;
            Assert.Null(first.SignalHour);Assert.Equal(4,first.SignalObservations.Count);Assert.Equal(2,first.AnalysisSymbols.Count);
            history.Rows[2]=Bars(2);clock.Current=Now.AddSeconds(30);Quote(1,clock.Current);Quote(2,clock.Current);
            await service.TickAsync(default);var final=repo.Restore()!;
            Assert.Equal(Now,final.SignalHour);Assert.Equal(8,final.SignalObservations.Count);Assert.Equal(8,final.SignalObservations.Select(d=>d.Id).Distinct().Count());
            Assert.Equal(JsonSerializer.Serialize(first.SignalObservations),JsonSerializer.Serialize(final.SignalObservations.Take(4).ToArray()));
            clock.Current=Now.AddSeconds(60);await service.TickAsync(default);Assert.Equal(8,repo.Restore()!.SignalObservations.Count);
        }
        finally{SqliteConnection.ClearAllPools();if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    [Fact]public async Task RealRepositoryMigrationEnablesUnvalidatedExperimentWithoutResetAndRestoresPause()
    {
        var root=Path.Combine(Path.GetTempPath(),"Pullet-Experiment-"+Guid.NewGuid());
        try
        {
            var db=new SqliteDatabase(Options.Create(new StorageOptions{Directory=root}));db.Initialize();var clock=new Clock();
            var repo=new PaperRepository(db);var persist=new Persistence(db);var market=new MarketRuntimeStore();
            PaperService Service()=>new(repo,persist,market,new Radar(),clock,new History(),new DerivativeRuntimeStore());
            var service=Service();await service.SetEnabledAsync(true,default);var result=await service.OverviewAsync(default);
            Assert.True(result.State.Enabled);Assert.False(result.State.Rules.ResearchValidated);Assert.True(result.State.Rules.ExperimentalSimulation);
            Assert.Equal(1000,result.State.Cash);Assert.Equal(5,result.State.Rules.MaximumPositions);
            var started=result.State.ExecutionStartedAt;clock.Current=Now.AddSeconds(10);await service.SetEnabledAsync(true,default);
            Assert.Equal(started,(await service.OverviewAsync(default)).State.ExecutionStartedAt);
            var restored=Service();Assert.True((await restored.OverviewAsync(default)).State.Enabled);
            await restored.SetEnabledAsync(false,default);Assert.False((await Service().OverviewAsync(default)).State.Enabled);
            Assert.Equal(1000,repo.Restore()!.Cash);
        }
        finally{SqliteConnection.ClearAllPools();if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    [Fact]public void RealRepositoryStoresDistinctPartialLegsAndRejectsDuplicateBatch()
    {
        var root=Path.Combine(Path.GetTempPath(),"Pullet-Partial-"+Guid.NewGuid());
        try
        {
            var db=new SqliteDatabase(Options.Create(new StorageOptions{Directory=root}));db.Initialize();var initial=State();db.SavePaper(new(initial,0,[]));
            var pending=StructuralPaperEngine.Tick(initial,[Decision()],Markets(Now),Now);db.SavePaper(pending);
            var opened=StructuralPaperEngine.Tick(pending.State,[],Markets(Now.AddSeconds(20)),Now.AddSeconds(20));db.SavePaper(opened);
            var first=StructuralPaperEngine.Tick(opened.State,[],Markets(Now.AddSeconds(40),107),Now.AddSeconds(40));db.SavePaper(first);
            Assert.Throws<MappingConflictException>(()=>db.SavePaper(first));
            var repository=new PaperRepository(db);var restored=repository.Restore()!;Assert.True(restored.Positions[0].FirstTaken);
            var last=StructuralPaperEngine.Tick(restored with{Enabled=false},[],Markets(Now.AddSeconds(60),99),Now.AddSeconds(60));db.SavePaper(last);
            var records=repository.Trades();Assert.Equal(2,records.Count);Assert.Single(records.Select(t=>t.PositionId).Distinct());
            Assert.Equal(1000+records.Sum(t=>t.NetPnl),repository.Restore()!.Cash,8);
        }
        finally{SqliteConnection.ClearAllPools();if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    [Fact]public async Task AccountOverviewMarksShortProfitInCorrectDirection()
    {
        var root=Path.Combine(Path.GetTempPath(),"Pullet-Short-Overview-"+Guid.NewGuid());
        try
        {
            var db=new SqliteDatabase(Options.Create(new StorageOptions{Directory=root}));db.Initialize();var state=ShortOpened() with{Revision=1};db.SavePaper(new(state,0,[]));
            var market=new MarketRuntimeStore();market.Register([Market(Now).Instrument]);
            market.Apply(new TickerEvent("TEST1USDT",90,10000000,-10,Now.AddSeconds(40),Now.AddSeconds(40)));
            market.Apply(new MarkPriceEvent("TEST1USDT",90,0,Now.AddHours(8),Now.AddSeconds(40),Now.AddSeconds(40)));
            var service=new PaperService(new PaperRepository(db),new Persistence(db),market,new Radar(),new Clock{Current=Now.AddSeconds(40)},new History(),new DerivativeRuntimeStore());
            var result=await service.OverviewAsync(default);Assert.True(result.CompleteEquity>1000);Assert.True(result.UnrealizedPnl>0);
        }
        finally{SqliteConnection.ClearAllPools();if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
