using Microsoft.Extensions.Options;
using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;
namespace PulletCryptoRadar.IntegrationTests;
public sealed class QuantStudyTests
{
    private static readonly DateTimeOffset Now=new(2026,11,1,17,0,0,TimeSpan.Zero);
    private static readonly DateTimeOffset EntryAt=Now.AddHours(-80);
    private static readonly QuantStudyParameters Zero=new(){FeePerSide=0,SlippagePerSide=0,AdverseFundingPerHour=0};
    private static QuantExitSeed Seed=>new("TESTUSDT","Long",EntryAt,100,1,95,110,120,2);
    private static Candle Bar(int hour,decimal open,decimal high,decimal low,decimal close)=>new("TESTUSDT",60,EntryAt.AddHours(hour),open,high,low,close,1_000_000,EntryAt.AddHours(hour+1).AddMilliseconds(-1),Now.AddSeconds(-1));
    private static Candle[] TailBars=>[Bar(0,100,111,99,110),Bar(1,110,121,108,120),Bar(2,120,122,114,116)];
    [Theory][InlineData(false)][InlineData(true)]public void PartialExitsConserveQuantityAndTrailingOnlyAppliesAfterBarClose(bool mirror)
    {
        var seed=Seed;var bars=TailBars;
        if(mirror){seed=seed with{Direction="Short",Stop=105,Target1=90,Target2=80};bars=bars.Select(c=>c with{Open=200-c.Open,High=200-c.Low,Low=200-c.High,Close=200-c.Close}).ToArray();}
        var result=QuantExitEngine.Replay(seed,bars,Now,"ScaleTail",1,Zero);Assert.Equal("Closed",result.Status);Assert.Equal(15,result.Net);
        Assert.Equal(new[]{.3m,.3m,.4m},result.Legs.Select(l=>l.Quantity));Assert.Equal(seed.Quantity,result.Legs.Sum(l=>l.Quantity));Assert.Equal(0,result.RemainingQuantity);
        Assert.Equal(bars[2].EndTime,result.ClosedAt);Assert.Equal(mirror?85:115,result.Legs[^1].Price);
        var full=QuantExitEngine.Replay(seed,bars,Now,"FullAtFirst",1,Zero);Assert.Equal(10,full.Net);Assert.Single(full.Legs);
    }
    [Theory][InlineData(false)][InlineData(true)]public void AmbiguousStopAndTargetsRetainBothOrdersRatherThanAlwaysChoosingTheProfit(bool mirror)
    {
        var seed=Seed;var bar=Bar(0,100,122,94,101);
        if(mirror){seed=seed with{Direction="Short",Stop=105,Target1=90,Target2=80};bar=bar with{Open=100,High=106,Low=78,Close=99};}
        var lower=QuantExitEngine.Replay(seed,[bar],Now,"ScaleTail",1,Zero);var higher=QuantExitEngine.Replay(seed,[bar],Now,"ScaleTail",1,Zero,true);
        Assert.Equal(-5,lower.Net);Assert.Equal(7,higher.Net);Assert.Equal(1,lower.AmbiguousBars);Assert.Equal(1,higher.AmbiguousBars);Assert.Equal(lower.ClosedAt,higher.ClosedAt);
    }
    [Theory][InlineData("Long",90,-10)][InlineData("Short",110,-10)]public void AdverseOpeningGapUsesOpeningPriceNotTheUnreachableStop(string direction,int open,int expected)
    {
        var seed=direction=="Long"?Seed:Seed with{Direction="Short",Stop=105,Target1=90,Target2=80};var bar=Bar(0,open,open+1,open-1,open);
        var result=QuantExitEngine.Replay(seed,[bar],Now,"ScaleTail",1,Zero);Assert.Equal(expected,result.Net);Assert.Equal("GapStop",Assert.Single(result.Legs).Reason);Assert.Equal(EntryAt,result.ClosedAt);
    }
    [Fact]public void OpeningTargetIsKnownBeforeIntrabarStopAndReceivesNoInventedGapImprovement()
    {
        var bars=new[]{Bar(0,100,102,99,101),Bar(1,125,126,94,100)};var result=QuantExitEngine.Replay(Seed,bars,Now,"ScaleTail",1,Zero);
        Assert.Equal(7,result.Net);Assert.Equal(110,result.Legs[0].Price);Assert.Equal(120,result.Legs[1].Price);Assert.Equal(bars[1].OpenTime,result.Legs[0].At);
    }
    [Fact]public void HigherCostsReduceModelResultsAndFundingUsesOnlyRemainingExposure()
    {
        var p=new QuantStudyParameters();var one=QuantExitEngine.Replay(Seed,TailBars,Now,"ScaleTail",1,p);var two=QuantExitEngine.Replay(Seed,TailBars,Now,"ScaleTail",2,p);var three=QuantExitEngine.Replay(Seed,TailBars,Now,"ScaleTail",3,p);
        Assert.True(one.Net>two.Net&&two.Net>three.Net);Assert.Equal(.021m,one.Funding);Assert.True(one.Fees>0);Assert.True(three.Fees>one.Fees);
    }
    [Fact]public void MissingFutureForeignAndConflictingPathsAreUnknownRatherThanEndOfWindowProfit()
    {
        Assert.Equal("MissingPath",QuantExitEngine.Replay(Seed,[TailBars[0],TailBars[2]],Now,"ScaleTail",1,Zero).Status);
        Assert.Null(QuantExitEngine.Replay(Seed,[TailBars[0]],Now,"ScaleTail",1,Zero).Net);
        Assert.Equal("RightCensored",QuantExitEngine.Replay(Seed,[TailBars[0]],EntryAt.AddHours(1),"ScaleTail",1,Zero).Status);
        Assert.Null(QuantExitEngine.Replay(Seed,[TailBars[0] with{Symbol="FAKE"}],Now,"ScaleTail",1,Zero).Net);
        Assert.Null(QuantExitEngine.Replay(Seed,[TailBars[0] with{ReceivedTime=Now.AddSeconds(1)}],Now,"ScaleTail",1,Zero).Net);
        Assert.Equal("MissingPath",QuantExitEngine.Replay(Seed,[TailBars[0],TailBars[0] with{High=112}],Now,"ScaleTail",1,Zero).Status);
    }
    [Fact]public void CompleteTimeLimitIsSeventyTwoHoursRatherThanForcedResearchWindowClose()
    {
        var bars=Enumerable.Range(0,72).Select(i=>Bar(i,100,101,99,100)).ToArray();var result=QuantExitEngine.Replay(Seed,bars,Now,"ScaleTail",1,Zero);
        Assert.Equal("Closed",result.Status);Assert.Equal(0,result.Net);Assert.Equal(EntryAt.AddHours(72),result.ClosedAt);Assert.Equal("TimeLimit",Assert.Single(result.Legs).Reason);
    }
    [Fact]public void InvalidTargetsDirectionAndPartialFractionsCannotProduceResearchTrades()
    {
        Assert.Equal("Invalid",QuantExitEngine.Replay(Seed with{Target2=-1},TailBars,Now,"ScaleTail",1,Zero).Status);
        Assert.Equal("Invalid",QuantExitEngine.Replay(Seed with{Direction="Buy"},TailBars,Now,"ScaleTail",1,Zero).Status);
        Assert.Equal("Invalid",QuantExitEngine.Replay(Seed,TailBars,Now,"ScaleTail",1,Zero with{FirstFraction=.8m,SecondFraction=.8m}).Status);
    }
    private static Candle[] SignalBars(DateTimeOffset hour,DateTimeOffset received)
    {
        var bars=Enumerable.Range(0,337).Select(i=>{var end=hour.AddHours(i-336);var c=100+i*.02m+(decimal)Math.Sin(i/3d)*.3m;
            return new Candle("TESTUSDT",60,end.AddHours(-1),c-.03m,c+.4m,c-.4m,c,1_000_000,end.AddMilliseconds(-1),received);}).ToArray();
        bars[^1]=bars[^1] with{Open=106.7m,High=108.5m,Low=106.5m,Close=108.4m,QuoteVolume=3_000_000};return bars;
    }
    [Fact]public void HistoricalReconstructionKeepsOriginalReceiptTimesAndDoesNotChangeLiveRulesOrUseFutureCandles()
    {
        var p=new QuantParameters();Assert.Equal("bacafd3197215f69e908fec0fbd02c67c79cabb589610b5e20ebd5a5ba3e955d",QuantRules.Hash(p));
        var hour=Now.AddDays(-20);var bars=SignalBars(hour,Now.AddSeconds(-1));Assert.NotNull(QuantSignalEngine.Frame("TESTUSDT",bars,hour,p).Issue);
        var historical=QuantSignalEngine.HistoricalFrame("TESTUSDT",bars,hour,Now,p);Assert.Null(historical.Issue);Assert.All(bars,b=>Assert.Equal(Now.AddSeconds(-1),b.ReceivedTime));
        var instrument=new Instrument(1,"TESTUSDT","TEST",Now.AddYears(-1),true);var d=QuantSignalEngine.Reconstruct(instrument,historical,Now,QuantRules.Hash(p),"source",p);
        Assert.Contains(d,x=>x.Status=="PriceCandidate"&&x.Strategy=="Breakout20"&&x.Direction=="Long");Assert.All(d,x=>{Assert.Equal("HistoricalPriceStudy",x.Mode);Assert.Equal("Unknown",x.Oi.Status);Assert.Equal(Now,x.CapturedAt);});
        var future=bars[^1] with{OpenTime=hour,SourceTime=hour.AddHours(1).AddMilliseconds(-1),High=500,Close=400};
        Assert.Equal(historical.Facts,QuantSignalEngine.HistoricalFrame("TESTUSDT",bars.Append(future).ToArray(),hour,Now,p).Facts);
    }
    [Fact]public void TimeSplitsAreSequentialAndPredefineSeventyTwoHourPurging()
    {
        var request=QuantStudyEngine.Request([],new(),Now);Assert.Equal(60,(request.Through-request.From).TotalDays);Assert.Equal(new[]{"Development","Validation","Holdout"},request.Windows.Select(w=>w.Name));
        Assert.Equal(30,(request.Windows[0].Through-request.Windows[0].From).TotalDays);Assert.Equal(request.Windows[0].Through,request.Windows[1].From);Assert.Equal(request.Windows[1].Through,request.Windows[2].From);
        Assert.All(request.Windows,w=>Assert.Equal(72,(w.Through-w.LastEntryExclusive).TotalHours));
    }
    [Fact]public void FixedHistoricalRunProducesSeparateCostsAndPoliciesWithoutOriginalPaperOrFutureBaselineLeakage()
    {
        var instrument=new Instrument(1,"TESTUSDT","TEST",Now.AddYears(-1),true);var request=QuantStudyEngine.Request([instrument],new(),Now);var signalAt=request.From.AddDays(1);
        var bars=SignalBars(signalAt,Now.AddSeconds(-1)).Concat(new[]{new Candle("TESTUSDT",60,signalAt,108.4m,109,90,100,1_000_000,signalAt.AddHours(1).AddMilliseconds(-1),Now.AddSeconds(-1))}).ToArray();
        var series=new QuantStudySeries(instrument,Now,"fixture",bars,null);var hash=QuantRules.HashValue(series);var source=new QuantStudySource(instrument.Symbol,hash,bars.Length,bars[0].OpenTime,bars[^1].EndTime,"fixture",null);
        var (report,trades)=QuantStudyEngine.Run(request,[(source,series)],new(0,0,0,0,0,[]),Now,null,default);
        Assert.Equal(36,report.Summaries.Count);Assert.Equal(6,trades.Count);Assert.All(trades,t=>{Assert.Equal("Development",t.Window);Assert.Equal(signalAt,t.SignalHour);Assert.Equal("Closed",t.Conservative.Status);Assert.Equal(Now,t.ReconstructedAt);});
        Assert.Contains(report.Gates,g=>g.Name=="ProspectiveProfitEvidence"&&g.Status=="Unknown");Assert.All(report.Summaries.Where(s=>s.Window=="Holdout"),s=>Assert.Null(s.MeanNetR));
        Assert.Equal(new[]{1,2,3},trades.Select(t=>t.CostMultiplier).Distinct().Order());
    }
    [Fact]public void TooFewDaysDoesNotYieldAnInventedConfidenceIntervalOrZeroTradeProfit()
    {
        var empty=QuantStudyEngine.Summary("Holdout","Breakout20","ScaleTail",1,[],new());Assert.Null(empty.MeanNetR);Assert.Null(empty.ProfitFactor);Assert.Null(empty.MeanLower95);Assert.Null(empty.WinRate);
    }
    [Fact]public void ContentAddressedSourcesAndImmutableReportsSurviveRepositoryRestart()
    {
        var root=Path.Combine(Path.GetTempPath(),"Pullet-QuantStudy-"+Guid.NewGuid().ToString("N"));
        try
        {
            var options=Options.Create(new StorageOptions{Directory=root});var repo=new QuantStudyRepository(options);var series=new QuantStudySeries(new(1,"TESTUSDT","TEST",Now.AddYears(-1),true),Now,"fixture",TailBars,null);
            var hash=repo.SaveSeries(series);Assert.Equal(hash,repo.SaveSeries(series));Assert.Equal(hash,QuantRules.HashValue(new QuantStudyRepository(options).Series(hash)));
            Assert.Throws<InvalidDataException>(()=>repo.Series("../bad"));var path=Path.Combine(root,"QuantValidation","Inputs",hash+".json.gz");File.WriteAllText(path,"corrupt");Assert.Throws<InvalidDataException>(()=>repo.Series(hash));
        }
        finally{var full=Path.GetFullPath(root);if(!full.StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException();if(Directory.Exists(full))Directory.Delete(full,true);}
    }
    [Fact]public void ReportCommitRetriesAreIdempotentAndCannotChangeFrozenRequestsOrResults()
    {
        using var temp=new Temp();var repo=new QuantStudyRepository(Options.Create(new StorageOptions{Directory=temp.Path}));var request=QuantStudyEngine.Request([],new(),Now);
        var progress=new QuantStudyProgress(request,"Processing",Now,0,0,"test",[]);repo.SaveProgress(progress);repo.SaveProgress(progress with{Message="progress only"});
        Assert.Throws<InvalidDataException>(()=>repo.SaveProgress(progress with{Request=request with{From=request.From.AddHours(1)}}));
        var (report,trades)=QuantStudyEngine.Run(request,[],new(0,0,0,0,0,[]),Now,null,default);repo.SaveReport(report,trades);repo.SaveReport(report with{CompletedAt=Now.AddHours(1)},trades);
        var restored=new QuantStudyRepository(Options.Create(new StorageOptions{Directory=temp.Path}));Assert.Equal(Now,restored.LatestReport()!.CompletedAt);Assert.Equal(request.Id,restored.LatestProgress()!.Request.Id);
        Assert.Throws<InvalidDataException>(()=>repo.SaveReport(report with{Counts=new Dictionary<string,long>{{"changed",1}}},trades));Assert.Equal(Now,restored.LatestReport()!.CompletedAt);
    }
    [Fact]public void ValidationStatusReadsDoNotStartAJobOrDownloadHistory()
    {
        using var temp=new Temp();var rest=new CountingRest();var repo=new QuantStudyRepository(Options.Create(new StorageOptions{Directory=temp.Path}));
        var service=new QuantStudyService(null!,null!,rest,repo,null!,null!,new(),TimeProvider.System);
        for(var i=0;i<5;i++){var status=service.Status();Assert.Null(status.Progress);Assert.Null(status.Report);Assert.False(status.Running);}
        Assert.Equal(0,rest.Calls);Assert.False(Directory.Exists(temp.Path));
    }
    [Fact]public void ForwardAuditReconstructsFrozenInputsAndTreatsCorruptionAsMissingEvidence()
    {
        using var temp=new Temp();var repo=new QuantShadowRepository(Options.Create(new StorageOptions{Directory=temp.Path}));var p=new QuantParameters();var hash=QuantRules.Hash(p);var captured=Now.AddSeconds(30);
        var cohort=repo.OpenCohort(hash,p,Now.AddSeconds(-1));var bars=SignalBars(Now,Now.AddSeconds(1));var input=new QuantInputBundle("TESTUSDT",Now,bars,[]);var digest=repo.SaveInput(hash,input);
        var frame=QuantSignalEngine.Frame("TESTUSDT",bars,captured,p);var market=new QuantMarketInput(1,"TESTUSDT","TEST",true,Now.AddYears(-1),108.4m,30_000_000,5,captured.AddSeconds(-1),captured,108.4m,0,captured.AddSeconds(-1));
        var oi=QuantSignalEngine.Oi("TESTUSDT",[],Now,captured,p);var rows=QuantSignalEngine.Analyze(market,frame,oi,captured,"Forward",hash,digest,p).Select(d=>d with{InputArchived=true}).ToArray();
        repo.SaveDecision(new(cohort.Id,hash,"Forward",Now,captured,"TESTUSDT",digest,"universe",new(captured,Now,"Unknown","Unknown","Unknown",0,1,null,null,null,"fixture"),rows));
        var audit=QuantStudyEngine.Audit(repo,cohort);Assert.Equal(4,audit.Decisions);Assert.Equal(4,audit.Matches);Assert.Equal(0,audit.Mismatches);Assert.Equal(0,audit.MissingInputs);
        File.WriteAllText(Path.Combine(temp.Path,"QuantShadow",hash,"Inputs",digest+".json.gz"),"corrupt");var failed=QuantStudyEngine.Audit(repo,cohort);Assert.Equal(4,failed.Decisions);Assert.Equal(4,failed.MissingInputs);Assert.Equal(0,failed.Matches);
    }
    [Fact]public void ConfidenceIntervalsResampleWholeSignalDatesAndAreDeterministic()
    {
        var rows=Enumerable.Range(0,60).Select(i=>
        {
            var net=i/5%3==0?-1m:2m;var exit=new QuantExitResult("Closed",Now,net,0,0,1,0,0,[],"fixture");
            return new QuantStudyTrade(i.ToString(),"Holdout","TESTUSDT","Breakout20","Long","ScaleTail",1,Now.AddDays(i/5-12).AddMinutes(i%5),Now,"source",Seed,exit,exit);
        }).ToArray();
        var a=QuantStudyEngine.Summary("Holdout","Breakout20","ScaleTail",1,rows,new());var b=QuantStudyEngine.Summary("Holdout","Breakout20","ScaleTail",1,rows,new());
        Assert.Equal(a,b);Assert.Equal(12,a.SignalDays);Assert.Equal(1,a.MeanNetR);Assert.Equal(4,a.ProfitFactor);Assert.NotNull(a.MeanLower95);Assert.True(a.MeanUpper95-a.MeanLower95>=1m);
    }
    private sealed class CountingRest:IMarketRestProvider
    {
        public int Calls;private T Fail<T>(){Calls++;throw new InvalidOperationException("Read-only status cannot fetch");}
        public Task<IReadOnlyList<SymbolDefinition>> GetSymbolsAsync(CancellationToken ct)=>Fail<Task<IReadOnlyList<SymbolDefinition>>>();
        public Task<IReadOnlyList<TickerEvent>> GetTickersAsync(CancellationToken ct)=>Fail<Task<IReadOnlyList<TickerEvent>>>();
        public Task<IReadOnlyList<Candle>> GetCandlesAsync(string symbol,int intervalMinutes,int limit,DateTimeOffset? endTime,CancellationToken ct)=>Fail<Task<IReadOnlyList<Candle>>>();
    }
    private sealed class Temp:IDisposable
    {
        public string Path {get;}=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"Pullet-QuantStudy-"+Guid.NewGuid().ToString("N"));
        public void Dispose(){var full=System.IO.Path.GetFullPath(Path);if(!full.StartsWith(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()),StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException();if(Directory.Exists(full))Directory.Delete(full,true);}
    }
}
