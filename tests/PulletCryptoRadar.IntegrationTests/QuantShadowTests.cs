using Microsoft.Extensions.Options;
using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;
namespace PulletCryptoRadar.IntegrationTests;
public sealed class QuantShadowTests
{
    private static readonly DateTimeOffset Now=new(2026,10,31,16,0,30,TimeSpan.Zero);
    private static readonly DateTimeOffset Hour=Now.AddSeconds(-30);
    private static readonly QuantParameters P=new();private static readonly string Hash=QuantRules.Hash(P);
    private static Candle[] Bars(string symbol="TESTUSDT",string shape="Breakout",bool shortSide=false)
    {
        var rows=Enumerable.Range(0,337).Select(i=>
        {
            var end=Hour.AddHours(i-336);var c=100+i*.02m+(decimal)Math.Sin(i/3d)*.3m;
            return new Candle(symbol,60,end.AddHours(-1),c-.03m,c+.4m,c-.4m,c,100,end.AddMilliseconds(-1),end.AddMilliseconds(1));
        }).ToArray();
        var last=shape=="Pullback"?107.2m:108.4m;rows[^1]=rows[^1] with{Open=106.7m,High=last+.1m,Low=106.5m,Close=last,QuoteVolume=200};
        if(shortSide)rows=rows.Select(c=>c with{Open=220-c.Open,High=220-c.Low,Low=220-c.High,Close=220-c.Close}).ToArray();return rows;
    }
    private static QuantMarketInput Market(string symbol="TESTUSDT",decimal price=100)=>new(1,symbol,"TEST",true,Now.AddYears(-1),price,20_000_000,5,Now.AddSeconds(-1),Now,price,0,Now.AddSeconds(-1)){MarkReceivedAt=Now};
    private static QuantMarketContext Context=>new(Now,Hour,"Uptrend","Up","Up",100,522,55,Now.AddSeconds(-1),Now,"fixture");
    private static QuantPortfolio Portfolio=>new(3,Now,1000,1000,1000,[]);
    private static QuantOiEvidence Oi=>new("Increasing",1,5,1,null,null,null,"fixture");
    private static QuantFacts Facts=>new(337,Hour,100,99,98,98.5m,84,Hour,100,98,97,1,1,2,5,99,95);
    private static QuantHypothesis Candidate(string direction="Long",string mode="CurrentPreview",decimal? stop=null)=>new("fixture",Hash,"TESTUSDT","Breakout20",direction,mode,Hour,Now.AddSeconds(-10),Now.AddSeconds(110),"PriceCandidate","fixture","input",Market(),Facts,Oi,stop??(direction=="Long"?95:105),"structure",[],[]){InputArchived=true};
    private static ExecutionPlanningInputs Inputs=>new(new("TESTUSDT","TRADING",.01m,.01m,100000,.001m,.001m,10000,.001m,.001m,10000,1,.3m,Now,null),
        new("TESTUSDT",1,Now.AddMilliseconds(-20),Now.AddMilliseconds(-10),Now,[new(99.99m,10000)],[new(100.01m,10000)]),8);
    private static QuantAssessment Assess(QuantHypothesis? d=null,QuantPortfolio? portfolio=null,QuantMarketContext? context=null,QuantMarketInput? market=null,ExecutionPlanningInputs? inputs=null,IReadOnlyDictionary<string,QuantFrame>? frames=null)=>
        QuantPortfolioEngine.Assess(d??Candidate(),market??Market(),context??Context,portfolio??Portfolio,frames??new Dictionary<string,QuantFrame>(),inputs??Inputs,Now,P,new());

    [Theory][InlineData(false)][InlineData(true)]public void BreakoutUsesPreviousRangeAndMirrorsDirection(bool shortSide)
    {
        var bars=Bars(shortSide:shortSide);var frame=QuantSignalEngine.Frame("TESTUSDT",bars,Now,P);Assert.Null(frame.Issue);Assert.Equal(337,frame.Facts!.Hours);Assert.Equal(Hour,frame.Facts.FourHourEnd);
        var rows=QuantSignalEngine.Analyze(Market(price:bars[^1].Close),frame,Oi,Now,"CurrentPreview",Hash,"input",P);
        var d=Assert.Single(rows,d=>d.Strategy=="Breakout20"&&d.Status=="PriceCandidate");Assert.Equal(shortSide?"Short":"Long",d.Direction);
        Assert.NotEqual(bars[^1].High,frame.Facts.Prior20High);Assert.True(d.Conditions.All(c=>c.Passed));Assert.True(shortSide?d.Stop>bars[^1].Close:d.Stop<bars[^1].Close);
    }
    [Theory][InlineData(false)][InlineData(true)]public void PullbackRequiresConfirmedStructureAndMirrorsDirection(bool shortSide)
    {
        var bars=Bars(shape:"Pullback",shortSide:shortSide);var frame=QuantSignalEngine.Frame("TESTUSDT",bars,Now,P);
        var d=QuantSignalEngine.Analyze(Market(price:bars[^1].Close),frame,Oi,Now,"CurrentPreview",Hash,"input",P).Single(d=>d.Strategy=="TrendPullback"&&d.Direction==(shortSide?"Short":"Long"));
        Assert.Equal("PriceCandidate",d.Status);Assert.All(d.Levels,p=>Assert.True(p.ConfirmedAt<=Hour));Assert.Contains("两根右侧",d.StopBasis);
    }
    [Fact]public void FutureForeignUnclosedAndConflictingBarsCannotManufactureBaseline()
    {
        var bars=Bars();var altered=bars.ToArray();altered[^1]=altered[^1] with{ReceivedTime=Now.AddSeconds(1)};
        Assert.NotNull(QuantSignalEngine.Frame("TESTUSDT",altered,Now,P).Issue);
        Assert.NotNull(QuantSignalEngine.Frame("OTHERUSDT",bars,Now,P).Issue);
        Assert.NotNull(QuantSignalEngine.Frame("TESTUSDT",bars.Skip(1).Append(bars[^1] with{OpenTime=Hour}).ToArray(),Now,P).Issue);
        Assert.Contains("冲突",QuantSignalEngine.Frame("TESTUSDT",bars.Append(bars[^1] with{Close=108.41m}).ToArray(),Now,P).Issue);
        Assert.NotNull(QuantSignalEngine.Frame("TESTUSDT",bars.Where((_,i)=>i!=110).ToArray(),Now,P).Issue);
    }
    [Fact]public void PartialFourHourBarCannotEnterTrend()
    {
        var shifted=Now.AddHours(1);var bars=Bars().Select(c=>c with{OpenTime=c.OpenTime.AddHours(1),SourceTime=c.SourceTime.AddHours(1),ReceivedTime=c.ReceivedTime.AddHours(1)}).ToArray();
        var frame=QuantSignalEngine.Frame("TESTUSDT",bars,shifted,P);Assert.Null(frame.Issue);Assert.Equal(Hour,frame.Facts!.FourHourEnd);Assert.NotEqual(frame.Facts.Close,frame.Facts.FourHourClose);
    }
    private static DerivativeSample Point(int minutes,decimal qty,string symbol="TESTUSDT")=>new(symbol,DerivativeKind.OpenInterest5m,Hour.AddMinutes(-5+minutes),Now,qty,999999);
    [Fact]public void OiUsesQuantityAndRetainsUnavailableTwentyFourHourEvidence()
    {
        var result=QuantSignalEngine.Oi("TESTUSDT",[Point(-60,100),Point(0,105) with{NotionalUsd=50}],Hour,Now,P);
        Assert.Equal(5,result.Change1hPercent);Assert.Null(result.Change24hPercent);Assert.Equal(.5m,result.RiskScale);
        Assert.Equal("Unknown",QuantSignalEngine.Oi("TESTUSDT",[Point(-60,100),Point(0,105) with{ReceivedTime=Now.AddSeconds(1)}],Hour,Now,P).Status);
        Assert.Equal("Unknown",QuantSignalEngine.Oi("TESTUSDT",[Point(-60,100,"FAKE"),Point(0,105)],Hour,Now,P).Status);
        Assert.Equal("Unknown",QuantSignalEngine.Oi("TESTUSDT",[Point(-60,100),Point(0,105),Point(0,106)],Hour,Now,P).Status);
    }
    [Fact]public void OiExtremeAndStaleBaselinesAreNotCalledNormalAccumulation()
    {
        Assert.Equal("Extreme",QuantSignalEngine.Oi("TESTUSDT",[Point(-60,100),Point(0,116)],Hour,Now,P).Status);
        Assert.Equal("Unknown",QuantSignalEngine.Oi("TESTUSDT",[Point(-75,100),Point(0,105)],Hour,Now,P).Status);
        Assert.Equal("Unknown",QuantSignalEngine.Oi("TESTUSDT",[Point(-80,100),Point(-20,105)],Hour,Now,P).Status);
    }
    [Theory][InlineData("Long")][InlineData("Short")]public void PlanIncludesCostsAndObeysHalfRiskMarginAndLotGrids(string direction)
    {
        var result=Assess(Candidate(direction));Assert.Equal("ShadowReady",result.Status);var plan=Assert.IsType<QuantPlan>(result.Plan);
        Assert.InRange(plan.EstimatedRisk,0.001m,2);Assert.InRange(plan.Margin,0.001m,100);Assert.Equal(0,plan.Quantity%.001m);
        Assert.True(plan.EstimatedRisk>result.Quote!.PlannedLossIncludingCosts);Assert.True(direction=="Long"?plan.Target2>plan.Target1&&plan.Target1>plan.Entry:plan.Target2<plan.Target1&&plan.Target1<plan.Entry);
        Assert.Equal("2R风险投影",plan.Target1Basis);Assert.Equal(5,plan.Leverage);Assert.Equal(0,plan.Target1%.01m);Assert.Equal(0,plan.Target2%.01m);
    }
    [Theory][InlineData("Long",70)][InlineData("Short",124)]public void WideStructureReducesLeverageRatherThanMovingStop(string side,int stop)
    {var result=Assess(Candidate(side,stop:stop));Assert.Equal("ShadowReady",result.Status);Assert.Equal(3,result.Plan!.Leverage);Assert.Equal(stop,result.Plan.Stop);}
    [Theory][InlineData("Unknown")][InlineData("Pressure")]public void MissingOrStressedEnvironmentCannotPassRisk(string state)
    {Assert.Null(Assess(context:Context with{State=state}).Plan);}
    [Fact]public void OriginalPositionsPendingAndUnknownCorrelationsConsumeCapacityAndRisk()
    {
        var held=new QuantReservation("OLDUSDT","Short",12,80,false);var p=Portfolio with{Reservations=[held]};
        var result=Assess(portfolio:p);Assert.Equal("Rejected",result.Status);Assert.Contains("相关",result.Reason);
        Assert.Equal("Rejected",Assess(portfolio:Portfolio with{Reservations=[held with{Symbol="TESTUSDT",Pending=true}]}).Status);
        Assert.Equal("Rejected",Assess(portfolio:Portfolio with{Reservations=Enumerable.Range(0,5).Select(i=>held with{Symbol="S"+i,PlannedRisk=.1m}).ToArray()}).Status);
        Assert.Equal("AwaitData",Assess(portfolio:Portfolio with{Equity=null}).Status);
    }
    [Fact]public void CorrelationRequiresAlignedReturnsAndTreatsConstantSeriesAsUnknown()
    {
        var a=QuantSignalEngine.Frame("TESTUSDT",Bars(),Now,P);var b=a with{Symbol="OTHER"};Assert.InRange(QuantPortfolioEngine.Correlation(a,b)!.Value,.999m,1);
        Assert.Null(QuantPortfolioEngine.Correlation(a,null));Assert.Null(QuantPortfolioEngine.Correlation(a,b with{Hour=Hour.AddHours(-1)}));
        var flat=a with{Bars=a.Bars.Select(c=>c with{Close=100}).ToArray()};Assert.Null(QuantPortfolioEngine.Correlation(a,flat));
    }
    [Fact]public void ForwardRequiresQuotesStrictlyAfterCaptureAndExpiresWithoutBackdatedFill()
    {
        var d=Candidate(mode:"Forward") with{CapturedAt=Now};Assert.Equal("AwaitData",Assess(d).Status);
        Assert.Equal("Expired",Assess(d with{Hour=Hour.AddHours(-1),CapturedAt=Now.AddMinutes(-4),ExpiresAt=Now.AddSeconds(-1)}).Status);
        Assert.Equal("AwaitData",Assess(Candidate(mode:"AwaitBaseline")).Status);
        Assert.Equal("Rejected",Assess(Candidate() with{RulesHash="changed"}).Status);
        Assert.Equal("AwaitData",Assess(Candidate(mode:"Forward"),market:Market() with{MarkReceivedAt=Now.AddSeconds(-10)}).Status);
        Assert.Equal("AwaitData",Assess(Candidate(mode:"Forward") with{InputArchived=false}).Status);
        Assert.Equal("ShadowReady",Assess(Candidate(mode:"Forward")).Status);
        Assert.Equal("AwaitData",Assess(market:Market() with{MarkReceivedAt=null}).Status);
    }
    [Fact]public void MissingBookAndTinyMinimumNotionalNeverRoundRiskUpward()
    {
        Assert.Equal("AwaitData",Assess(inputs:Inputs with{Book=null}).Status);
        Assert.Null(Assess(inputs:Inputs with{Rules=Inputs.Rules! with{MinimumNotional=1000}}).Plan);
        Assert.Null(Assess(market:Market() with{FundingRate=.002m}).Plan);
        Assert.Null(Assess(market:Market(price:102)).Plan);
        Assert.Null(Assess(Candidate() with{Oi=Oi with{Status="Extreme",RiskScale=0}}).Plan);
    }
    [Fact]public void DrawdownScalesDownWithoutAnEightHundredDollarStopOrCashReplenishment()
    {
        var p=Portfolio with{Equity=790,Cash=790};var r=Assess(portfolio:p);Assert.Equal("ShadowReady",r.Status);Assert.InRange(r.Plan!.EstimatedRisk,0.001m,.395m);Assert.Equal(790,p.Cash);
    }
    [Fact]public void RegimeDoesNotCallMissingBenchmarksNeutral()
    {
        var result=QuantPortfolioEngine.Context(new Dictionary<string,QuantFrame>(),[],Now,P);Assert.Equal("Unknown",result.State);Assert.Null(result.PositiveBreadthPercent);
        Assert.Equal(0,QuantPortfolioEngine.EnvironmentScale(result,"Long"));
    }
    [Fact]public void ParameterAndCostChangesCreateDifferentResearchIdentity()
    {Assert.Equal(Hash,QuantRules.Hash(new()));Assert.NotEqual(Hash,QuantRules.Hash(P with{BreakoutVolumeRatio=1.6m}));Assert.NotEqual(Hash,QuantRules.Hash(P with{MaximumLeverage=4}));Assert.NotEqual(Hash,QuantRules.Hash(P,new(){FeeRate=.0006m}));}
    [Fact]public void BaselineMergePreservesConflictsAndRejectsPrematurelyObservedHourlySnapshots()
    {
        var bars=Bars();var merged=QuantSignalEngine.MergeHourly("TESTUSDT",bars.Append(bars[^1] with{Close=108.41m}).ToArray(),[],Now);
        var frame=QuantSignalEngine.Frame("TESTUSDT",merged,Now,P);Assert.Contains("冲突",frame.Issue);Assert.Equal(338,frame.Evidence.Count);
        var partial=bars.ToArray();partial[^1]=partial[^1] with{SourceTime=partial[^1].OpenTime.AddMinutes(5),ReceivedTime=partial[^1].OpenTime.AddMinutes(6)};
        Assert.NotNull(QuantSignalEngine.Frame("TESTUSDT",partial,Now,P).Issue);
    }
    [Fact]public async Task ShadowServiceFreezesOnlyFutureHoursSurvivesRestartAndNeverWritesOriginalPaperOrFetchesOnStatus()
    {
        using var temp=new Temp();var storage=Options.Create(new StorageOptions{Directory=temp.Path});var clock=new Clock{Current=Hour.AddSeconds(-30)};
        var market=new MarketRuntimeStore();var instruments=Enumerable.Range(1,22).Select(i=>new Instrument(i,i==21?"BTCUSDT":i==22?"ETHUSDT":$"TEST{i}USDT","TEST",Now.AddYears(-1),true)).ToArray();market.Register(instruments);
        var history=new History(instruments);var derivative=new DerivativeRuntimeStore();
        foreach(var ins in instruments)
        {
            market.Apply(new TickerEvent(ins.Symbol,108.4m,20_000_000,5,Now.AddSeconds(-1),Now));market.Apply(new MarkPriceEvent(ins.Symbol,108.4m,0,Now.AddHours(8),Now.AddSeconds(-1),Now));
            derivative.Apply(new DerivativeEvent(ins.Symbol,[Point(-1440,100,ins.Symbol),Point(-60,104,ins.Symbol),Point(0,105,ins.Symbol)],Now));
        }
        var original=new OriginalPaper();var persistence=new NoPaperWrites();var paper=new PaperService(original,persistence,market,new Radar(),clock,history,derivative);
        var external=new NoExternalFetch();var execution=new ExecutionEvidenceService(external,new ExecutionEvidenceRepository(storage),clock,new());var repo=new QuantShadowRepository(storage);
        var service=new QuantShadowService(market,derivative,history,repo,paper,execution,new(){HistorySymbols=20},clock);
        Assert.Null(service.Status().Cohort);Assert.Equal(0,external.Calls);await service.RefreshAsync(default);var start=service.Status().Cohort!.StartedAt;
        Assert.Equal(clock.Current,start);Assert.Empty(service.Status().ForwardHours);
        clock.Current=Now;await service.RefreshAsync(default);var status=service.Status();Assert.Null(status.Error);Assert.Equal("ForwardWindow",status.Latest!.Mode);
        Assert.Equal(20,Assert.Single(status.ForwardHours).Symbols);Assert.Equal(80,Assert.Single(status.ForwardHours).Observations);Assert.All(status.Latest.Decisions,d=>Assert.True(d.InputArchived));
        var captured=status.Latest.Decisions.Select(d=>d.CapturedAt).ToArray();clock.Current=Now.AddSeconds(30);await service.RefreshAsync(default);Assert.Equal(captured,service.Status().Latest!.Decisions.Select(d=>d.CapturedAt));
        var restarted=new QuantShadowService(market,derivative,history,new QuantShadowRepository(storage),paper,execution,new(){HistorySymbols=20},clock);await restarted.RefreshAsync(default);
        Assert.Equal(start,restarted.Status().Cohort!.StartedAt);Assert.Equal(captured,restarted.Status().Latest!.Decisions.Select(d=>d.CapturedAt));
        Assert.Equal(0,external.Calls);Assert.Equal(0,persistence.Writes);Assert.Equal(970.451475m,(await paper.OverviewAsync(default)).State.Cash);Assert.Equal(9,(await paper.OverviewAsync(default)).State.Revision);
        for(var i=0;i<3;i++)service.Status();Assert.Equal(0,external.Calls);
    }
    private sealed class Clock:TimeProvider{public DateTimeOffset Current;public override DateTimeOffset GetUtcNow()=>Current;}
    private sealed class History(IReadOnlyList<Instrument> instruments):IAssetRepository
    {public Task<IReadOnlyList<Instrument>> ReadInstrumentsAsync(CancellationToken ct)=>Task.FromResult(instruments);public Task<IReadOnlyList<Candle>> ReadHourlyHistoryAsync(int id,int limit,CancellationToken ct)=>Task.FromResult<IReadOnlyList<Candle>>(Bars(instruments.Single(i=>i.AssetId==id).Symbol));}
    private sealed class Radar:IRadarSnapshotSource{public RadarSnapshot GetSnapshot()=>null!;}
    private sealed class OriginalPaper:IPaperRepository
    {
        public PaperState Restore()=>new(){Revision=9,Cash=970.451475m,StartedAt=Now.AddDays(-2),UpdatedAt=Now.AddHours(-3)};
        public IReadOnlyList<PaperTrade> Trades()=>[];public IReadOnlyList<PaperHoldingSnapshot> HoldingHistory(Guid? id,int limit,long? before=null)=>[];public long HoldingSnapshotCount()=>0;
    }
    private sealed class NoPaperWrites:IPaperPersistence{public int Writes;public Task SaveAsync(PaperBatch batch,CancellationToken ct){Writes++;throw new InvalidOperationException("Shadow cannot write paper");}}
    private sealed class NoExternalFetch:IExecutionEvidenceProvider
    {
        public int Calls;private T Fail<T>(){Calls++;throw new InvalidOperationException("GET/analysis cannot fetch");}
        public Task<ContractRulesSnapshot> GetTradingRulesAsync(CancellationToken ct)=>Fail<Task<ContractRulesSnapshot>>();public Task<ExecutionOrderBook> GetOrderBookAsync(string symbol,CancellationToken ct)=>Fail<Task<ExecutionOrderBook>>();
        public Task<FundingRateWindow> GetFundingHistoryAsync(string symbol,DateTimeOffset from,DateTimeOffset through,CancellationToken ct)=>Fail<Task<FundingRateWindow>>();public Task<FundingIntervalSnapshot> GetFundingIntervalsAsync(CancellationToken ct)=>Fail<Task<FundingIntervalSnapshot>>();
    }
    [Fact]public void ImmutableForwardEvidenceSurvivesRestartAndCannotBeBackfilled()
    {
        using var temp=new Temp();var repo=new QuantShadowRepository(Options.Create(new StorageOptions{Directory=temp.Path}));var cohort=repo.OpenCohort(Hash,P,Hour.AddSeconds(-1));
        var universe=repo.FreezeUniverse(Hash,new(cohort.Id,Hour,Now,["TESTUSDT"]));Assert.Single(repo.FreezeUniverse(Hash,universe with{Symbols=["FAKE"]}).Symbols);Assert.Equal("TESTUSDT",repo.Universe(Hash,Hour)!.Symbols[0]);
        var input=new QuantInputBundle("TESTUSDT",Hour,Bars(),[]);var digest=repo.SaveInput(Hash,input);Assert.Equal(digest,repo.SaveInput(Hash,input));
        var frame=QuantSignalEngine.Frame("TESTUSDT",input.Candles,Now,P);var rows=QuantSignalEngine.Analyze(Market(price:input.Candles[^1].Close),frame,Oi,Now,"Forward",Hash,digest,P).Select(d=>d with{InputArchived=true}).ToArray();
        var batch=new QuantDecisionBatch(cohort.Id,Hash,"Forward",Hour,Now,"TESTUSDT",digest,"universe",Context,rows);Assert.True(repo.SaveDecision(batch));Assert.False(repo.SaveDecision(batch with{CapturedAt=Now.AddSeconds(1)}));
        var restarted=new QuantShadowRepository(Options.Create(new StorageOptions{Directory=temp.Path}));Assert.Equal(cohort,restarted.OpenCohort(Hash,P,Now.AddHours(1)));Assert.Equal(Now,Assert.Single(restarted.Decisions(Hash,Hour)).CapturedAt);Assert.Equal(4,Assert.Single(restarted.History(Hash,48)).Observations);
        Assert.Throws<InvalidDataException>(()=>repo.SaveDecision(batch with{Hour=Hour.AddHours(-1)}));Assert.Throws<InvalidDataException>(()=>repo.SaveDecision(batch with{CapturedAt=Hour.AddMinutes(10)}));
    }
    private sealed class Temp:IDisposable
    {
        public string Path {get;}=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"Pullet-Quant-"+Guid.NewGuid().ToString("N"));
        public void Dispose(){var full=System.IO.Path.GetFullPath(Path);if(!full.StartsWith(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()),StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException();if(Directory.Exists(full))Directory.Delete(full,true);}
    }
}
