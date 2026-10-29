using Microsoft.Extensions.Options;
using Microsoft.Data.Sqlite;
using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;

namespace PulletCryptoRadar.IntegrationTests;

public sealed class ForwardTests:IDisposable
{
    private static readonly DateTimeOffset Now=new(2026,10,29,12,2,0,TimeSpan.Zero);
    private readonly string root=Path.Combine(Path.GetTempPath(),"Pullet-Forward-"+Guid.NewGuid());
    private static ForwardCohort Cohort()=>new(Guid.NewGuid(),Now.AddHours(-1),Now.AddDays(14),new(),ForwardEngine.Hash(new()));
    private static MarketView Market(int id=1,decimal change=10)=>new(new(id,$"TEST{id}USDT","TEST",Now.AddDays(-100),true),102,1000000,change,
        102,.001m,Now.AddSeconds(-1),Now,Now,300,300,300,new());
    private static ForwardRank Rank()=>ForwardEngine.Capture([Market()],Now,new()).Rows[0];
    private static DerivativeRuntimeSnapshot Derivatives()=>new("TEST1USDT",new Dictionary<DerivativeKind,IReadOnlyList<DerivativeSample>>(),[],null,null);
    private static Candle[] Bars()
    {
        var end=DateTimeOffset.FromUnixTimeSeconds(Now.ToUnixTimeSeconds()/3600*3600);
        var rows=Enumerable.Range(0,337).Select(i=>new Candle("TEST1USDT",60,end.AddHours(i-337),100,103,99,102,100,
            end.AddHours(i-336).AddMilliseconds(-1),end.AddHours(i-336))).ToArray();
        rows[330]=rows[330] with{Low=98};rows[^1]=rows[^1] with{QuoteVolume=150};return rows;
    }
    [Fact]public void FrozenHashSurvivesRoundTripAndChangingDefinitionChangesHash()
    {var a=new ForwardRules();Assert.NotEqual(ForwardEngine.Hash(a),ForwardEngine.Hash(a with{CompressionMax=.13m}));}
    [Fact]public void RankingUsesOnlyFreshActiveQuotesAndStableTies()
    {
        var old=Market(3,90) with{PriceSourceTime=Now.AddMinutes(-3)};
        var snap=ForwardEngine.Capture([Market(2),old,Market()],Now,new());
        Assert.Equal(1,snap.Rows.Single(r=>r.Symbol=="TEST1USDT").Rank);Assert.Null(snap.Rows.Single(r=>r.Symbol=="TEST3USDT").Rank);
    }
    [Fact]public void MissingHistoryRecordedForBothStrategiesNotSilentlyDropped()
    {var ds=ForwardEngine.Decide(Cohort(),Market(),[],Rank(),Derivatives(),Now);Assert.Equal(2,ds.Count);Assert.All(ds,d=>{Assert.Equal("Insufficient",d.Status);Assert.Null(d.Plan);});}
    [Fact]public void CandidateHasConfirmedStructureRiskAndUnknownOi()
    {
        var ds=ForwardEngine.Decide(Cohort(),Market(),Bars(),Rank(),Derivatives(),Now);var a=ds.Single(d=>d.Strategy=="A");
        Assert.Equal("Candidate",a.Status);Assert.NotNull(a.Plan);Assert.True(a.Plan.Margin<=100);Assert.True(a.Plan.EstimatedRisk<=10.00000001m);
        Assert.Contains("Unknown",a.OiConfirmation);Assert.Null(a.Oi24hPercent);Assert.Equal("NoSignal",ds.Single(d=>d.Strategy=="B").Status);
    }
    [Fact]public void FutureCandleOrFutureReceivedEvidenceCannotCreatePlan()
    {
        var bars=Bars();bars[^1]=bars[^1] with{ReceivedTime=Now.AddMinutes(1)};
        Assert.All(ForwardEngine.Decide(Cohort(),Market(),bars,Rank(),Derivatives(),Now),d=>Assert.Equal("Insufficient",d.Status));
    }
    [Fact]public void BaselineGapAndLateDecisionAreNotBackfilled()
    {
        var rows=Bars();rows[120]=rows[120] with{OpenTime=rows[120].OpenTime.AddMinutes(1)};
        Assert.All(ForwardEngine.Decide(Cohort(),Market(),rows,Rank(),Derivatives(),Now),d=>Assert.Equal("Insufficient",d.Status));
        Assert.All(ForwardEngine.Decide(Cohort(),Market(),Bars(),Rank(),Derivatives(),Now.AddMinutes(15)),d=>Assert.Null(d.Plan));
    }
    [Fact]public void PreStartSignalCannotBeBackfilled()
    {var c=Cohort() with{StartedAt=Now};Assert.All(ForwardEngine.Decide(c,Market(),Bars(),Rank(),Derivatives(),Now),d=>Assert.Contains("早于",d.Reason));}
    [Fact]public void OneMissingFiveMinuteBarPreventsHourlyAggregation()
    {
        var start=Now.AddHours(-2);start=start.AddMinutes(-start.Minute);
        var bars=Enumerable.Range(0,12).Select(i=>new Candle("TEST1USDT",5,start.AddMinutes(i*5),100,101,99,100,1,start.AddMinutes((i+1)*5).AddMilliseconds(-1),Now)).ToArray();
        Assert.Single(ForwardEngine.Hourly([],bars,Now));Assert.Empty(ForwardEngine.Hourly([],bars.Skip(1).ToArray(),Now));
    }
    private static ForwardDecision Decision()=>new("d","TEST1USDT","A",Now,Now,"Candidate","test",337,1,2,1,null,null,null,"Unknown",
        new(100,95,1,10,6,110,120,"pressure","projection",2),ForwardEngine.Hash(new()));
    [Fact]public void NoSamplesProducesUnknownNotAFalseWinOrStopFailure()
    {
        var r=ForwardEngine.Evaluate(Decision(),24,[],Now.AddHours(24),new());
        Assert.False(r.CoverageComplete);Assert.Null(r.EndReturnPercent);Assert.Null(r.SampledStopCrossed);Assert.Equal("Incomplete",r.Status);
    }
    [Fact]public void SampledCrossingAndEndReturnAreLabelsNotNetPnl()
    {
        var d=Decision();var rows=Enumerable.Range(1,288).Select(i=>
        {var t=Now.AddMinutes(i*5);var r=Rank() with{Price=i==50?94:i==100?121:105,SourceTime=t,ReceivedTime=t};return new ForwardSnapshot(t,ForwardEngine.Bucket(t),[r]);}).ToArray();
        var o=ForwardEngine.Evaluate(d,24,rows,Now.AddHours(24),new());
        Assert.True(o.CoverageComplete);Assert.True(o.SampledStopCrossed);Assert.True(o.SampledTarget2Crossed);Assert.Equal(5,o.EndReturnPercent);
        Assert.Contains("未计算净利润",o.Meaning);
        var partial=ForwardEngine.Evaluate(d,24,rows.Where((_,i)=>i<20||i>35).ToArray(),Now.AddHours(24),new());Assert.False(partial.CoverageComplete);
    }
    [Fact]public void SqliteRestartsPreserveCohortAndFirstDecisionWithoutRewriting()
    {
        var settings=Options.Create(new StorageOptions{Directory=root});var r=new ForwardRepository(settings);var c=Cohort();r.Initialize(c);
        r.Initialize(c with{Id=Guid.NewGuid(),StartedAt=Now});Assert.Equal(c,r.Cohort());
        var snap=new ForwardSnapshot(Now,ForwardEngine.Bucket(Now),[Rank()]);r.Save(snap,[Decision()]);
        r.Save(snap with{Bucket=snap.Bucket.AddMinutes(5),CapturedAt=Now.AddMinutes(5)},[Decision() with{Reason="rewrite attempt"}]);
        var restored=new ForwardRepository(settings);Assert.Equal("test",Assert.Single(restored.Decisions()).Reason);
        Assert.Equal(2,restored.SnapshotSummaries().Count);Assert.True(restored.HasSnapshot(snap.Bucket));
        Assert.Throws<SqliteException>(()=>restored.Save(snap,[]));
        Assert.Equal(2,restored.Snapshots(Now,Now.AddMinutes(10)).Count);
    }
    [Fact]public void OutcomeIsImmutableAcrossRestartAndDuplicateSave()
    {
        var r=new ForwardRepository(Options.Create(new StorageOptions{Directory=root}));var o=ForwardEngine.Evaluate(Decision(),24,[],Now.AddHours(24),new());
        r.SaveOutcomes([o]);r.SaveOutcomes([o with{Status="fake rewrite"}]);Assert.Equal("Incomplete",Assert.Single(r.Outcomes()).Status);
    }
    [Fact]public void OutcomesRejectFutureTimestampAndPrematureEvaluation()
    {
        var d=Decision();var t=Now.AddHours(24);var snap=new ForwardSnapshot(t,ForwardEngine.Bucket(t),[Rank() with{SourceTime=t.AddMinutes(1),ReceivedTime=t,Price=1000000}]);
        Assert.Throws<ArgumentException>(()=>ForwardEngine.Evaluate(d,24,[],Now,new()));
        var outcome=ForwardEngine.Evaluate(d,24,[snap],t,new());Assert.Equal(0,outcome.Samples);Assert.Null(outcome.EndReturnPercent);
    }
    private sealed class Clock : TimeProvider
    {public DateTimeOffset Now=ForwardTests.Now;public override DateTimeOffset GetUtcNow()=>Now;}
    private sealed class History : IAssetRepository
    {
        public Task<IReadOnlyList<Instrument>> ReadInstrumentsAsync(CancellationToken ct)=>Task.FromResult<IReadOnlyList<Instrument>>([]);
        public Task<IReadOnlyList<Candle>> ReadHourlyHistoryAsync(int id,int limit,CancellationToken ct)=>Task.FromResult<IReadOnlyList<Candle>>(Bars());
    }
    [Fact]public async Task ServiceDoesNotReplayMissedSnapshotsAndKeepsPostPeriodOutcomeCollection()
    {
        var clock=new Clock();var r=new ForwardRepository(Options.Create(new StorageOptions{Directory=root}));var cohort=Cohort();r.Initialize(cohort);
        var market=new MarketRuntimeStore();market.Register([Market().Instrument]);market.Apply(new TickerEvent("TEST1USDT",102,1000000,10,Now,Now));
        var service=new ForwardService(r,market,new History(),new DerivativeRuntimeStore(),clock);
        await service.TickAsync(default);await service.TickAsync(default);Assert.Single(r.SnapshotSummaries());Assert.Equal(2,r.Decisions().Count);
        var overview=System.Text.Json.JsonSerializer.SerializeToElement(await service.OverviewAsync(default));
        // Fixture cohort began an hour before its first tick: those 12 missing slots stay visible.
        Assert.Equal(13,overview.GetProperty("Days")[0].GetProperty("ExpectedSlots").GetInt32());
        Assert.Equal(12,overview.GetProperty("Days")[0].GetProperty("MissingSlots").GetInt32());
        clock.Now=Now.AddHours(2);await service.TickAsync(default);Assert.Equal(2,r.SnapshotSummaries().Count);
        clock.Now=cohort.EndsAt.AddHours(1);var decisions=r.Decisions().Count;await service.TickAsync(default);
        Assert.Equal(3,r.SnapshotSummaries().Count);Assert.Equal(decisions,r.Decisions().Count);
        clock.Now=cohort.EndsAt.AddHours(73);await service.TickAsync(default);Assert.Equal(3,r.SnapshotSummaries().Count);
        Assert.Equal(cohort.Id,r.Cohort()!.Id);
    }
    public void Dispose(){SqliteConnection.ClearAllPools();if(Directory.Exists(root))Directory.Delete(root,true);}
}
