using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;
namespace PulletCryptoRadar.IntegrationTests;

public sealed class PaperActivityTests
{
    private static readonly DateTimeOffset Now=new(2026,10,31,14,20,0,TimeSpan.Zero);
    private static DateTimeOffset Hour=>Now.AddMinutes(-20);
    private static ForwardDecision Decision(string id="one",string status="NoSignal")=>new(id,"TESTUSDT","A",Hour,Hour.AddMinutes(1),status,"frozen",337,1,2,8,5,Hour.AddMinutes(-5),Hour,"OI",new(100,97,1,10,3,106,112,"projection","projection",1),PaperAnalysis.RulesHash);
    private static PaperState State()=>new(){Revision=1,StartedAt=Now.AddDays(-2),ExecutionStartedAt=Now.AddDays(-2),UpdatedAt=Now,Enabled=true,SignalHour=Hour,Rules=StructuralPaperEngine.Rules()};
    [Fact] public void AuditDeduplicatesRepeatedPartialHourBatchesAndExcludesFutureOrExpiredSignals()
    {
        var d=Decision();var window=new PaperAuditWindow([new(1,Hour.AddMinutes(1),[d],[]),new(2,Hour.AddMinutes(2),[d,Decision("old") with{DecisionAt=Now.AddHours(-25),CapturedAt=Now.AddHours(-25)},Decision("future") with{CapturedAt=Now.AddSeconds(1)}],[])],false);
        var result=PaperActivityEngine.Build(State() with{SignalObservations=[d]},window,Now);
        Assert.Equal(1,result.Analyses);Assert.Equal(1,result.Hours);Assert.Equal(1,result.UniqueSymbols);Assert.Equal("本轮没有结构候选",result.Status);
    }
    [Fact] public void LastHourStatusDoesNotConfuseHistoricalCandidatesWithCurrentReadiness()
    {
        var past=Decision("past","Candidate") with{DecisionAt=Hour.AddHours(-1),CapturedAt=Hour.AddMinutes(-59),OiSourceTime=Hour.AddMinutes(-65),OiReceivedTime=Hour.AddHours(-1)};
        var result=PaperActivityEngine.Build(State() with{SignalObservations=[past,Decision()]},new([],false),Now);
        Assert.Equal(1,result.Candidates);Assert.Equal(1,result.OiEligibleCandidates);Assert.Equal("本轮没有结构候选",result.Status);
        Assert.Equal("账本停更",PaperActivityEngine.Build(State() with{UpdatedAt=Now.AddMinutes(-1)},new([],false),Now).Status);
        Assert.Equal("分析待完成",PaperActivityEngine.Build(State() with{SignalHour=Hour.AddHours(-1)},new([],false),Now).Status);
        Assert.Equal("本小时分析进行中",PaperActivityEngine.Build(State() with{UpdatedAt=Hour.AddMinutes(2),AnalysisHour=Hour,SignalHour=Hour.AddHours(-1)},new([],false),Hour.AddMinutes(2)).Status);
        Assert.Equal("分析待完成",PaperActivityEngine.Build(State() with{SignalHour=null,AnalysisHour=Hour},new([],false),Now).Status);
    }
    [Fact] public void ExecutionCountsDeduplicateAuditAndCurrentLedgerWithoutCountingAnalysesAsTrades()
    {
        var e=new PaperDecision(Now,"TESTUSDT","实验模拟开仓","x","key");
        var result=PaperActivityEngine.Build(State() with{Decisions=[e]},new([new(1,Now,[],[e]),new(2,Now,[],[e])],false),Now);
        Assert.Equal(1,result.Openings);Assert.Equal(0,result.Analyses);Assert.Equal(0,result.IntentCreated);
    }
    [Fact] public void PersistentAuditDeserializesSubsetAndPreservesConditionsWithoutWritingAccount()
    {
        var root=Path.Combine(Path.GetTempPath(),"PulletActivity-"+Guid.NewGuid());
        try
        {
            var db=new SqliteDatabase(Options.Create(new StorageOptions{Directory=root}));db.Initialize();
            var state=State() with{AnalysisHour=Hour,SignalObservations=[Decision() with{Conditions=[new("v","volume",.8m,">=1.2",false)]}],Decisions=[new(Now,"账户","双向分析快照","save")]};
            db.SavePaper(new(state,0,[]));var repo=new PaperRepository(db);var audit=repo.AnalysisAudit(Now.AddHours(-24),Now);
            Assert.False(audit.Truncated);Assert.Single(audit.Records);Assert.Single(audit.Records[0].Signals[0].Conditions);
            Assert.Equal(1,repo.Restore()!.Revision);Assert.Equal(1000,repo.Restore()!.Cash);
            Assert.Empty(repo.AnalysisAudit(Now.AddSeconds(1),Now.AddHours(1)).Records);
        }
        finally{SqliteConnection.ClearAllPools();if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    [Fact] public void ConditionRefactorPreservesAllFourOriginalPredicatesIncludingFlatBarsAndStrictBreakoutBoundary()
    {
        var random=new Random(1307);var r=new ForwardRules();
        for(var i=0;i<2500;i++)
        {
            decimal lo=100,hi=i%7==0?100:100+random.Next(1,60),c=80+random.Next(0,80),l=c-3,h=i%5==0?l:c+random.Next(0,8),ret=(random.Next(-50,51))/100m,v=random.Next(0,40)/10m;
            bool Check(string s,string side)=>SignalConditionEngine.Check(s,side,lo,hi,c,l,h,ret,v,r).All(g=>g.Passed);
            Assert.Equal(hi/lo-1<=r.CompressionMax&&c>=lo+(hi-lo)*.7m&&c<=hi&&ret is >=0 and <=.12m&&v>=r.VolumeRatioA,Check("A","Long"));
            Assert.Equal(ret is >=.08m and <=.4m&&c>hi*(1+r.BreakoutBuffer)&&v>=r.VolumeRatioB&&h>l&&(c-l)/(h-l)>=.7m,Check("B","Long"));
            Assert.Equal(hi/lo-1<=.12m&&c<=lo+(hi-lo)*.3m&&c>=lo&&ret is >=-.12m and <=0&&v>=1.2m,Check("A","Short"));
            Assert.Equal(ret is >=-.4m and <=-.08m&&c<lo*.998m&&v>=1.5m&&h>l&&(c-l)/(h-l)<=.3m,Check("B","Short"));
        }
        Assert.False(SignalConditionEngine.Check("B","Long",90,100,100.2m,98,101,.1m,2,r).Single(g=>g.Key=="Break72h").Passed);
    }
}
