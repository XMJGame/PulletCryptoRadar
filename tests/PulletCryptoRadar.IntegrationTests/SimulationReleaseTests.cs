using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;

namespace PulletCryptoRadar.IntegrationTests;

public sealed class SimulationReleaseTests:IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"Pullet-Simulation-Release-"+Guid.NewGuid());
    private string PathFor(string name)=>Path.Combine(root,name);
    private string Server(bool pending=false)
    {
        var server=PathFor("server");Directory.CreateDirectory(server);File.WriteAllText(Path.Combine(server,"appsettings.json"),"{}");
        var db=new SqliteDatabase(Options.Create(new StorageOptions{Directory=Path.Combine(server,"Data")}));db.Initialize();
        var (input,sources)=QuantPortfolioValidationTests.Fixture();var state=input.State with{Revision=1};
        var signals=new QuantShadowRepository(Options.Create(new StorageOptions{Directory=Path.Combine(server,"Data")}));foreach(var source in sources.Values)signals.SaveInput(source.Review.Control.RulesHash,source.Signal!);
        if(pending)state=state with{Positions=[],Pending=[state.Positions[0].Intent]};
        db.SavePaper(new(state,0,[]));return server;
    }
    [Fact]public void OnlineBackupAndOfflineRecoveryPreserveSourceAndRejectSecondCommit()
    {
        var server=Server();var source=Path.Combine(server,"Data","PulletCryptoRadar.db");var before=SimulationBackup.Ledger(source);
        var backup=SimulationBackup.Create(server,PathFor("backup"));Assert.Equal(QuantRules.HashValue(before),QuantRules.HashValue(backup.Ledger));
        var proof=SimulationBackup.RestoreTest(PathFor("backup"),PathFor("restored"));Assert.True(proof.RestoredStateEqual&&proof.Repeatable&&proof.CommittedCopyEqual&&proof.DuplicateCommitRejected);
        Assert.True(proof.CashPreserved&&proof.PositionQuantitiesPreserved&&proof.NoNewTradesOrFunding);
        Assert.Equal(QuantRules.HashValue(before),QuantRules.HashValue(SimulationBackup.Ledger(source)));SimulationBackup.Verify(PathFor("backup"));
    }
    [Fact]public void PendingIntentSurvivesRecoveryWithoutInventedMarketFills()
    {
        var server=Server(true);SimulationBackup.Create(server,PathFor("backup"));var proof=SimulationBackup.RestoreTest(PathFor("backup"),PathFor("restored"));
        Assert.True(proof.NoNewTradesOrFunding&&proof.DuplicateCommitRejected);Assert.Equal(1,SimulationBackup.Ledger(Path.Combine(PathFor("restored"),"Data","PulletCryptoRadar.db")).Pending);
    }
    [Fact]public void BackupCannotOverwriteServerItsChildOrExistingDirectory()
    {
        var server=Server();Assert.Throws<IOException>(()=>SimulationBackup.Create(server,server));Assert.Throws<IOException>(()=>SimulationBackup.Create(server,Path.Combine(server,"Data","backup")));
        Directory.CreateDirectory(PathFor("existing"));Assert.Throws<IOException>(()=>SimulationBackup.Create(server,PathFor("existing")));
        SimulationBackup.Create(server,PathFor("backup"));Assert.Throws<IOException>(()=>SimulationBackup.RestoreTest(PathFor("backup"),server));
    }
    [Fact]public void MissingOrChangedBackupIsRejectedBeforeRestoreDirectoryIsCreated()
    {
        var server=Server();SimulationBackup.Create(server,PathFor("backup"));File.AppendAllText(Path.Combine(PathFor("backup"),"appsettings.json"),"changed");
        Assert.Throws<InvalidDataException>(()=>SimulationBackup.RestoreTest(PathFor("backup"),PathFor("restored")));Assert.False(Directory.Exists(PathFor("restored")));
    }
    [Fact]public void ManifestCannotEscapeItsBackupRoot()
    {
        var server=Server();var backup=SimulationBackup.Create(server,PathFor("backup"));backup=backup with{Files=backup.Files.Append(new("../outside.json",0,"bad")).ToArray()};
        File.WriteAllText(Path.Combine(PathFor("backup"),"backup.json"),JsonSerializer.Serialize(backup));Assert.Throws<InvalidDataException>(()=>SimulationBackup.Verify(PathFor("backup")));
    }
    [Fact]public void ObservationReadsNewPersistedEquityRowsUsingTheConfiguredDecoderLimit()
    {
        var server=Server();var db=new SqliteDatabase(Options.Create(new StorageOptions{Directory=Path.Combine(server,"Data")}));var state=new PaperRepository(db).Restore()!;var at=state.UpdatedAt;
        Directory.CreateDirectory(Path.Combine(server,"Logs"));var journal=Path.Combine(server,"Logs","runtime-sessions.jsonl");
        File.WriteAllLines(journal,[JsonSerializer.Serialize(new RuntimeEvent(at.AddSeconds(-2),7,"Starting","session")),JsonSerializer.Serialize(new RuntimeEvent(at,7,"Heartbeat","session"))]);
        var baseline=SimulationObservation.Start(server,at,7);var batch=PaperEquityRecorder.Attach(state,new(state with{Revision=2,UpdatedAt=at.AddMinutes(1)},1,[]),[],[]);db.SavePaper(batch);
        File.AppendAllText(journal,JsonSerializer.Serialize(new RuntimeEvent(at.AddMinutes(1),7,"Heartbeat","session"))+Environment.NewLine);
        var report=SimulationObservation.Check(baseline,at.AddMinutes(1),true,true);Assert.Equal(1,report.EquitySamples);Assert.Equal(0,report.MissingOrChangedRows);
    }
    private static readonly DateTimeOffset At=DateTimeOffset.Parse("2026-11-01T08:00:00+08:00");
    private static SimulationObservationBaseline Baseline=>new(SimulationObservation.Version,At,At.AddHours(24),"unused",7,"session","settings","control",new Dictionary<string,string>{{"server.exe","binary"}},[new("PaperTrade","trade","row")],1,0,0,[]);
    private static RuntimeEvent[] Heartbeats=>Enumerable.Range(1,2880).Select(i=>new RuntimeEvent(At.AddSeconds(30*i),7,"Heartbeat","session")).ToArray();
    private static PaperEquitySample[] Samples=>Enumerable.Range(0,1441).Select(i=>PaperEquityRecorder.Capture(new(){UpdatedAt=At.AddMinutes(i),Cash=1000,Revision=i},[],[],At.AddMinutes(i))).ToArray();
    private static SimulationObservationReport Evaluate(DateTimeOffset now,IReadOnlyList<RuntimeEvent>? events=null,IReadOnlyList<PaperEquitySample>? samples=null,IReadOnlyList<SimulationSavedRow>? rows=null,string settings="settings")=>
        SimulationObservation.Evaluate(Baseline,now,events??Heartbeats,0,samples??Samples,rows??Baseline.Rows,settings,"control",Baseline.Binaries,true,true);
    [Fact]public void TwentyFourHoursCannotBeApprovedEarly()
    {var report=Evaluate(At.AddHours(24).AddTicks(-1));Assert.Equal("Collecting",report.Status);Assert.Contains(report.Gates,g=>g.Name=="24小时窗口"&&g.Status=="Collecting");}
    [Fact]public void CompleteObservationKeepsProfitabilityAsAnIndependentUnknown()
    {var report=Evaluate(At.AddHours(24));Assert.Equal("Observed24Hours",report.Status);Assert.Equal(2880,report.Heartbeats);Assert.Contains(report.Gates,g=>g.Name=="策略盈利"&&g.Status=="Unknown");}
    [Fact]public void AQuietDayDoesNotAutomaticallyCloseAKnownNativeCrash()
    {
        var b=Baseline with{KnownIssues=["Unresolved native encoder crash"]};var report=SimulationObservation.Evaluate(b,At.AddHours(24),Heartbeats,0,Samples,b.Rows,b.SettingsHash,b.ControlHash,b.Binaries,true,true);
        Assert.Equal("CompletedWithEvidenceGaps",report.Status);Assert.Contains(report.Gates,g=>g.Name=="已知原生异常"&&g.Status=="Unknown");
    }
    [Fact]public void BothBoundaryAndInteriorHeartbeatGapsAreCounted()
    {
        Assert.Equal("NeedsInvestigation",Evaluate(At.AddHours(24),[]).Status);
        var report=Evaluate(At.AddHours(24),Heartbeats.Where(r=>r.At<At.AddHours(12)||r.At>At.AddHours(12).AddMinutes(5)).ToArray());Assert.True(report.LongestHeartbeatGapSeconds>300);Assert.Equal("NeedsInvestigation",report.Status);
    }
    [Fact]public void RestartAndChangedOldLedgerRowsCannotMasqueradeAsContinuousObservation()
    {
        Assert.Equal("NeedsInvestigation",Evaluate(At.AddHours(24),Heartbeats.Append(new(At.AddHours(12),8,"Starting","another")).ToArray()).Status);
        var report=Evaluate(At.AddHours(24),rows:[new("PaperTrade","trade","changed")]);Assert.Equal(1,report.MissingOrChangedRows);Assert.Equal("NeedsInvestigation",report.Status);
        Assert.Equal("NeedsInvestigation",Evaluate(At.AddHours(24),settings:"changed").Status);
    }
    [Fact]public void MissingMarksAndFundingRemainEvidenceGapsInsteadOfZeroCost()
    {
        var samples=Samples;samples[10]=samples[10] with{Equity=null,MarksFresh=false,FundingObserved=false,Reconciled=false,LedgerExpected=null,Difference=null};var report=Evaluate(At.AddHours(24),samples:samples);
        Assert.Equal("CompletedWithEvidenceGaps",report.Status);Assert.Equal(1,report.MissingMarks);Assert.Equal(1,report.MissingFunding);Assert.Equal(1440,report.ValidEquitySamples);
        Assert.Equal(0,report.LedgerFailures);Assert.Equal(1,report.ReconciliationUnknown);Assert.Contains(report.Gates,g=>g.Name=="账目守恒"&&g.Status=="Unknown");
    }
    [Fact]public void AMeasurableLedgerMismatchIsNotHiddenByAConcurrentStaleMark()
    {
        var samples=Samples;samples[10]=samples[10] with{Equity=null,MarksFresh=false,Reconciled=false,Difference=.01m};var report=Evaluate(At.AddHours(24),samples:samples);
        Assert.Equal("NeedsInvestigation",report.Status);Assert.Equal(1,report.LedgerFailures);Assert.Equal(0,report.ReconciliationUnknown);Assert.Contains(report.Gates,g=>g.Name=="账目守恒"&&g.Status=="Fail");
    }
    [Fact]public void MinuteCoverageDoesNotCountEventSamplesAsIndependentMinutes()
    {
        var samples=Samples;var report=Evaluate(At.AddHours(24),samples:samples.Concat([samples[10] with{Revision=99999,CapturedAt=samples[10].CapturedAt.AddSeconds(1)}]).ToArray());
        Assert.Equal(samples.Length+1,report.EquitySamples);Assert.Equal(samples.Length,report.EquityMinuteSlots);
        samples[20]=samples[20] with{Reconciled=false};Assert.Equal("NeedsInvestigation",Evaluate(At.AddHours(24),samples:samples).Status);
    }
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();var path=Path.GetFullPath(root);if(!path.StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase))throw new IOException("Unexpected test root");
        if(Directory.Exists(path))Directory.Delete(path,true);
    }
}
