using System.IO.Compression;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;
namespace PulletCryptoRadar.IntegrationTests;

public sealed class QuantPortfolioValidationTests:IDisposable
{
    private readonly string directory=Path.Combine(Path.GetTempPath(),"Pullet-Portfolio-"+Guid.NewGuid());
    internal static (QuantPortfolioFrozenInput Input,Dictionary<string,QuantPortfolioSource> Sources) Fixture()
    {
        var f=QuantRehearsalTests.ReadyInput();var pending=QuantPaperEngine.Tick(QuantLiveTests.State(f),[],[],QuantLiveTests.Input(f,f.CapturedAt));
        var fill=QuantPaperEngine.Tick(pending.State,[],[],QuantLiveTests.Input(f,f.CapturedAt.AddSeconds(2)));
        var reviews=pending.EntryReviews.Concat(fill.EntryReviews).Select(r=>new QuantPortfolioSource(r,f.ArchivedSignals[r.Intent.QuantIntent!.Decision.InputHash])).ToArray();
        var sources=reviews.ToDictionary(QuantRules.HashValue);var at=f.CapturedAt.AddSeconds(4);
        var input=new QuantPortfolioFrozenInput(Guid.NewGuid(),QuantPortfolioValidationEngine.Version,PaperEngine.Day(at).ToString("yyyy-MM-dd"),at,fill.State,[],[],
            QuantPortfolioValidationEngine.Freeze(QuantLiveTests.Input(f,at)),reviews.Select(r=>new QuantPortfolioSourceRef(r.Review.Id,QuantRules.HashValue(r))).ToArray());
        return(input,sources);
    }
    private static QuantPortfolioValidationRun Run(QuantPortfolioFrozenInput input,Dictionary<string,QuantPortfolioSource> sources)=>QuantPortfolioValidationEngine.Run(input,h=>sources[h]);
    [Fact]public void RealEngineAdmissionAndAllFixedBoundaryCasesAreReproducibleAndReadOnly()
    {
        var (input,sources)=Fixture();var before=QuantRules.HashValue(input);var run=Run(input,sources);var report=run.Report;
        Assert.Equal(2,report.Reproduced);Assert.Equal(0,report.Mismatches);Assert.Equal(0,report.ConstraintFailures);
        Assert.Equal(17,report.ScenariosPassed);Assert.Equal(0,report.ScenariosFailed);Assert.True(report.Repeatable&&report.SourcePreserved);
        Assert.Equal(1,report.LinkedEntries);Assert.Equal(0,report.UnlinkedEntries);Assert.Equal(before,QuantRules.HashValue(input));
        Assert.Equal(QuantRules.HashValue(run),QuantRules.HashValue(Run(input,sources)));Assert.Equal("Unknown",report.Gates.Single(g=>g.Name=="HistoricalMarketReplay").Status);
        Assert.Equal("Ready",report.Scenarios.Single(s=>s.Key=="Fifth").Actual);Assert.Equal("Cancelled",report.Scenarios.Single(s=>s.Key=="Sixth").Actual);
    }
    [Fact]public void FreezePreservesEveryFrameFieldActuallyConsumedByTheProductionTick()
    {
        var (input,sources)=Fixture();var f=QuantRehearsalTests.ReadyInput();var native=QuantLiveTests.Input(f,input.CapturedAt);
        Assert.Equal(QuantRules.HashValue(QuantPaperEngine.Tick(input.State,input.Trades,input.Funding,native)),Run(input,sources).Report.CopyHash);
    }
    [Fact]public void MissingSignalIsUnknownAndLatestBrokenSeedDoesNotFallBackToAnOlderWinner()
    {
        var (input,sources)=Fixture();var original=sources.Values.Single(s=>s.Review.Outcome=="Filled");var missing=original with{Signal=null};var hash=QuantRules.HashValue(missing);
        sources.Remove(QuantRules.HashValue(original));sources[hash]=missing;input=input with{Sources=input.Sources.Select(r=>r.Id==original.Review.Id?new(r.Id,hash):r).ToArray()};
        var report=Run(input,sources).Report;Assert.Equal(1,report.MissingEvidence);Assert.Equal(1,report.ScenariosUnknown);Assert.Equal(0,report.ScenariosPassed);Assert.Equal(original.Review.Id,report.ScenarioSeed);
    }
    [Fact]public void CorruptedSignalAndChangedRecordedPlanAreNotCountedAsReplayPasses()
    {
        var (input,sources)=Fixture();var original=sources.Values.Single(s=>s.Review.Outcome=="Filled");
        var modified=original with{Review=original.Review with{Selected=original.Review.Selected! with{Plan=original.Review.Selected.Plan! with{Quantity=original.Review.Selected.Plan.Quantity*2}}}};
        var altered=QuantPortfolioValidationEngine.Audit(modified);Assert.Equal("Fail",altered.Status);Assert.Contains(altered.Checks,c=>c.Key=="ApprovedQuantity"&&c.Status=="Fail");
        var bad=original with{Signal=original.Signal! with{Symbol="WRONGUSDT"}};Assert.Equal("Fail",QuantPortfolioValidationEngine.Audit(bad).Status);
    }
    [Fact]public void OtherPendingMarginAndFeesAreProtectedEvenWhenRawCashLooksSufficient()
    {
        var (input,sources)=Fixture();var report=Run(input,sources).Report;var blocked=report.Scenarios.Single(s=>s.Key=="ReservedCash");Assert.Equal("Cancelled",blocked.Actual);
        Assert.Contains("现金",blocked.Reason);Assert.Equal("Cancelled",report.Scenarios.Single(s=>s.Key=="PendingSlot").Actual);
        Assert.Equal("Waiting",report.Scenarios.Single(s=>s.Key=="MissingFunding").Actual);
    }
    [Fact]public void UnknownCorrelationConsumesBudgetAndDoesNotBecomeZeroRisk()
    {
        var (input,sources)=Fixture();var result=Run(input,sources).Report.Scenarios.Single(s=>s.Key=="UnknownCorrelation");Assert.Equal("Cancelled",result.Actual);Assert.Contains("风险",result.Reason);
    }
    [Fact]public void RejectedAndWaitingRecordsAreReplayedAndOtherPhasesRemainSeparate()
    {
        var (input,sources)=Fixture();var filled=sources.Values.Single(s=>s.Review.Outcome=="Filled").Review;
        var blockedEvidence=filled.Evidence with{Portfolio=filled.Evidence.Portfolio with{Equity=null}};
        var waiting=QuantEntryEngine.Evaluate(filled.Intent,filled.Control,blockedEvidence,filled.At,filled.Revision);
        var audit=QuantPortfolioValidationEngine.Audit(new(waiting,sources.Values.First().Signal));Assert.Equal("Pass",audit.Status);Assert.Equal("Waiting",audit.Outcome);
        var control=filled with{Phase="UserControl",Outcome="Cancelled",Selected=null,Proposed=null};audit=QuantPortfolioValidationEngine.Audit(new(control,null));Assert.Equal("NotApplicable",audit.Status);
    }
    [Fact]public void EntireReviewCountIsIndependentOfHundredRowDisplayLimit()
    {
        var (input,sources)=Fixture();var seed=sources.Values.First();var all=Enumerable.Range(0,105).Select(i=>seed with{Review=seed.Review with{Id=QuantRules.HashValue(i),Phase="Funding",Outcome="Waiting",Selected=null,Proposed=null}}).ToArray();
        sources=all.ToDictionary(QuantRules.HashValue);input=input with{Sources=all.Select(s=>new QuantPortfolioSourceRef(s.Review.Id,QuantRules.HashValue(s))).ToArray()};
        var report=Run(input,sources).Report;Assert.Equal(105,report.SourceReviews);Assert.Equal(105,report.NotReplayed);Assert.Equal(100,report.Rows.Count);Assert.True(report.RowsLimited);Assert.Equal(0,report.Reproduced);
    }
    [Fact]public void ContentAddressScopeAndFutureRevisionsAreEnforced()
    {
        var (input,sources)=Fixture();Assert.Throws<InvalidDataException>(()=>Run(input with{Sources=input.Sources.Concat(input.Sources.Take(1)).ToArray()},sources));
        Assert.Throws<InvalidDataException>(()=>Run(input with{Date="2000-01-01"},sources));
        Assert.Throws<InvalidDataException>(()=>Run(input with{State=input.State with{Revision=1}},sources));
        var wrong=sources.ToDictionary(p=>p.Key,p=>p.Value with{Signal=null});Assert.Throws<InvalidDataException>(()=>Run(input,wrong));
    }
    [Fact]public void EarlyEntryWithoutReviewRemainsUnlinkedInsteadOfBeingBackfilled()
    {
        var (input,sources)=Fixture();var p=input.State.Positions[0];input=input with{State=input.State with{Positions=[p with{Quant=p.Quant! with{EntryReviewId=null,EntryImplementationVersion=null}}]}};
        var report=Run(input,sources).Report;Assert.Equal(1,report.UnlinkedEntries);Assert.Equal(0,report.LinkedEntries);Assert.Equal("Unknown",report.Gates.Single(g=>g.Name=="EntryCoverage").Status);
    }
    [Fact]public void ArchiveIsImmutableSelfContainedAcrossRestartAndRejectsMissingOrChangedSources()
    {
        var (input,sources)=Fixture();var repo=new QuantPortfolioValidationRepository(Options.Create(new StorageOptions{Directory=directory}));
        foreach(var source in sources.Values)Assert.Equal(QuantRules.HashValue(source),repo.SaveSource(source));
        var run=Run(input,sources);repo.Save(run);repo.Save(run);
        var restarted=new QuantPortfolioValidationRepository(Options.Create(new StorageOptions{Directory=directory}));Assert.Equal(QuantRules.HashValue(run),QuantRules.HashValue(restarted.Read(input.Id)));Assert.Equal(input.Id,restarted.Latest()!.Id);
        Assert.Throws<InvalidDataException>(()=>repo.Save(run with{Report=run.Report with{Reproduced=999}}));Assert.Throws<ArgumentException>(()=>repo.ReadSource("../bad"));
        var latest=Path.Combine(directory,"QuantPortfolioValidation","latest.json.gz");var saved=File.ReadAllBytes(latest);
        using(var file=File.Create(latest))using(var zip=new GZipStream(file,CompressionLevel.Optimal))JsonSerializer.Serialize(zip,run.Report with{Reproduced=999});
        Assert.Throws<InvalidDataException>(()=>restarted.Latest());File.WriteAllBytes(latest,saved);
        var path=Path.Combine(directory,"QuantPortfolioValidation","Sources",input.Sources[0].Hash+".json.gz");File.WriteAllBytes(path,[1,2,3]);
        Assert.Throws<InvalidDataException>(()=>restarted.Read(input.Id));
    }
    [Fact]public void SqliteReviewIndexDoesNotSilentlyStopAtFiveHundredSummaries()
    {
        var db=new SqliteDatabase(Options.Create(new StorageOptions{Directory=directory}));db.Initialize();var at=new DateTimeOffset(2026,11,1,12,0,0,TimeSpan.FromHours(8));
        using(var c=db.Open())using(var tx=c.BeginTransaction())
        {for(var i=0;i<601;i++)c.Execute("INSERT INTO PaperEntryReview VALUES(@id,@at,@revision,'x',@empty,@empty)",new{id=QuantRules.HashValue(i),at=at.ToUnixTimeMilliseconds(),revision=i,empty=new byte[]{0}},tx);tx.Commit();}
        Assert.Equal(601,new PaperRepository(db).EntryReviewIds(at.AddMinutes(-1),at.AddMinutes(1)).Count);
    }
    private static (PaperState State,QuantPaperInput Input) FivePositions()
    {
        var (longState,longF)=QuantLiveTests.Open();var (shortState,shortF)=QuantLiveTests.Open(true);var at=longState.UpdatedAt.AddMinutes(12);
        var positions=new List<PaperPosition>();var markets=new List<MarketView>();var execution=new Dictionary<string,ExecutionPlanningInputs>();var funding=new Dictionary<string,FundingRateWindow?>();
        for(var i=0;i<5;i++)
        {
            var original=(i<3?longState:shortState).Positions[0];var symbol="SYNTHETIC"+i+"USDT";var source=i<3?longF:shortF;var input=QuantLiveTests.Input(source,at,original.EntryPrice);
            var d=original.Intent.QuantIntent!.Decision;var intent=original.Intent with{Key="synthetic-"+i,AssetId=i+1,Symbol=symbol,QuantIntent=original.Intent.QuantIntent with{Decision=d with{Symbol=symbol,Market=d.Market with{Symbol=symbol,AssetId=i+1}}}};
            var p=original with{Id=Guid.NewGuid(),SignalKey=intent.Key,AssetId=i+1,Symbol=symbol,Intent=intent,Bucket=i+1,LastMark=original.EntryPrice,LastQuoteTime=at.AddSeconds(-2),LastMarkSourceTime=at.AddSeconds(-2),Quant=original.Quant! with{EntryQuote=original.Quant.EntryQuote with{Symbol=symbol}}};positions.Add(p);
            var m=input.Markets[0];markets.Add(m with{Instrument=m.Instrument with{AssetId=i+1,Symbol=symbol}});
            var e=input.Execution[d.Symbol];execution[symbol]=e with{Rules=e.Rules! with{Symbol=symbol},Book=e.Book! with{Symbol=symbol}};
            funding[symbol]=new(symbol,p.OpenedAt,at.AddMinutes(-2),at,true,[]);
        }
        var state=longState with{Positions=positions,Cash=1000-positions.Sum(p=>p.InitialMargin+p.OpenFee),UpdatedAt=at.AddSeconds(-1),QuantFunding=positions.Select(p=>new QuantFundingCoverage(p.Id,p.Symbol,"ObservedWindow",at.AddMinutes(-10),at.AddSeconds(-1),"Synthetic engineering fixture")).ToArray()};
        return(state,new(at,markets,null,new Dictionary<string,QuantFrame>(),execution,funding,new Dictionary<string,QuantInputBundle>()));
    }
    [Fact]public void FiveActualModelPositionsKeepCashMarginFeesAndBothDirectionsConservedInOneTick()
    {
        var (state,input)=FivePositions();var original=QuantRules.HashValue(state);var result=QuantPaperEngine.Tick(state,[],[],input);
        Assert.Equal(5,result.State.Positions.Count);Assert.Equal(3,result.State.Positions.Count(p=>p.Direction=="Long"));Assert.Empty(result.Trades);Assert.Empty(result.FundingEvents);
        Assert.True(Math.Abs(state.Cash+state.Positions.Sum(p=>p.RemainingMargin)-result.State.Cash-result.State.Positions.Sum(p=>p.RemainingMargin))<.00000001m);
        Assert.Equal(original,QuantRules.HashValue(state));var sample=PaperEquityRecorder.Capture(result.State,[],[],input.At);Assert.True(sample.Reconciled);Assert.NotNull(sample.Equity);
    }
    [Fact]public void FiveLongShortFundingAllocationsConserveCollateralAndReplayDoesNotChargeAgain()
    {
        var (state,input)=FivePositions();var windows=state.Positions.ToDictionary(p=>p.Symbol,p=>(FundingRateWindow?)new FundingRateWindow(p.Symbol,p.OpenedAt,input.At.AddMinutes(-2),input.At,true,
            [new(p.Symbol,p.OpenedAt.AddSeconds(30),.001m,100,"Regular",input.At)]));
        input=input with{Funding=windows};var batch=QuantPaperEngine.Tick(state,[],[],input);Assert.Equal(5,batch.FundingEvents.Count);
        Assert.Equal(3,batch.FundingEvents.Count(f=>f.OpenAllocated>0));Assert.Equal(2,batch.FundingEvents.Count(f=>f.OpenAllocated<0));Assert.Equal(state.Cash,batch.State.Cash);
        var expected=state.Cash+state.Positions.Sum(p=>p.RemainingMargin)-batch.FundingEvents.Sum(f=>f.Evidence.SignedCost);
        Assert.True(Math.Abs(expected-batch.State.Cash-batch.State.Positions.Sum(p=>p.RemainingMargin))<.00000001m);
        var repeated=QuantPaperEngine.Tick(batch.State,[],batch.FundingEvents,input);Assert.Empty(repeated.FundingEvents);Assert.Equal(batch.State.Cash,repeated.State.Cash);
        Assert.True(PaperEquityRecorder.Capture(batch.State,[],batch.FundingEvents,input.At).Reconciled);
    }
    [Fact]public void NoHistoricalReviewsDoesNotTurnACurrentSnapshotIntoHistoricalValidation()
    {
        var (input,sources)=Fixture();var report=Run(input with{Sources=[]},sources).Report;Assert.Equal(0,report.Reproduced);Assert.Equal(1,report.ScenariosUnknown);
        Assert.Equal("Unknown",report.Gates.Single(g=>g.Name=="AdmissionReplay").Status);Assert.True(report.UnlinkedEntries>0);
    }
    [Fact]public async Task GenerationUsesLocalFrozenSourcesAndNeverCallsProductionPersistence()
    {
        var (input,sources)=Fixture();var options=Options.Create(new StorageOptions{Directory=directory});var signalRepository=new QuantShadowRepository(options);
        foreach(var source in sources.Values)signalRepository.SaveInput(source.Review.Control.RulesHash,source.Signal!);
        var account=new FrozenAccount(input,sources.Values.Select(s=>s.Review).ToArray());var persistence=new ForbiddenPersistence();
        var paper=new PaperService(account,persistence,null!,null!,TimeProvider.System,null!,null!);
        var repo=new QuantPortfolioValidationRepository(options);var fixture=QuantRehearsalTests.ReadyInput();
        var service=new QuantPortfolioValidationService(paper,new FrozenCoordinator(QuantLiveTests.Input(fixture,input.CapturedAt)),account,signalRepository,repo);
        var original=QuantRules.HashValue(input.State);var report=await service.RunAsync(null,default);
        Assert.Equal(0,persistence.Calls);Assert.Equal(original,QuantRules.HashValue(account.Restore()));Assert.Equal(2,report.Reproduced);Assert.NotNull(repo.Read(report.Id));
        await Assert.ThrowsAsync<ArgumentException>(()=>service.RunAsync(PaperEngine.Day(input.CapturedAt).AddDays(1),default));Assert.Equal(0,persistence.Calls);
    }
    private sealed class FrozenAccount(QuantPortfolioFrozenInput input,IReadOnlyList<QuantEntryReview> reviews):IPaperRepository
    {
        public PaperState? Restore()=>input.State;public IReadOnlyList<PaperTrade> Trades()=>input.Trades;
        public IReadOnlyList<PaperHoldingSnapshot> HoldingHistory(Guid? positionId,int limit,long? before=null)=>[];public long HoldingSnapshotCount()=>0;
        public IReadOnlyList<string> EntryReviewIds(DateTimeOffset since,DateTimeOffset through)=>reviews.Select(r=>r.Id).ToArray();
        public QuantEntryReview? EntryReview(string id)=>reviews.SingleOrDefault(r=>r.Id==id);
    }
    private sealed class ForbiddenPersistence:IPaperPersistence
    {public int Calls;public Task SaveAsync(PaperBatch batch,CancellationToken ct){Calls++;throw new InvalidOperationException("The source ledger must remain read-only");}}
    private sealed class FrozenCoordinator(QuantPaperInput input):IQuantExecutionCoordinator
    {public Task<QuantPaperInput> CaptureAsync(PaperState state,IReadOnlyList<PaperTrade> trades,CancellationToken ct)=>Task.FromResult(input);}
    [Fact]public void FiveSimultaneousRiskExitsReleaseCollateralChargeFeesAndRetainNegativeCashDeficit()
    {
        var (state,input)=FivePositions();var markets=new List<MarketView>();var execution=new Dictionary<string,ExecutionPlanningInputs>();
        foreach(var p in state.Positions)
        {
            var value=p.Direction=="Long"?.1m:10000m;var m=input.Markets.Single(m=>m.Instrument.AssetId==p.AssetId);markets.Add(m with{Price=value,MarkPrice=value});
            var e=input.Execution[p.Symbol];execution[p.Symbol]=e with{Book=e.Book! with{Bids=[new(value-.01m,10000)],Asks=[new(value+.01m,10000)]}};
        }
        var batch=QuantPaperEngine.Tick(state,[],[],input with{Markets=markets,Execution=execution});Assert.Empty(batch.State.Positions);Assert.Equal(5,batch.Trades.Count);Assert.True(batch.State.Cash<0);Assert.True(batch.State.Enabled);
        var expected=state.Cash+state.Positions.Sum(p=>p.RemainingMargin)+batch.Trades.Sum(t=>t.GrossPnl-t.ExitQuote!.Fee!.Value);Assert.True(Math.Abs(expected-batch.State.Cash)<.00000001m);Assert.All(batch.Trades,t=>Assert.StartsWith("LiquidationRisk",t.Reason));
    }
    [Fact]public void MissingBooksDoNotHideFiveRiskPositionsOrInventExitCash()
    {
        var (state,input)=FivePositions();var markets=input.Markets.Select(m=>m with{Price=m.Instrument.AssetId<=3?.1m:10000m,MarkPrice=m.Instrument.AssetId<=3?.1m:10000m}).ToArray();
        var batch=QuantPaperEngine.Tick(state,[],[],input with{Markets=markets,Execution=input.Execution.ToDictionary(p=>p.Key,p=>p.Value with{Book=null})});
        Assert.Equal(5,batch.State.Positions.Count);Assert.Empty(batch.Trades);Assert.Equal(state.Cash,batch.State.Cash);Assert.All(batch.State.Positions,p=>Assert.Equal("LiquidationRisk",p.Quant!.PendingExit));
    }
    public void Dispose(){SqliteConnection.ClearAllPools();if(Directory.Exists(directory))Directory.Delete(directory,true);}
}
