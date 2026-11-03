using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;
namespace PulletCryptoRadar.IntegrationTests;

public sealed class PaperPerformanceTests:IDisposable
{
    private static readonly DateTimeOffset Midnight=new(2026,11,2,0,0,0,TimeSpan.FromHours(8));
    private readonly string directory=Path.Combine(Path.GetTempPath(),"Pullet-Equity-"+Guid.NewGuid());
    private static PaperState Empty(DateTimeOffset at,long revision=1)=>new(){Revision=revision,StartedAt=at,UpdatedAt=at};
    private static PaperEquitySample Sample(DateTimeOffset at,long revision,decimal? equity)=>PaperEquityRecorder.Capture(Empty(at,revision),[],[],at) with{Equity=equity};
    private static PaperPerformance Report(PaperEquitySample current,PaperEquitySample[] samples,DateOnly? date=null)=>
        PaperPerformanceEngine.Build(date??PaperEngine.Day(Midnight),current.CapturedAt,current,samples.Select(s=>s.Point()).ToArray(),samples,samples.Min(s=>s.CapturedAt));
    private static PaperPosition Position(DateTimeOffset at,string direction="Long")=>new(Guid.NewGuid(),"key",1,"TESTUSDT",at,100,1,10,9,.05m,1,at,105,at,90,120,
        new("key",Guid.Empty,1,"TESTUSDT",at,null)){InitialQuantity=1,Direction=direction,LastMarkSourceTime=at};
    [Fact]public void LongShortAndAlreadyPaidCostsReconcileWithoutDoubleDebit()
    {
        var p=Position(Midnight);var s=Empty(Midnight) with{Cash=989.95m,Positions=[p]};
        var longSample=PaperEquityRecorder.Capture(s,[],[],Midnight);
        Assert.Equal(1003.95m,longSample.Equity);Assert.Equal(0,longSample.Difference);Assert.True(longSample.Reconciled);
        Assert.Equal(3.95m,longSample.Contributions.Single(c=>c.Cohort=="Legacy").TotalNet);
        var shortSample=PaperEquityRecorder.Capture(s with{Positions=[p with{Direction="Short"}]},[],[],Midnight);
        Assert.Equal(993.95m,shortSample.Equity);Assert.Equal(-6.05m,shortSample.Contributions.Single(c=>c.Cohort=="Legacy").TotalNet);
        Assert.Equal(.05m,shortSample.Contributions.Sum(c=>c.Fees));Assert.Equal(1m,shortSample.Contributions.Sum(c=>c.Funding));
    }
    [Fact]public void LegacyMarginFloorIsAnExplicitValuationAdjustment()
    {
        var p=Position(Midnight) with{LastMark=80};var s=Empty(Midnight) with{Cash=989.95m,Positions=[p]};
        var result=PaperEquityRecorder.Capture(s,[],[],Midnight);
        Assert.Equal(-20,result.Gross);Assert.Equal(11,result.Adjustment);Assert.Equal(989.95m,result.Equity);Assert.Equal(0,result.Difference);
    }
    [Fact]public void MissingMarksFutureClocksAndStaleAccountsRemainUnknown()
    {
        var p=Position(Midnight);var s=Empty(Midnight) with{Cash=989.95m,Positions=[p]};
        Assert.Null(PaperEquityRecorder.Capture(s with{Positions=[p with{LastMarkSourceTime=default}]},[],[],Midnight).Equity);
        Assert.Null(PaperEquityRecorder.Capture(s with{Positions=[p with{LastMarkSourceTime=Midnight.AddSeconds(1)}]},[],[],Midnight).Equity);
        Assert.Null(PaperEquityRecorder.Capture(s,[],[],Midnight.AddSeconds(31)).Equity);
        Assert.Null(PaperEquityRecorder.Capture(s with{UpdatedAt=Midnight.AddSeconds(1)},[],[],Midnight).Equity);
    }
    [Fact]public void MissingTradesCannotBeHiddenBehindCashAndStaleHistory()
    {
        var result=PaperEquityRecorder.Capture(Empty(Midnight) with{Cash=999},[],[],Midnight);
        Assert.False(result.Reconciled);Assert.Equal(-1,result.Difference);Assert.Null(result.Equity);Assert.Contains(result.Issues,i=>i.Contains("不守恒"));
    }
    [Fact]public void ActualQuantEntryAndClosedFundingMatchCurrentCohortAndKeepCapitalIntact()
    {
        var (s,_)=QuantLiveTests.Open();var opened=s.Positions.Single();
        var before=QuantRules.HashValue(s);var funded=s with{QuantFunding=[new(opened.Id,opened.Symbol,"ObservedWindow",s.UpdatedAt,s.UpdatedAt,"Fixture")]};
        var sample=PaperEquityRecorder.Capture(funded,[],[],s.UpdatedAt);
        Assert.True(sample.Reconciled);Assert.NotNull(sample.Equity);Assert.Equal(1,sample.Contributions.Single(c=>c.Cohort=="Current").OpenPositions);
        Assert.Equal(sample.Equity-1000,sample.Contributions.Sum(c=>c.TotalNet));Assert.Equal(before,QuantRules.HashValue(s));
        Assert.Null(PaperEquityRecorder.Capture(s with{QuantFunding=[]},[],[],s.UpdatedAt).Equity);
        var changed=funded with{QuantControl=funded.QuantControl! with{ActivatedAt=s.UpdatedAt.AddSeconds(1)}};
        Assert.Equal(1,PaperEquityRecorder.Capture(changed,[],[],s.UpdatedAt).Contributions.Single(c=>c.Cohort=="Other").OpenPositions);
    }
    [Fact]public void PartialExitAndLateSignedFundingBridgeToSameAccountEquity()
    {
        var p=Position(Midnight) with{Quantity=.5m,RemainingMargin=4.5m,OpenFee=.025m,FundingCost=.5m};
        var trade=new PaperTrade(Guid.NewGuid(),"key",1,"TESTUSDT",Midnight,Midnight,100,105,.5m,5,1,2.5m,.05m,.5m,1.95m,"Partial",false,"Legacy",p.Intent){PositionId=p.Id,FinalExit=false};
        var state=Empty(Midnight) with{Cash=996.925m,Positions=[p]};
        var sample=PaperEquityRecorder.Capture(state,[trade],[],Midnight);
        Assert.Equal(1003.925m,sample.Equity);Assert.Equal(3.925m,sample.Contributions.Sum(c=>c.TotalNet));Assert.Equal(.075m,sample.Contributions.Sum(c=>c.Fees));
        // A late credit for the already exited part raises cash once; open part is already in RemainingMargin.
        var eventPayment=new QuantFundingEvent("late",Midnight,0,-.2m,new("Fixture",p.Id,"TESTUSDT","Long",Midnight,"Fixture",1,-.002m,100,-.2m,Midnight));
        var credited=PaperEquityRecorder.Capture(state with{Cash=state.Cash+.2m},[trade],[eventPayment],Midnight);
        Assert.Equal(sample.Equity+.2m,credited.Equity);Assert.Equal(0,credited.Difference);Assert.Equal(.8m,credited.Contributions.Sum(c=>c.Funding));
    }
    [Fact]public void OrphanFundingDoesNotBecomeTrustedEquity()
    {
        var payment=new QuantFundingEvent("orphan",Midnight,0,0,new("Fixture",Guid.NewGuid(),"TESTUSDT","Long",Midnight,"Fixture",1,0,100,0,Midnight));
        var result=PaperEquityRecorder.Capture(Empty(Midnight),[],[payment],Midnight);
        Assert.Null(result.Equity);Assert.False(result.Reconciled);
    }
    [Fact]public void MiddayStartAndOldRollingCurveDoNotInventDailyReturn()
    {
        var current=Sample(Midnight.AddHours(12),2,990);var report=Report(current,[current]);
        Assert.Equal("MissingOpening",report.Day.Status);Assert.Null(report.Day.Net);Assert.Null(report.Day.ReturnPercent);
        Assert.Equal(0,report.Drawdown.MaximumPercent);Assert.Equal(1,report.Drawdown.ValidSamples);
        var legacy=Empty(Midnight.AddHours(12)) with{Curve=[new(Midnight,1000,true)]};
        var sample=PaperEquityRecorder.Capture(legacy,[],[],legacy.UpdatedAt);
        Assert.Equal("MissingOpening",Report(sample,[sample]).Day.Status);
    }
    [Fact]public void ShanghaiBoundaryAndCurrentNetIncludeFloatingLosses()
    {
        var start=Sample(Midnight.AddSeconds(4),1,1000);var current=Sample(Midnight.AddHours(2),3,975);
        var r=Report(current,[start,current]);Assert.Equal("Ready",r.Day.Status);Assert.Equal(-25,r.Day.Net);Assert.Equal(-2.5m,r.Day.ReturnPercent);
        Assert.Equal(Midnight,r.Day.From);Assert.Equal(2.5m,r.Drawdown.MaximumPercent);Assert.Equal(2.5m,r.Drawdown.CurrentPercent);
    }
    [Fact]public void InvalidFirstBoundaryIsNotSkippedAndSixtySecondLimitIsExclusive()
    {
        var bad=Sample(Midnight.AddSeconds(2),1,null);var later=Sample(Midnight.AddSeconds(30),2,1000);var current=Sample(Midnight.AddMinutes(2),3,1010);
        Assert.Equal("InvalidOpening",Report(current,[bad,later,current]).Day.Status);
        Assert.Null(Report(current,[bad,later,current]).Day.Net);
        var late=Sample(Midnight.AddSeconds(60),2,1000);Assert.Equal("MissingOpening",Report(current,[late,current]).Day.Status);
    }
    [Fact]public void HistoricalDayNeedsNextBoundaryAndUsesActualTimes()
    {
        var opening=Sample(Midnight.AddSeconds(3),1,1000);var last=Sample(Midnight.AddHours(23).AddMinutes(59),2,1030);
        var current=Sample(Midnight.AddDays(1).AddHours(4),4,1020);
        Assert.Equal("MissingClosing",Report(current,[opening,last,current]).Day.Status);
        var closing=Sample(Midnight.AddDays(1).AddSeconds(7),3,1040);
        var result=Report(current,[opening,last,closing,current]);Assert.Equal(40,result.Day.Net);Assert.Equal(4,result.Day.ReturnPercent);Assert.Equal(closing.CapturedAt,result.Day.Closing!.CapturedAt);
        Assert.Equal(1040,result.Drawdown.PeakEquity);Assert.Equal(100m*20/1040,result.Drawdown.CurrentPercent);
    }
    [Theory][InlineData(0)][InlineData(-10)]public void NonPositiveOpeningKeepsNetButNoDivision(decimal initial)
    {
        var opening=Sample(Midnight,1,initial);var current=Sample(Midnight.AddMinutes(1),2,initial+5);
        var r=Report(current,[opening,current]);Assert.Equal("InvalidBase",r.Day.Status);Assert.Equal(5,r.Day.Net);Assert.Null(r.Day.ReturnPercent);
    }
    [Fact]public void UnknownCurrentAndNoValidSamplesNeverProduceZeroDrawdown()
    {
        var a=Sample(Midnight,1,null);var b=Sample(Midnight.AddMinutes(1),2,null);var r=Report(b,[a,b]);
        Assert.Null(r.Drawdown.MaximumPercent);Assert.Null(r.Drawdown.CurrentPercent);Assert.Null(r.CumulativeNet);
        var valid=Sample(Midnight,1,1000);var incomplete=Report(b,[valid,b]);Assert.Equal(0,incomplete.Drawdown.MaximumPercent);Assert.Null(incomplete.Drawdown.CurrentPercent);
    }
    [Fact]public void GapsAndInvalidSamplesRemainVisibleWithoutAssumingUnseenPeaks()
    {
        var a=Sample(Midnight,1,1000);var b=Sample(Midnight.AddMinutes(1),2,null);var c=Sample(Midnight.AddMinutes(10),3,900);
        var r=Report(c,[a,b,c]);Assert.Equal(10,r.Drawdown.MaximumPercent);Assert.Equal(1,r.Day.InvalidSamples);Assert.Equal(1,r.Day.GapIntervals);Assert.Equal(540,r.Day.LongestGapSeconds);
    }
    [Fact]public void StaleLiveValuationCannotErasePreviouslyValidSavedObservationOfSameRevision()
    {
        var saved=Sample(Midnight.AddSeconds(4),1,1000);var stale=saved with{Equity=null,MarksFresh=false};
        var result=PaperPerformanceEngine.Build(PaperEngine.Day(Midnight),Midnight.AddMinutes(2),stale,[saved.Point()],[saved],saved.CapturedAt);
        Assert.Equal(1,result.Drawdown.ValidSamples);Assert.Equal(0,result.Drawdown.InvalidSamples);Assert.Equal(saved.CapturedAt,result.Drawdown.FirstValidAt);
        Assert.Equal(0,result.Drawdown.MaximumPercent);Assert.Null(result.Drawdown.CurrentPercent);Assert.Null(result.Day.Net);Assert.Equal("InvalidClosing",result.Day.Status);
    }
    [Fact]public void WindowExcludesFutureAndExpiredSamplesAndDoesNotResetAtCurveLimit()
    {
        var start=Midnight.AddDays(-2);var samples=Enumerable.Range(0,22000).Select(i=>Sample(start.AddSeconds(i*5),i+1,i<100?1100:1000)).ToList();
        var current=samples[^1];samples.Add(Sample(current.CapturedAt.AddDays(1),30000,2000));samples.Add(Sample(start.AddDays(-100),0,9999));
        var r=Report(current,samples.ToArray(),PaperEngine.Day(current.CapturedAt));Assert.Equal(22000,r.Drawdown.ValidSamples);Assert.Equal(1100,r.Drawdown.PeakEquity);Assert.Equal(100m*100/1100,r.Drawdown.MaximumPercent);
    }
    [Fact]public void MinuteBucketsAndTradingEventsDecideDurableSamples()
    {
        var before=Empty(Midnight.AddSeconds(-4)) with{LastEquitySampleAt=Midnight.AddSeconds(-4)};
        var next=new PaperBatch(Empty(Midnight.AddSeconds(1),2),1,[]);var result=PaperEquityRecorder.Attach(before,next,[],[]);
        Assert.Single(result.EquitySamples);Assert.Equal(next.State.UpdatedAt,result.State.LastEquitySampleAt);
        var same=new PaperBatch(result.State with{Revision=3,UpdatedAt=Midnight.AddSeconds(40)},2,[]);Assert.Empty(PaperEquityRecorder.Attach(result.State,same,[],[]).EquitySamples);
        var opened=same with{State=same.State with{Cash=989.95m,Positions=[Position(same.State.UpdatedAt)]}};Assert.Single(PaperEquityRecorder.Attach(result.State,opened,[],[]).EquitySamples);
    }
    [Fact]public void DatabaseSamplesAreAtomicDurableAndRejectCorruptBatchWithoutTradeLeak()
    {
        var db=new SqliteDatabase(Options.Create(new StorageOptions{Directory=directory}));db.Initialize();db.Initialize();var repo=new PaperRepository(db);
        var initial=Empty(Midnight.AddSeconds(-5));db.SavePaper(new(initial,0,[]));
        var batch=PaperEquityRecorder.Attach(initial,new(Empty(Midnight.AddSeconds(3),2),1,[]),[],[]);db.SavePaper(batch);
        Assert.Single(repo.EquityPoints(Midnight,Midnight.AddDays(1)));Assert.Equal(1000,Assert.Single(repo.EquitySamples(Midnight,Midnight.AddDays(1))).Equity);
        Assert.Throws<MappingConflictException>(()=>db.SavePaper(batch));
        var state=batch.State with{Revision=3,UpdatedAt=Midnight.AddMinutes(1)};var next=PaperEquityRecorder.Attach(batch.State,new(state,2,[]),[],[]);
        var sample=next.EquitySamples[0];var p=Position(state.UpdatedAt);var trade=new PaperTrade(Guid.NewGuid(),"key",1,"TESTUSDT",Midnight,state.UpdatedAt,100,100,1,10,1,0,0,0,0,"Fixture",false,"Legacy",p.Intent);
        var invalid=next with{Trades=[trade],EquitySamples=[sample with{Cash=999}]};Assert.Throws<InvalidDataException>(()=>db.SavePaper(invalid));
        Assert.Empty(repo.Trades());Assert.Equal(2,repo.Restore()!.Revision);Assert.Single(repo.EquitySamples(Midnight,Midnight.AddDays(1)));
        db.SavePaper(next);Assert.Equal(2,repo.EquityPoints(Midnight,Midnight.AddDays(1)).Count);Assert.NotNull(repo.FirstEquitySampleAt());
        Assert.Equal(next.State.LastEquitySampleAt,repo.Restore()!.LastEquitySampleAt);
    }
    [Fact]public void HistoricalQueryCacheRefreshesAndFutureDatesAreRejected()
    {
        var opening=Sample(Midnight.AddSeconds(3),1,1000);var repository=new EquityRepository([opening]);
        var service=new PaperPerformanceService(null!,repository,TimeProvider.System);
        var first=service.ForContext(PaperEngine.Day(Midnight),Midnight.AddSeconds(5),Empty(Midnight.AddSeconds(5),2),[],[]);
        Assert.Equal(0,first.Day.Net);Assert.Equal(1,repository.Reads);
        service.ForContext(null,Midnight.AddSeconds(30),Empty(Midnight.AddSeconds(30),3),[],[]);Assert.Equal(1,repository.Reads);
        repository.Rows.Add(Sample(Midnight.AddMinutes(1),4,1000));
        var refreshed=service.ForContext(null,Midnight.AddMinutes(1).AddSeconds(10),Empty(Midnight.AddMinutes(1).AddSeconds(10),5),[],[]);
        Assert.Equal(2,repository.Reads);Assert.Equal(3,refreshed.Drawdown.ValidSamples);
        Assert.Throws<ArgumentException>(()=>service.ForContext(PaperEngine.Day(Midnight).AddDays(1),Midnight,Empty(Midnight),[],[]));
        Assert.Throws<ArgumentException>(()=>service.ForContext(PaperEngine.Day(Midnight).AddDays(-61),Midnight,Empty(Midnight),[],[]));
    }
    [Fact]public void NestedDailyPerformanceRoundTripsThroughRealArchiveWithUnknownsAndDecimals()
    {
        var control=QuantPaperEngine.Control(Midnight,new(),new());var state=Empty(Midnight.AddHours(1)) with{QuantControl=control};
        var sample=PaperEquityRecorder.Capture(state,[],[],state.UpdatedAt);var performance=Report(sample,[sample]);
        var report=QuantDailyEngine.Build(PaperEngine.Day(Midnight),state.UpdatedAt,state,[],[],[],[],[],new([],false),new Dictionary<Guid,IReadOnlyList<PaperHoldingSnapshot>>()) with{AccountPerformance=performance};
        var repository=new QuantDailyRepository(Options.Create(new StorageOptions{Directory=directory}));repository.Save(report);
        var restored=repository.Read(control,PaperEngine.Day(Midnight))!;
        Assert.Equal(QuantRules.HashValue(performance),QuantRules.HashValue(restored.AccountPerformance));Assert.Null(restored.AccountPerformance!.Day.Net);
        Assert.Equal(PaperEquityRecorder.Version,restored.AccountPerformance.Version);
    }
    private sealed class EquityRepository(IEnumerable<PaperEquitySample> rows):IPaperRepository
    {
        public List<PaperEquitySample> Rows {get;}=rows.ToList();public int Reads;
        public PaperState? Restore()=>null;public IReadOnlyList<PaperTrade> Trades()=>[];
        public IReadOnlyList<PaperHoldingSnapshot> HoldingHistory(Guid? positionId,int limit,long? before=null)=>[];
        public long HoldingSnapshotCount()=>0;
        public IReadOnlyList<PaperEquityPoint> EquityPoints(DateTimeOffset since,DateTimeOffset through){Reads++;return Rows.Select(r=>r.Point()).ToArray();}
        public IReadOnlyList<PaperEquitySample> EquitySamples(DateTimeOffset since,DateTimeOffset through)=>Rows.ToArray();
        public DateTimeOffset? FirstEquitySampleAt()=>Rows.FirstOrDefault()?.CapturedAt;
    }
    public void Dispose(){SqliteConnection.ClearAllPools();if(Directory.Exists(directory))Directory.Delete(directory,true);}
}
