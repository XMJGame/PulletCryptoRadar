using Microsoft.Extensions.Options;
using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;
namespace PulletCryptoRadar.IntegrationTests;

public sealed class QuantDailyTests
{
    private static readonly DateTimeOffset At=new(2026,11,1,4,0,30,TimeSpan.FromHours(8));
    private static readonly QuantLiveControl Control=QuantPaperEngine.Control(At.AddMinutes(-1),new(),new());
    private static readonly PaperState State=new(){UpdatedAt=At,QuantControl=Control};
    private static QuantHypothesis Decision(string id="one",string status="PriceCandidate")=>new(id,Control.RulesHash,"TESTUSDT","Breakout20","Long","Forward",At.AddSeconds(-30),At,At.AddSeconds(120),status,"fixture","input",
        new(1,"TESTUSDT","TEST",true,At.AddYears(-1),100,20_000_000,5,At,At,100,0,At),null,new("Unknown",null,null,0,null,null,null,"fixture"),95,"support",[],[new("Trend","趋势",null,"up",status=="PriceCandidate")]){InputArchived=true};
    private static QuantDecisionBatch Batch(params QuantHypothesis[] decisions)=>new(Guid.NewGuid(),Control.RulesHash,"Forward",At.AddSeconds(-30),At,"TESTUSDT","input","universe",new(At,At.AddSeconds(-30),"Mixed","Mixed","Mixed",20,20,50,At,At,"fixture"),decisions);
    private static QuantAssessment Assessment(QuantHypothesis d,string status,DateTimeOffset? at=null)=>new(d.Id,d.Symbol,d.Strategy,d.Direction,"Forward",at??At.AddSeconds(5),status,"reason-"+status,status=="ShadowReady"?new(2,1,100,95,50,.05m,110,120,"2R","4R",6,0,1,50,72):null,null,[]);
    private static QuantDailyReport Build(QuantDecisionBatch[]? batches=null,QuantAssessment[]? assessments=null,PaperAuditWindow? audit=null,PaperState? state=null,PaperTrade[]? trades=null,QuantFundingEvent[]? funding=null,DateTimeOffset? now=null,QuantUniverse[]? universes=null,IReadOnlyDictionary<Guid,IReadOnlyList<PaperHoldingSnapshot>>? histories=null,DateOnly? date=null)=>
        QuantDailyEngine.Build(date??new(2026,11,1),now??At.AddMinutes(20),state??State,trades??[],funding??[],universes??[],batches??[],assessments??[],audit??new([],false),histories??new Dictionary<Guid,IReadOnlyList<PaperHoldingSnapshot>>());
    [Fact]public void RepeatedAssessmentsDoNotInflateCandidatesAndLaterExpiryDoesNotEraseReadiness()
    {
        var d=Decision();var report=Build([Batch(d,d)],[Assessment(d,"AwaitData"),Assessment(d,"ShadowReady",At.AddSeconds(10)),Assessment(d,"Rejected",d.ExpiresAt.AddSeconds(1))]);
        Assert.Equal(1,report.Funnel.PriceCandidates);Assert.Equal(1,report.Funnel.EverReady);Assert.Equal(0,report.Funnel.RejectedOnly);Assert.Equal(2,Assert.Single(report.Candidates).AssessmentSamples);Assert.Equal(1,report.Funnel.ReadyWithoutObservedIntent);
    }
    [Fact]public void CandidatePartitionsConserveCountsWithoutCallingMissingEvidenceRejection()
    {
        var a=Decision("ready");var b=Decision("wait");var c=Decision("reject");var d=Decision("missing");
        var report=Build([Batch(a,b,c,d)],[Assessment(a,"ShadowReady"),Assessment(b,"AwaitData"),Assessment(c,"Rejected")]);var f=report.Funnel;
        Assert.Equal(4,f.PriceCandidates);Assert.Equal(1,f.EverReady);Assert.Equal(1,f.AwaitDataOnly);Assert.Equal(1,f.RejectedOnly);Assert.Equal(1,f.Unassessed);Assert.Equal(f.PriceCandidates,f.EverReady+f.AwaitDataOnly+f.RejectedOnly+f.Unassessed);
    }
    [Fact]public void PreviewPreActivationWrongHashAndUnarchivedJudgmentsAreExcluded()
    {
        var d=Decision();var report=Build([Batch(d with{Mode="CurrentPreview"},d with{CapturedAt=Control.ActivatedAt.AddSeconds(-1)},d with{RulesHash="other"},d with{InputArchived=false},Decision("no","NoSignal"))]);
        Assert.Equal(1,report.Funnel.Observations);Assert.Equal(0,report.Funnel.PriceCandidates);Assert.Equal(1,Assert.Single(report.FailedConditions).Count);
    }
    [Fact]public void ShanghaiMidnightAndEndExclusivePreventDoubleBooking()
    {
        var midnight=QuantDailyEngine.Start(new(2026,11,1));var d=Decision() with{CapturedAt=midnight,Id="start"};var end=d with{CapturedAt=midnight.AddDays(1),Id="end"};var s=State with{QuantControl=Control with{ActivatedAt=midnight.AddSeconds(-1)}};
        var report=Build([Batch(d,end)],state:s,now:midnight.AddDays(2));Assert.Equal(midnight,report.From);Assert.Equal(midnight.AddDays(1),report.Through);Assert.True(report.DayEnded);Assert.Equal(1,report.Funnel.Observations);
    }
    [Fact]public void AReadyPlanCanBeCancelledAndTheExecutionReasonIsNotHidden()
    {
        var d=Decision();var created=new PaperDecision(At.AddSeconds(10),d.Symbol,"新模型等待后续盘口","saved",d.Id);var cancelled=new PaperDecision(At.AddSeconds(20),d.Symbol,"取消新意向","风险扩大",QuantPaperEngine.Version+":"+d.Id);
        var report=Build([Batch(d)],[Assessment(d,"ShadowReady")],new([new(1,At.AddSeconds(20),[],[created,cancelled])],false));
        Assert.Equal(1,report.Funnel.EverReady);Assert.Equal(1,report.Funnel.ObservedIntents);Assert.Equal(1,report.Funnel.Cancellations);Assert.Equal(0,report.Funnel.Openings);Assert.Contains(report.Reasons,r=>r.Name=="风险扩大");Assert.Contains(report.ExecutionEvents,e=>e.Action=="取消新意向");
    }
    [Fact]public void MissingUniverseAndIncompleteFreezeRemainVisibleInsteadOfZeroOpportunity()
    {
        var universe=new QuantUniverse(Guid.NewGuid(),At.AddSeconds(-30),At,["TESTUSDT","OTHERUSDT"]);var report=Build([Batch(Decision())],universes:[universe],now:At.AddHours(1).AddMinutes(20));
        Assert.Equal("Incomplete",report.Hours[0].Status);Assert.Equal(2,report.Hours[0].ExpectedSymbols);Assert.Equal("MissingUniverse",report.Hours[1].Status);
    }
    [Fact]public void TruncatedAuditIsFlaggedAndFilledIntentRemainsObservableWithoutDoubleCount()
    {
        var (state,f)=QuantLiveTests.Open();var p=state.Positions[0];var at=state.UpdatedAt;var date=DateOnly.FromDateTime(at.ToOffset(TimeSpan.FromHours(8)).DateTime);
        var ev=new PaperDecision(p.Intent.CreatedAt,p.Symbol,"新模型等待后续盘口","saved",p.Intent.QuantIntent!.Decision.Id);
        var report=Build(state:state,now:at,audit:new([new(1,at,[],[ev,ev])],true),date:date);
        Assert.True(report.AuditTruncated);Assert.Equal(1,report.Funnel.ObservedIntents);Assert.Equal(1,report.Funnel.Openings);Assert.True(Assert.Single(report.Paths).SubsequentBook);Assert.Equal(p.Quant!.Plan.EstimatedRisk,report.Paths[0].PlannedRisk);
    }
    [Fact]public void LateFundingChangesBookingDayNetWithoutRewritingOriginalExitOrDoubleChargingEntryFee()
    {
        var (state,f)=QuantLiveTests.Open();var p=state.Positions[0];var at=state.UpdatedAt;var date=DateOnly.FromDateTime(at.ToOffset(TimeSpan.FromHours(8)).DateTime);
        var trade=new PaperTrade(Guid.NewGuid(),p.SignalKey,p.AssetId,p.Symbol,p.OpenedAt,at.AddMinutes(1),p.EntryPrice,p.EntryPrice+1,p.Quantity,p.InitialMargin,p.Quant!.Plan.Leverage,p.Quantity,.2m,.1m,p.Quantity-.3m,"Stop",false,QuantPaperEngine.Version,p.Intent){PositionId=p.Id,Quant=p.Quant};
        var fund=new QuantFundingEvent("unique",at.AddMinutes(2),0,.4m,new("actual",p.Id,p.Symbol,p.Direction,at,"Observed",p.Quantity,.001m,100,.4m,at));
        var report=Build(state:state with{Positions=[]},trades:[trade,trade],funding:[fund,fund,fund with{Key="foreign",Evidence=fund.Evidence with{PositionId=Guid.NewGuid()}}],now:at.AddMinutes(3),date:date);
        Assert.Equal(trade.NetPnl-.4m,report.Costs.RealizedNet);Assert.Equal(trade.GrossPnl-report.Costs.Fees-report.Costs.SettledFunding-report.Costs.LateFundingAdjustment,report.Costs.RealizedNet);Assert.Equal(.4m,report.Costs.FundingBooked);Assert.Equal(trade.NetPnl,trade.GrossPnl-trade.Fees-trade.FundingCost);Assert.Equal(1,report.Funnel.Openings);
    }
    [Theory][InlineData(false)][InlineData(true)]public void SampledPathRespectsDirectionAndExcludesStaleOrFutureQuotes(bool shortSide)
    {
        var (state,f)=QuantLiveTests.Open(shortSide);var p=state.Positions[0];var at=state.UpdatedAt;var market=f.Markets[0] with{Price=p.EntryPrice*(shortSide?.9m:1.1m),PriceSourceTime=at,PriceReceivedTime=at};
        var good=new PaperHoldingSnapshot(p.Id,at,state.Revision,QuantPaperEngine.Version,"open",p,market,true,0,null,[]);
        IReadOnlyList<PaperHoldingSnapshot> samples=[good,good with{Market=market with{Price=10000},DataFresh=false},good with{Market=market with{Price=10000,PriceSourceTime=at.AddHours(1)}}];
        var report=Build(state:state,now:at,date:DateOnly.FromDateTime(at.ToOffset(TimeSpan.FromHours(8)).DateTime),histories:new Dictionary<Guid,IReadOnlyList<PaperHoldingSnapshot>>{{p.Id,samples}});
        var path=Assert.Single(report.Paths);Assert.Equal(1,path.FreshSamples);Assert.InRange(path.SampledBestPricePercent!.Value,9.999m,10.001m);Assert.Equal(0,path.SampledWorstPricePercent);
    }
    [Fact]public void AssessmentArchiveReadsOnlyRequestedTimeAndSurvivesRestart()
    {
        using var temp=new Temp();var options=Options.Create(new StorageOptions{Directory=temp.Path});var repo=new QuantShadowRepository(options);var d=Decision();
        repo.SaveAssessment(Control.RulesHash,Assessment(d,"AwaitData",At.AddMinutes(-1)));repo.SaveAssessment(Control.RulesHash,Assessment(d,"ShadowReady",At));repo.SaveAssessment(Control.RulesHash,Assessment(d,"Rejected",At.AddMinutes(1)));
        var rows=new QuantShadowRepository(options).ReadAssessments(Control.RulesHash,At,At.AddSeconds(1));Assert.Equal("ShadowReady",Assert.Single(rows).Status);
    }
    [Fact]public void DailyArchiveIsSeparatePerActivationAndRejectsMismatchedReportIdentity()
    {
        using var temp=new Temp();var options=Options.Create(new StorageOptions{Directory=temp.Path});var report=Build();var repo=new QuantDailyRepository(options);repo.Save(report);
        var restarted=new QuantDailyRepository(options);Assert.Equal(QuantRules.HashValue(report),QuantRules.HashValue(restarted.Read(Control,new(2026,11,1))));Assert.Null(restarted.Read(Control with{ActivatedAt=At},new(2026,11,1)));
        Assert.Throws<InvalidDataException>(()=>repo.Save(report with{Version="other"}));
    }
    [Fact]public void CrossingMidnightSettlementsExcludeLegacyAndDoNotChangeAccountOrRebookOldEntryFees()
    {
        var (state,f)=QuantLiveTests.Open();var p=state.Positions[0];var date=DateOnly.FromDateTime(state.UpdatedAt.ToOffset(TimeSpan.FromHours(8)).DateTime).AddDays(1);var at=QuantDailyEngine.Start(date).AddHours(1);
        var trade=new PaperTrade(Guid.NewGuid(),p.SignalKey,p.AssetId,p.Symbol,p.OpenedAt,at,p.EntryPrice,p.EntryPrice+1,p.Quantity,p.InitialMargin,p.Quant!.Plan.Leverage,p.Quantity,.2m,.1m,p.Quantity-.3m,"Target",false,QuantPaperEngine.Version,p.Intent){PositionId=p.Id,Quant=p.Quant};
        var old=trade with{Id=Guid.NewGuid(),Version="Legacy",Quant=null};var before=QuantRules.HashValue(state);
        var report=Build(state:state with{Positions=[]},trades:[trade,old],date:date,now:at.AddSeconds(1));Assert.Equal(0,report.Funnel.Openings);Assert.Equal(1,report.Costs.SettlementLegs);Assert.Equal(0,report.Costs.EntryFeesPaid);Assert.Equal(trade.NetPnl,report.Costs.RealizedNet);Assert.Equal(before,QuantRules.HashValue(state));
    }
    [Fact]public void OpenLossIsSeparateFromZeroSettlementsAndBecomesUnknownWhenStale()
    {
        var (state,f)=QuantLiveTests.Open();var p=state.Positions[0];var at=state.UpdatedAt;var date=DateOnly.FromDateTime(at.ToOffset(TimeSpan.FromHours(8)).DateTime);
        p=p with{LastMark=p.EntryPrice-1,LastMarkSourceTime=at,LastQuoteTime=at,FundingCost=.01m};state=state with{Positions=[p]};
        var report=Build(state:state,now:at,date:date);var path=Assert.Single(report.Paths);Assert.Equal(0,report.Costs.RealizedNet);Assert.Equal(-p.Quantity,path.OpenGrossPnl);Assert.Equal(p.OpenFee,path.RemainingOpenFee);Assert.Equal(.01m,path.AllocatedOpenFunding);
        Assert.Null(Assert.Single(Build(state:state,now:at.AddMinutes(2),date:date).Paths).OpenGrossPnl);
    }
    [Fact]public void FourDirectionalJudgmentsAreOneSymbolHourAndLiquidityExclusionIsNotReportedAsAMissingBaseline()
    {
        var facts=QuantRehearsalTests.ReadyInput().Shadow!.Decisions[0].Facts;
        var gap=Decision("gap","Insufficient") with{Symbol="GAPUSDT",Reason="原始小时缺口"};
        var low=Decision("low","Insufficient") with{Symbol="LOWUSDT",Reason="原始成交额不足",Facts=facts,Market=Decision().Market with{Symbol="LOWUSDT",QuoteVolume24h=6_000_000}};
        var decisions=Enumerable.Range(0,4).SelectMany(i=>new[]{gap with{Id="gap"+i},low with{Id="low"+i}}).Append(gap with{Id="preview",Mode="CurrentPreview"}).ToArray();
        var report=Build([Batch(decisions)]);Assert.Equal(8,report.Funnel.Observations);Assert.Equal(2,report.InputIssues.Count);Assert.All(report.InputIssues,r=>Assert.Equal(4,r.Judgments));
        Assert.Contains(report.InputIssues,r=>r.Symbol=="GAPUSDT"&&r.Category=="Baseline"&&r.Reason==gap.Reason);
        Assert.Contains(report.InputIssues,r=>r.Symbol=="LOWUSDT"&&r.Category=="Liquidity"&&r.Reason==low.Reason);Assert.Equal(0,report.Funnel.PriceCandidates);
    }
    [Fact]public async Task ReportClockIsCapturedAfterTheAccountLockSoANewFundingCheckIsNotMislabelledAsFuture()
    {
        using var temp=new Temp();var (state,f)=QuantLiveTests.Open();var clock=new MutableClock{At=state.UpdatedAt};var updated=state.UpdatedAt.AddSeconds(2);var p=state.Positions[0];
        state=state with{UpdatedAt=updated,QuantFunding=[new(p.Id,p.Symbol,"ObservedWindow",updated,updated,"fixture")]};
        var repository=new AdvancingRepository(state,clock);var paper=new PaperService(repository,null!,null!,null!,clock,null!,null!);
        var options=Options.Create(new StorageOptions{Directory=temp.Path});var service=new QuantDailyService(paper,repository,new QuantShadowRepository(options),new QuantDailyRepository(options),clock);
        var report=(await service.GetAsync(null,default))!;Assert.True(report.GeneratedAt>=report.AccountUpdatedAt);
        Assert.Equal("Pass",Assert.Single(report.Paths).Lifecycle!.Checks.Single(c=>c.Key=="FundingCoverage").Status);
    }
    private sealed class MutableClock:TimeProvider {public DateTimeOffset At;public override DateTimeOffset GetUtcNow()=>At;}
    private sealed class AdvancingRepository(PaperState state,MutableClock clock):IPaperRepository
    {
        public PaperState? Restore(){clock.At=state.UpdatedAt;return state;}
        public IReadOnlyList<PaperTrade> Trades()=>[];
        public IReadOnlyList<PaperHoldingSnapshot> HoldingHistory(Guid? positionId,int limit,long? before=null)=>[];
        public long HoldingSnapshotCount()=>0;
    }
    private sealed class Temp:IDisposable
    {
        public string Path{get;}=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"Pullet-QuantDaily-"+Guid.NewGuid().ToString("N"));
        public void Dispose(){var full=System.IO.Path.GetFullPath(Path);if(!full.StartsWith(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()),StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException();if(Directory.Exists(full))Directory.Delete(full,true);}
    }
}
