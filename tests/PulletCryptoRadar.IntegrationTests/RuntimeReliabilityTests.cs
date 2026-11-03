using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;

namespace PulletCryptoRadar.IntegrationTests;
public sealed class RuntimeReliabilityTests
{
    private static readonly DateTimeOffset Now=DateTimeOffset.Parse("2026-10-31T03:00:00Z");
    [Theory][InlineData(true,false,1,false,0)][InlineData(false,true,2,false,0)][InlineData(false,false,1,true,15)][InlineData(false,false,2,true,30)][InlineData(false,false,3,true,60)][InlineData(false,false,4,false,0)]
    public void NormalStopsAndRepeatedFailuresDoNotRestartForever(bool requested,bool clean,int failures,bool restart,int delay)
    {var result=RuntimeRecoveryPolicy.Decide(requested,clean,failures);Assert.Equal(restart,result.Restart);Assert.Equal(delay,result.Delay.TotalSeconds);}
    [Fact]public void MissingStopProducesUncertainGapFromLastHeartbeatRatherThanInventedExactExit()
    {
        var rows=new[]{new RuntimeEvent(Now.AddMinutes(-10),1,"Starting","first"),new(Now.AddMinutes(-9),1,"Heartbeat","first"),new(Now,2,"Starting","second")};
        var gap=Assert.Single(RuntimeRecoveryPolicy.Gaps(rows));Assert.False(gap.CleanExit);Assert.Equal(Now.AddMinutes(-9),gap.LastEvidenceAt);Assert.Contains("未知",gap.Meaning);
        gap=Assert.Single(RuntimeRecoveryPolicy.Gaps(rows.Take(2).Append(new(Now.AddMinutes(-8),1,"Stopped","first")).Append(rows[2]).ToArray()));Assert.True(gap.CleanExit);
    }
    [Fact]public void StaleOrForeignSupervisorCannotBeShownAsConnectedAndPartialJournalIsVisible()
    {
        var root=Path.Combine(Path.GetTempPath(),"PulletRuntimeTest-"+Guid.NewGuid());Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root,"runtime-sessions.jsonl"),JsonSerializer.Serialize(new RuntimeEvent(Now,7,"Starting"))+"\nbroken\n");
            var state=new SupervisorState(Now,90,7,"Running",0,null,null,null,"Fixture");File.WriteAllText(Path.Combine(root,"supervisor-state.json"),JsonSerializer.Serialize(state));
            Assert.True(RuntimeReliabilityService.Read(root,Now,Now,7).SupervisorFresh);
            var result=RuntimeReliabilityService.Read(root,Now.AddSeconds(50),Now,7);Assert.False(result.SupervisorFresh);Assert.Contains(result.Notices,n=>n.Contains("未完整解析"));
            Assert.False(RuntimeReliabilityService.Read(root,Now,Now,8).SupervisorFresh);
        }
        finally{Directory.Delete(root,true);}
    }
    private static TokenMonitorTarget Target=>new("aio-bsc","olaxbt","AIO","binance-smart-chain","0x"+new string('1',40));
    private static TokenMonitorSnapshot Snapshot(DateTimeOffset at)=>new(Guid.NewGuid(),Target.Id,Target.ContractAddress,10,"hash",at,at,0,"1000",[],null,new(1,11,"hash",at,at,"same"),[],10);
    [Fact]public void DailySlotsUseShanghaiDayAndMissingSnapshotIsUnknownNotZeroRelease()
    {
        var date=new DateOnly(2026,10,31);var first=Snapshot(Now.AddHours(-1));
        var result=DailyResearchEngine.Monitor(Target,[first,first with{Id=Guid.NewGuid(),SourceTime=Now.AddHours(-1).AddMinutes(1),ReceivedTime=Now.AddHours(-1).AddMinutes(1)}],null,date,Now);
        Assert.Equal(2,result.Snapshots);Assert.Equal(1,result.ObservedHalfHourSlots);Assert.Equal(22,result.CompletedDaySlots);Assert.Null(result.RecordedReleaseDelta);Assert.False(result.HasComparable24hBaseline);
        var missing=DailyResearchEngine.Monitor(Target,[],null,date,Now);Assert.Null(missing.ObservedTransfers);Assert.Contains(missing.Gaps,g=>g.Contains("没有有效"));
        Assert.Equal(0,DailyResearchEngine.Monitor(Target,[first with{SourceTime=Now.AddDays(-1),ReceivedTime=Now.AddDays(-1)}],null,date,Now).Snapshots);
    }
    [Fact]public void DailyReleaseDeltaRequiresComparableCodeAndDoesNotTreatAllowanceAsRealRelease()
    {
        var vest=new VestingSnapshot("vesting","samecode",Target.ContractAddress,"admin",true,true,"900",[new(0,"team","100","10","recipient",86400,Now,"10")],"https://source.example/","Fixture");
        var first=Snapshot(Now.AddHours(-1)) with{Vesting=vest};var second=Snapshot(Now) with{Vesting=vest with{Allocations=[vest.Allocations[0] with{RawReleased="20"}]}};
        var r=DailyResearchEngine.Monitor(Target,[first,second],null,new(2026,10,31),Now);Assert.Equal("10",r.RecordedReleaseDelta);Assert.Null(r.EarliestOrdinaryRelease7Days);
        Assert.Null(DailyResearchEngine.Monitor(Target,[first,second with{Vesting=second.Vesting! with{RuntimeSha256="changed"}}],null,new(2026,10,31),Now).RecordedReleaseDelta);
    }
    private static TokenCheckReport Report()=>TokenCheckEngine.Build(new("olaxbt","aio","OlaXBT",new Dictionary<string,string>{{Target.Platform,Target.ContractAddress}}),new("olaxbt",Target.Platform,Target.ContractAddress),true,"Fixture",null,null,null,[],[],null,[],[],Now.AddDays(-2),"AIOUSDT");
    [Fact]public void OutcomeGroupsKeepNegativeVerdictsMissingAndNotDueWithoutCherryPicking()
    {
        var report=Report() with{Verdict="暂不考虑当前买入"};var f=new TokenFollowup(report.Id,Now,"Fixture",[new(24,report.AsOf.AddHours(24),"已观察",Now.AddDays(-1),120,20,false,"Fixture"),new(72,report.AsOf.AddHours(72),"未到观察时间",null,null,null,null,"Fixture")]);
        var groups=DailyResearchEngine.Outcomes([report],[f],Now);Assert.Equal(3,groups.Count);Assert.Equal(20,groups.Single(g=>g.Hours==24).MeanPriceChange);Assert.Equal(1,groups.Single(g=>g.Hours==72).NotDue);
        Assert.All(groups,g=>Assert.Equal("暂不考虑当前买入",g.Verdict));Assert.Null(DailyResearchEngine.Outcomes([report],[],Now).Single(g=>g.Hours==24).MeanPriceChange);
        var unpriced=f with{Points=[new(72,report.AsOf.AddHours(72),"无可比基准",null,null,null,null,"Fixture")]};
        Assert.Equal(1,DailyResearchEngine.Outcomes([report],[unpriced],Now).Single(g=>g.Hours==72).NoBenchmark);
    }
    [Fact]public void DailyArchiveKeepsOriginalQuarterHourSnapshotInsteadOfOverwritingIt()
    {
        var root=Path.Combine(Path.GetTempPath(),"PulletDailyTest-"+Guid.NewGuid());
        try
        {
            var repo=new DailyResearchRepository(Options.Create(new StorageOptions{Directory=root}));var review=new DailyResearchReview("Daily-Research-1",new(2026,10,31),Now,1,[],[],[],[]);
            repo.Save(review);repo.Save(review with{Reports=99,AsOf=Now.AddMinutes(1)});Assert.Equal(1,repo.Latest(review.Date)!.Reports);
            repo.Save(review with{Reports=2,AsOf=Now.AddMinutes(16)});Assert.Equal(2,repo.Latest(review.Date)!.Reports);
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    [Fact]public async Task IsolatedSupervisorRestartsAnUnexpectedExitButHonorsStopDuringRecovery()
    {
        if(!OperatingSystem.IsWindows())return;
        var repo=FindRoot();var config=AppContext.BaseDirectory.Contains("Release")?"Release":"Debug";
        var supervisor=Path.Combine(repo,"src","PulletCryptoRadar.Supervisor","bin",config,"net10.0","PulletCryptoRadar.Supervisor.exe");
        var fixture=Path.Combine(repo,"tests","PulletCryptoRadar.SupervisorFixture","bin",config,"net10.0");
        var root=Path.Combine(Path.GetTempPath(),"PulletSupervisorTest-"+Guid.NewGuid());Directory.CreateDirectory(root);
        foreach(var f in Directory.EnumerateFiles(fixture))File.Copy(f,Path.Combine(root,Path.GetFileName(f)));
        Process? process=null;
        try
        {
            process=Process.Start(new ProcessStartInfo(supervisor){ArgumentList={root,"--background"},UseShellExecute=false,CreateNoWindow=true})!;
            var deadline=DateTime.UtcNow.AddSeconds(30);var starts=Path.Combine(root,"fixture-starts.txt");
            while(DateTime.UtcNow<deadline&&( !File.Exists(starts)||File.ReadAllLines(starts).Length<2))await Task.Delay(100);
            Assert.True(File.Exists(starts));Assert.Equal(2,File.ReadAllLines(starts).Length);
            File.WriteAllText(Path.Combine(root,"Logs","supervisor-stop.request"),"fixture stop");
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12));await Task.Delay(700);
            Assert.Equal(2,File.ReadAllLines(starts).Length);var events=File.ReadAllText(Path.Combine(root,"Logs","supervisor-events.jsonl"));Assert.Contains("UnexpectedExit",events);Assert.Contains("17",events);
        }
        finally
        {
            if(process is {HasExited:false}){File.WriteAllText(Path.Combine(root,"Logs","supervisor-stop.request"),"cleanup");await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));}
            process?.Dispose();Directory.Delete(root,true);
        }
    }
    [Fact]public async Task IsolatedSupervisorDoesNotRestartARecordedCleanExit()
    {
        if(!OperatingSystem.IsWindows())return;
        var repo=FindRoot();var config=AppContext.BaseDirectory.Contains("Release")?"Release":"Debug";var root=Path.Combine(Path.GetTempPath(),"PulletCleanSupervisorTest-"+Guid.NewGuid());Directory.CreateDirectory(root);
        foreach(var f in Directory.EnumerateFiles(Path.Combine(repo,"tests","PulletCryptoRadar.SupervisorFixture","bin",config,"net10.0")))File.Copy(f,Path.Combine(root,Path.GetFileName(f)));
        File.WriteAllText(Path.Combine(root,"fixture-clean"),"clean");
        try
        {
            using var process=Process.Start(new ProcessStartInfo(Path.Combine(repo,"src","PulletCryptoRadar.Supervisor","bin",config,"net10.0","PulletCryptoRadar.Supervisor.exe")){ArgumentList={root,"--background"},UseShellExecute=false,CreateNoWindow=true})!;
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));Assert.Single(File.ReadAllLines(Path.Combine(root,"fixture-starts.txt")));
            var state=JsonSerializer.Deserialize<SupervisorState>(File.ReadAllText(Path.Combine(root,"Logs","supervisor-state.json")))!;Assert.Equal("Stopped",state.Phase);Assert.Equal(0,state.LastExitCode);
        }
        finally{Directory.Delete(root,true);}
    }
    private static string FindRoot(){var p=new DirectoryInfo(AppContext.BaseDirectory);while(p is not null&&!File.Exists(Path.Combine(p.FullName,"PulletCryptoRadar.sln")))p=p.Parent;return p?.FullName??throw new DirectoryNotFoundException("repo");}
}
