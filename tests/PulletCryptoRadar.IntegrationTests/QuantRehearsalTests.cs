using System.Text.Json;
using Microsoft.Extensions.Options;
using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;
namespace PulletCryptoRadar.IntegrationTests;
public sealed class QuantRehearsalTests
{
    private static readonly DateTimeOffset Now=new(2026,11,1,0,0,30,TimeSpan.Zero);
    private static readonly DateTimeOffset Hour=Now.AddSeconds(-30);
    private static readonly QuantParameters Parameters=new();private static readonly ExecutionCostModel Costs=new();
    private static PaperState Baseline=>new(){Revision=3,StartedAt=Now.AddDays(-5),UpdatedAt=Now,Rules=StructuralPaperEngine.Rules(),Enabled=true,Cash=1000,PeakEquity=1000};
    private static QuantRehearsalLedger Empty=>QuantRehearsalLedgerEngine.Fork(Baseline,Now.AddMinutes(-1));
    private static QuantMarketInput Market(DateTimeOffset at)=>new(1,"TESTUSDT","TEST",true,Now.AddYears(-1),100,20_000_000,5,at.AddSeconds(-1),at,100,0,at.AddSeconds(-1)){MarkReceivedAt=at};
    private static QuantMarketContext Context(DateTimeOffset at)=>new(at,Hour,"Uptrend","Up","Up",100,522,55,at.AddSeconds(-1),at,"fixture");
    private static QuantPortfolio Portfolio(DateTimeOffset at,decimal cash=1000)=>new(3,at,1000,cash,1000,[]);
    private static ExecutionPlanningInputs Inputs(DateTimeOffset at,decimal price=100)=>new(new("TESTUSDT","TRADING",.01m,.01m,100000,.001m,.001m,10000,.001m,.001m,10000,1,.3m,at.AddSeconds(-1),null),
        new("TESTUSDT",at.ToUnixTimeMilliseconds(),at.AddMilliseconds(-20),at.AddMilliseconds(-10),at,[new(price-.01m,10000)],[new(price+.01m,10000)]),8);
    private static QuantHypothesis Candidate(string direction="Long")=>new("decision-"+direction,QuantRules.Hash(Parameters),"TESTUSDT","Breakout20",direction,"Forward",Hour,Now.AddSeconds(-20),Now.AddSeconds(100),"PriceCandidate","fixture","digest",Market(Now.AddSeconds(-20)),
        new(337,Hour,100,99,98,98.5m,84,Hour,100,98,97,1,1,2,5,99,95),new("Increasing",1,5,1,null,null,null,"fixture"),direction=="Long"?95:105,"structure",[],[]){InputArchived=true};
    private static QuantAssessment Intent(QuantHypothesis d)
    {
        var at=Now.AddSeconds(-10);return QuantPortfolioEngine.Assess(d,Market(at),Context(at),Portfolio(at),new Dictionary<string,QuantFrame>(),Inputs(at),at,Parameters,Costs);
    }
    private static QuantRehearsalOperation Open(string direction="Long",QuantRehearsalLedger? state=null,ExecutionPlanningInputs? inputs=null,QuantHypothesis? decision=null)
    {var d=decision??Candidate(direction);return QuantRehearsalLedgerEngine.Open(state??Empty,d,Intent(d),Market(Now),Context(Now),Portfolio(Now,(state??Empty).Cash),new Dictionary<string,QuantFrame>(),inputs??Inputs(Now),Now,Parameters,Costs);}
    private static FundingRateWindow Window(QuantLedgerPosition p,DateTimeOffset at,decimal rate=.001m)=>new(p.Symbol,p.OpenedAt,at.AddHours(2).AddMinutes(-2),at.AddHours(2),true,[new(p.Symbol,at,rate,100,"Regular",at.AddHours(1))]);
    [Theory][InlineData("Long")][InlineData("Short")]
    public void EntryUsesSubsequentBookActualLotGridsAndOnlyDebitsTheCopy(string direction)
    {
        var original=Empty;var hash=QuantRules.HashValue(original.Baseline);Assert.Equal("ShadowReady",Intent(Candidate(direction)).Status);var result=Open(direction,original);
        Assert.Equal("CopiedFill",result.Status);var p=Assert.Single(result.Ledger.Positions);Assert.Equal(0,p.Quantity%.001m);Assert.InRange(p.Leverage,1,5);Assert.InRange(p.PlannedRisk,.001m,2);
        Assert.Equal(original.Cash-p.Collateral-p.EntryFeeRemaining,result.Ledger.Cash);Assert.Equal(hash,QuantRules.HashValue(result.Ledger.Baseline));Assert.Equal(1000,original.Cash);QuantRehearsalLedgerEngine.Validate(result.Ledger);
    }
    [Fact]public void PreviewHistoryUnarchivedAndPreActivationSignalsNeverBecomeCopyEntries()
    {
        foreach(var d in new[]{Candidate() with{Mode="CurrentPreview"},Candidate() with{Mode="HistoricalPriceStudy"},Candidate() with{InputArchived=false},Candidate() with{CapturedAt=Now.AddMinutes(-2)}})
            Assert.Empty(Open(decision:d).Ledger.Positions);
    }
    [Fact]public void PlanGenerationBookCannotAlsoServeAsSubsequentFillAndExpiredSignalsAreRejected()
    {
        Assert.Equal("AwaitData",Open(inputs:Inputs(Now.AddSeconds(-10))).Status);
        Assert.Equal("Expired",Open(decision:Candidate() with{ExpiresAt=Now.AddSeconds(-1)}).Status);
        Assert.NotEqual("CopiedFill",Open(inputs:Inputs(Now) with{Book=null}).Status);
    }
    [Fact]public void ForgedOrIncompletePortfolioCannotHideOriginalPositionsPendingOrConsumedCash()
    {
        var state=Empty with{Baseline=Baseline with{Pending=[new("old",Guid.Empty,2,"OLDUSDT",Now,null)]}};state=state with{BaselineHash=QuantRules.HashValue(state.Baseline)};
        Assert.Equal("Rejected",Open(state:state).Status);Assert.Empty(Open(state:state).Ledger.Positions);
    }
    [Fact]public void RetryAndSerializedRestartNeverDuplicateEntry()
    {
        var first=Open().Ledger;var restored=JsonSerializer.Deserialize<QuantRehearsalLedger>(JsonSerializer.Serialize(first))!;var second=Open(state:restored);
        Assert.Equal("Duplicate",second.Status);Assert.Equal(QuantRules.HashValue(first),QuantRules.HashValue(second.Ledger));Assert.Single(second.Ledger.Fills);
    }
    [Fact]public void ExistingUnfilledIntentsReserveCashAsWellAsSlotsAndRisk()
    {
        var baseline=Baseline with{Cash=100,Pending=[new("old",Guid.Empty,2,"OLDUSDT",Now,null)]};var ledger=QuantRehearsalLedgerEngine.Fork(baseline,Now.AddMinutes(-1));var d=Candidate();
        var portfolio=Portfolio(Now,100) with{Reservations=[new("OLDUSDT","Long",6,100,true)]};
        var result=QuantRehearsalLedgerEngine.Open(ledger,d,Intent(d),Market(Now),Context(Now),portfolio,new Dictionary<string,QuantFrame>(),Inputs(Now),Now,Parameters,Costs);
        Assert.Equal("Rejected",result.Status);Assert.Contains("未成交意向",result.Reason);Assert.Empty(result.Ledger.Positions);Assert.Equal(100,result.Ledger.Cash);
    }
    [Theory][InlineData("Long")][InlineData("Short")]
    public void ThreeReductionsConserveQuantityCollateralAndFeesAcrossRestart(string direction)
    {
        var ledger=Open(direction).Ledger;var p=Assert.Single(ledger.Positions);var at=Now.AddSeconds(2);
        foreach(var fraction in new[]{.3m,.3m,1m})
        {
            var current=ledger.Positions[0];var requested=fraction==1?current.Quantity:p.InitialQuantity*fraction;
            var result=QuantRehearsalLedgerEngine.Reduce(ledger,p.Id,"reduce-"+at.ToUnixTimeSeconds(),requested,Inputs(at,103),at,Costs);Assert.Equal("CopiedReduction",result.Status);
            ledger=JsonSerializer.Deserialize<QuantRehearsalLedger>(JsonSerializer.Serialize(result.Ledger))!;QuantRehearsalLedgerEngine.Validate(ledger);at=at.AddSeconds(2);
        }
        Assert.Equal(0,ledger.Positions[0].Quantity);Assert.Equal(0,ledger.Positions[0].Collateral);Assert.Equal(p.InitialQuantity,ledger.Fills.Where(f=>f.Kind=="Reduce").Sum(f=>f.Quantity));
        Assert.Equal(ledger.Cash-1000,ledger.Fills.Where(f=>f.Kind=="Reduce").Sum(f=>f.NetPnl));
    }
    [Fact]public void ReduceRetriesAreIdempotentAndConflictingKeysAreRejected()
    {
        var ledger=Open().Ledger;var p=ledger.Positions[0];var at=Now.AddSeconds(2);var quantity=p.Quantity;
        var reduced=QuantRehearsalLedgerEngine.Reduce(ledger,p.Id,"exit",quantity,Inputs(at),at,Costs).Ledger;
        Assert.Equal("Duplicate",QuantRehearsalLedgerEngine.Reduce(reduced,p.Id,"exit",quantity,Inputs(at),at,Costs).Status);
        Assert.Throws<InvalidDataException>(()=>QuantRehearsalLedgerEngine.Reduce(reduced,p.Id,"exit",quantity/2,Inputs(at),at,Costs));
        Assert.Equal("AwaitData",QuantRehearsalLedgerEngine.Reduce(ledger,p.Id,"same-book",quantity,Inputs(Now),Now,Costs).Status);
    }
    [Theory][InlineData("Long",1)][InlineData("Short",-1)]
    public void ActualFundingHasCorrectSignAndSurvivesDeduplicationAndRestart(string direction,int sign)
    {
        var ledger=Open(direction).Ledger;var p=ledger.Positions[0];var at=Now.AddHours(1);var window=Window(p,at);
        var result=QuantRehearsalLedgerEngine.Funding(ledger,p.Id,window,at.AddHours(2));Assert.Equal("ObservedWindow",result.Audit.Status);
        Assert.Equal(p.Quantity*100*.001m*sign,Assert.Single(result.Ledger.Funding).Evidence.SignedCost);QuantRehearsalLedgerEngine.Validate(result.Ledger);
        var restored=JsonSerializer.Deserialize<QuantRehearsalLedger>(JsonSerializer.Serialize(result.Ledger))!;
        Assert.Equal(QuantRules.HashValue(restored),QuantRules.HashValue(QuantRehearsalLedgerEngine.Funding(restored,p.Id,window,at.AddHours(2)).Ledger));
        Assert.Equal("Unknown",QuantRehearsalLedgerEngine.Funding(ledger,p.Id,null,at.AddHours(2)).Audit.Status);
    }
    [Fact]public void LateFundingAllocatesTheAlreadyReducedPartToCashAndTheOpenPartToCollateralOnce()
    {
        var ledger=Open().Ledger;var p=ledger.Positions[0];var fundingAt=Now.AddHours(1);var exitAt=fundingAt.AddSeconds(2);
        ledger=QuantRehearsalLedgerEngine.Reduce(ledger,p.Id,"partial",p.Quantity/2,Inputs(exitAt),exitAt,Costs).Ledger;
        var before=ledger;var result=QuantRehearsalLedgerEngine.Funding(ledger,p.Id,Window(p,fundingAt),fundingAt.AddHours(2));var payment=Assert.Single(result.Ledger.Funding);
        Assert.True(payment.OpenAllocated>0&&payment.ClosedAllocated>0);Assert.Equal(payment.Evidence.SignedCost,payment.OpenAllocated+payment.ClosedAllocated);
        Assert.Equal(before.Cash-payment.ClosedAllocated,result.Ledger.Cash);Assert.Equal(before.Positions[0].Collateral-payment.OpenAllocated,result.Ledger.Positions[0].Collateral);QuantRehearsalLedgerEngine.Validate(result.Ledger);
    }
    [Fact]public void FundingObservedAfterFullCloseAdjustsCopyCashWithoutRewritingTheClosedFill()
    {
        var ledger=Open().Ledger;var p=ledger.Positions[0];var at=Now.AddHours(1);var exitAt=at.AddSeconds(2);
        ledger=QuantRehearsalLedgerEngine.Reduce(ledger,p.Id,"close",p.Quantity,Inputs(exitAt),exitAt,Costs).Ledger;var fillHash=QuantRules.HashValue(ledger.Fills);
        var result=QuantRehearsalLedgerEngine.Funding(ledger,p.Id,Window(p,at),at.AddHours(2));var payment=Assert.Single(result.Ledger.Funding);
        Assert.Equal(0,payment.OpenAllocated);Assert.Equal(ledger.Cash-payment.Evidence.SignedCost,result.Ledger.Cash);Assert.Equal(fillHash,QuantRules.HashValue(result.Ledger.Fills));
    }
    [Fact]public void SettlementAtTheSameTimestampAsReductionUsesPreReductionQuantity()
    {
        var ledger=Open().Ledger;var p=ledger.Positions[0];var at=Now.AddHours(1);
        ledger=QuantRehearsalLedgerEngine.Reduce(ledger,p.Id,"partial",p.Quantity/2,Inputs(at),at,Costs).Ledger;
        var result=QuantRehearsalLedgerEngine.Funding(ledger,p.Id,Window(p,at),at.AddHours(2));Assert.Equal(p.InitialQuantity,Assert.Single(result.Ledger.Funding).Evidence.Quantity);
    }
    [Fact]public void PartialFundingIsNotZeroCompleteAndChangedPublishedEventsCannotOverwritePayments()
    {
        var ledger=Open().Ledger;var p=ledger.Positions[0];var at=Now.AddHours(1);var window=Window(p,at) with{Complete=false};
        var partial=QuantRehearsalLedgerEngine.Funding(ledger,p.Id,window,at.AddHours(2));Assert.Equal("Partial",partial.Audit.Status);Assert.Null(partial.Audit.CompleteSignedCost);Assert.Single(partial.Ledger.Funding);
        var changed=window with{Records=[window.Records[0] with{Rate=.002m}]};Assert.Throws<InvalidDataException>(()=>QuantRehearsalLedgerEngine.Funding(partial.Ledger,p.Id,changed,at.AddHours(2)));
    }
    [Fact]public void GapLossBeyondAllCopyCashRemainsNegativeAndIsNeverClippedOrReplenished()
    {
        var ledger=Open("Short").Ledger;var p=ledger.Positions[0];var at=Now.AddSeconds(2);
        var result=QuantRehearsalLedgerEngine.Reduce(ledger,p.Id,"gap",p.Quantity,Inputs(at,10000),at,Costs);
        Assert.Equal("CopiedReduction",result.Status);Assert.True(result.Ledger.Cash<0);QuantRehearsalLedgerEngine.Validate(result.Ledger);Assert.Equal(1000,result.Ledger.Baseline.Cash);
    }
    private static QuantRehearsalInput Input()=>new(Guid.NewGuid(),Now,new(Baseline,1000,1000,0,0,0,0,0,0,null,"fixture",[]),[],null,null,[],new Dictionary<string,IReadOnlyList<Candle>>(),new Dictionary<string,ExecutionPlanningInputs>(),new Dictionary<string,FundingRateWindow?>(),Costs);
    internal static QuantRehearsalInput ReadyInput(bool shortSide=false)
    {
        var captured=Now.AddSeconds(-20);var bars=Enumerable.Range(0,337).Select(i=>{var end=Hour.AddHours(i-336);var close=100+i*.02m+(decimal)Math.Sin(i/3d)*.3m;
            return new Candle("TESTUSDT",60,end.AddHours(-1),close-.03m,close+.4m,close-.4m,close,500_000,end.AddMilliseconds(-1),end.AddMilliseconds(1));}).ToArray();
        bars[^1]=bars[^1] with{Open=106.7m,High=108.5m,Low=106.5m,Close=108.4m,QuoteVolume=2_000_000};
        if(shortSide)bars=bars.Select(c=>c with{Open=220-c.Open,High=220-c.Low,Low=220-c.High,Close=220-c.Close}).ToArray();
        DerivativeSample Point(int minutes,decimal qty)=>new("TESTUSDT",DerivativeKind.OpenInterest5m,Hour.AddMinutes(-5+minutes),captured.AddSeconds(-1),qty,999999);
        var points=new[]{Point(-1440,100),Point(-60,104),Point(0,105)};var bundle=new QuantInputBundle("TESTUSDT",Hour,bars,points);var hash=QuantRules.Hash(Parameters);var digest=QuantRules.HashValue(bundle);
        var price=shortSide?111.6m:108.4m;var direction=shortSide?"Short":"Long";
        QuantMarketInput QuoteAt(DateTimeOffset at)=>Market(at) with{Price=price,MarkPrice=price};
        var decisions=QuantSignalEngine.Analyze(QuoteAt(captured),QuantSignalEngine.Frame("TESTUSDT",bars,captured,Parameters),QuantSignalEngine.Oi("TESTUSDT",points,Hour,captured,Parameters),captured,"Forward",hash,digest,Parameters);
        var d=Assert.Single(decisions,x=>x.Status=="PriceCandidate"&&x.Strategy=="Breakout20"&&x.Direction==direction) with{InputArchived=true};
        var at=Now.AddSeconds(-10);var intent=QuantPortfolioEngine.Assess(d,QuoteAt(at),Context(at),Portfolio(at),new Dictionary<string,QuantFrame>(),Inputs(at,price),at,Parameters,Costs);Assert.Equal("ShadowReady",intent.Status);
        var runtime=new MarketRuntimeStore();runtime.Register([new(1,"TESTUSDT","TEST",Now.AddYears(-1),true)]);runtime.Apply(new TickerEvent("TESTUSDT",price,20_000_000,5,Now.AddSeconds(-1),Now));runtime.Apply(new MarkPriceEvent("TESTUSDT",price,0,Now.AddHours(8),Now.AddSeconds(-1),Now));
        return Input() with{Cohort=new(Guid.NewGuid(),Now.AddMinutes(-1),Parameters.Version,hash,Parameters),Shadow=new(Now,Hour,"ForwardWindow",hash,["TESTUSDT"],Context(Now),Portfolio(Now),[d],[intent]),Markets=runtime.GetAll(),Hourly=new Dictionary<string,IReadOnlyList<Candle>>{{"TESTUSDT",bars}},Execution=new Dictionary<string,ExecutionPlanningInputs>{{"TESTUSDT",Inputs(Now,price)}},ArchivedSignals=new Dictionary<string,QuantInputBundle>{{digest,bundle}}};
    }
    [Fact]public void CompleteArchivedForwardSignalReproducesAndOnlyCreatesOneDeterministicCopyFill()
    {
        var input=ReadyInput();var first=QuantRehearsalEngine.Run(input);Assert.Equal(1,first.Report.CopiedEntries);Assert.Single(first.Ledger.Fills);Assert.Equal("CopiedFill",Assert.Single(first.Report.Outcomes).Status);
        Assert.Equal(QuantRules.HashValue(first),QuantRules.HashValue(QuantRehearsalEngine.Run(input)));Assert.Equal(1000,input.Paper.State.Cash);
        var absent=QuantRehearsalEngine.Run(input with{ArchivedSignals=new Dictionary<string,QuantInputBundle>()});Assert.Equal(0,absent.Report.CopiedEntries);Assert.Equal("AwaitData",Assert.Single(absent.Report.Outcomes).Status);
    }
    [Fact]public void FrozenCopyRejectsFutureSnapshotsAndMixedCostVersions()
    {
        var input=ReadyInput();Assert.Throws<InvalidDataException>(()=>QuantRehearsalEngine.Run(input with{CapturedAt=Now.AddSeconds(-1)}));
        Assert.Throws<InvalidDataException>(()=>QuantRehearsalEngine.Run(input with{Costs=Costs with{FeeRate=.0006m}}));
    }
    [Fact]public void ExistingLegacyPositionStillExitsThroughItsOriginalEngineInAnIndependentCopy()
    {
        var intent=new PaperIntent("legacy",Guid.Empty,1,"TESTUSDT",Now.AddHours(-1),null);
        var p=new PaperPosition(Guid.NewGuid(),"legacy",1,"TESTUSDT",Now.AddHours(-1),100,.1m,1,1,.005m,0,Now.AddSeconds(-5),100,Now.AddSeconds(-5),95,110,intent)
            {InitialQuantity=.1m,InitialStop=95,InitialAtr=1,Target2=120,LastMarkSourceTime=Now.AddSeconds(-5)};
        var baseline=Baseline with{Cash=999,Positions=[p],Enabled=false};var hash=QuantRules.HashValue(baseline);var runtime=new MarketRuntimeStore();runtime.Register([new(1,"TESTUSDT","TEST",Now.AddYears(-1),true)]);
        runtime.Apply(new TickerEvent("TESTUSDT",94,20_000_000,-5,Now.AddSeconds(-1),Now));runtime.Apply(new MarkPriceEvent("TESTUSDT",94,0,Now.AddHours(8),Now.AddSeconds(-1),Now));
        var result=StructuralPaperEngine.Tick(baseline,[],runtime.GetAll().ToDictionary(m=>m.Instrument.AssetId),Now);Assert.Empty(result.State.Positions);Assert.Equal(StructuralPaperEngine.Version,Assert.Single(result.Trades).Version);Assert.Equal(hash,QuantRules.HashValue(baseline));Assert.Single(baseline.Positions);
    }
    [Fact]public void StatusReadsDoNotCreateAForkOrInvokeAnyMarketOrPaperDependency()
    {
        var root=Path.Combine(Path.GetTempPath(),"Pullet-Rehearsal-"+Guid.NewGuid().ToString("N"));var repo=new QuantRehearsalRepository(Options.Create(new StorageOptions{Directory=root}));
        var service=new QuantRehearsalService(null!,null!,null!,null!,null!,null!,repo,null!,TimeProvider.System);Assert.Null(service.Status());Assert.False(Directory.Exists(root));
    }
    [Fact]public void ZeroIntentRehearsalIsDeterministicAndNeverClaimsAnEntryOrLiveMigrationPass()
    {
        var input=Input();var first=QuantRehearsalEngine.Run(input);var second=QuantRehearsalEngine.Run(input);Assert.Equal(QuantRules.HashValue(first),QuantRules.HashValue(second));
        Assert.Equal(1000,first.Ledger.Cash);Assert.Empty(first.Ledger.Fills);Assert.Contains(first.Report.Checks,c=>c.Name=="EntryReplay"&&c.Status=="Unknown");Assert.Contains(first.Report.Checks,c=>c.Name=="LiveMigration"&&c.Status=="Unknown");
    }
    [Fact]public void ArchiveIsImmutableReplaysAfterRestartAndRejectsAlteredBalances()
    {
        var root=Path.Combine(Path.GetTempPath(),"Pullet-Rehearsal-"+Guid.NewGuid().ToString("N"));
        try
        {
            var repo=new QuantRehearsalRepository(Options.Create(new StorageOptions{Directory=root}));var run=QuantRehearsalEngine.Run(ReadyInput());repo.Save(run);repo.Save(run);
            Assert.Equal(QuantRules.HashValue(run),QuantRules.HashValue(new QuantRehearsalRepository(Options.Create(new StorageOptions{Directory=root})).Latest()));
            Assert.Throws<InvalidDataException>(()=>repo.Save(run with{Ledger=run.Ledger with{Cash=run.Ledger.Cash+1}}));Assert.Equal(run.Ledger.Cash,repo.Latest()!.Ledger.Cash);
        }
        finally{var full=Path.GetFullPath(root);if(!full.StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException();if(Directory.Exists(full))Directory.Delete(full,true);}
    }
}
