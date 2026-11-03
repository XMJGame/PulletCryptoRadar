using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;

namespace PulletCryptoRadar.IntegrationTests;

public sealed class PaperHoldingTests:IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"Pullet-Holding-"+Guid.NewGuid());
    private static readonly DateTimeOffset Now=new(2026,10,29,12,0,0,TimeSpan.Zero);
    private static PaperState Empty()=>new(){Revision=1,StartedAt=Now.AddHours(-1),UpdatedAt=Now.AddSeconds(-5),Rules=StructuralPaperEngine.Rules()};
    private static PaperPosition Position(string direction="Long")=>new(Guid.NewGuid(),"signal",1,"TESTUSDT",Now,100,1,10,10,.05m,0,Now,100,Now,95,106,new("signal",Guid.Empty,1,"TESTUSDT",Now,null))
        {InitialQuantity=1,Direction=direction,Bucket=1};
    private static MarketView Quote()=>new(new(1,"TESTUSDT","TEST",Now.AddDays(-90),true),100,10000000,1,100,.001m,Now,Now,Now,200,200,200,
        new(){Derivatives=new(){OpenInterestQuantity=new(123,DataStatus.Valid,Now.AddMinutes(-5),"FixtureOI")}});
    private static Dictionary<int,MarketView> Markets()=>new(){{1,Quote()}};
    private static PaperBatch Open(PaperState before,PaperPosition p)=>new(before with{Revision=before.Revision+1,UpdatedAt=Now,Cash=989.95m,Positions=[p]},before.Revision,[]);
    [Fact]public void OpeningRecordsMarketAndPlanContextWithOriginalSourceTimes()
    {
        var before=Empty();var p=Position();var result=PaperHoldingRecorder.Attach(before,Open(before,p),Markets());var sample=Assert.Single(result.HoldingSnapshots);
        Assert.Equal("开仓后",sample.Event);Assert.Equal(p.Id,sample.PositionId);Assert.Equal(123,sample.Market!.Features.Derivatives.OpenInterestQuantity.Value);
        Assert.Equal(Now.AddMinutes(-5),sample.Market.Features.Derivatives.OpenInterestQuantity.SourceTime);Assert.True(sample.DataFresh);
        Assert.Equal(999.95m,sample.AccountEquity);Assert.Equal(Now,result.State.LastHoldingSampleAt);
    }
    [Fact]public void MinuteCadenceDoesNotRecordEveryFiveSecondTick()
    {
        var p=Position();var before=Empty() with{Positions=[p],LastHoldingSampleAt=Now};
        var first=new PaperBatch(before with{Revision=2,UpdatedAt=Now.AddSeconds(5)},1,[]);
        Assert.Empty(PaperHoldingRecorder.Attach(before,first,Markets()).HoldingSnapshots);
        var minute=first with{State=first.State with{UpdatedAt=Now.AddSeconds(60)}};
        Assert.Single(PaperHoldingRecorder.Attach(before,minute,Markets()).HoldingSnapshots);
    }
    [Fact]public void StaleDataIsRetainedButDoesNotInventCurrentPnl()
    {
        var p=Position();var before=Empty() with{Positions=[p]};
        var batch=new PaperBatch(before with{Revision=2,UpdatedAt=Now.AddMinutes(5)},1,[]);
        var sample=Assert.Single(PaperHoldingRecorder.Attach(before,batch,Markets()).HoldingSnapshots);
        Assert.False(sample.DataFresh);Assert.Null(sample.MarkPnl);Assert.Null(sample.AccountEquity);Assert.Equal(Now,sample.Market!.PriceSourceTime);
    }
    [Fact]public void ShortMarkPnlHasCorrectSignAndCloseIsExplicitlyBeforeExitContext()
    {
        var p=Position("Short");var before=Empty();var markets=Markets();markets[1]=Quote() with{MarkPrice=90};
        Assert.Equal(10,Assert.Single(PaperHoldingRecorder.Attach(before,Open(before,p),markets).HoldingSnapshots).MarkPnl);
        var held=before with{Positions=[p],LastHoldingSampleAt=Now};
        var trade=new PaperTrade(Guid.NewGuid(),"signal",1,"TESTUSDT",Now,Now,100,90,1,10,10,10,0,0,10,"exit",false,held.Rules.Version,p.Intent){PositionId=p.Id,Direction="Short"};
        var close=new PaperBatch(held with{Revision=2,UpdatedAt=Now.AddSeconds(5),Positions=[]},1,[trade]);
        var sample=Assert.Single(PaperHoldingRecorder.Attach(held,close,markets).HoldingSnapshots);
        Assert.Contains("退出前",sample.Event);Assert.Single(sample.Executions);Assert.Equal(1,sample.Position.Quantity);
    }
    [Fact]public void EmptyAccountDoesNotCreateFakeHoldingHistory()
    {var before=Empty();Assert.Empty(PaperHoldingRecorder.Attach(before,new(before with{Revision=2,UpdatedAt=Now},1,[]),Markets()).HoldingSnapshots);}
    [Fact]public void SnapshotsCommitAtomicallyRestoreAndPaginateWithoutRewriting()
    {
        var db=new SqliteDatabase(Options.Create(new StorageOptions{Directory=root}));db.Initialize();var before=Empty();db.SavePaper(new(before,0,[]));
        var p=Position();var open=PaperHoldingRecorder.Attach(before,Open(before,p),Markets());db.SavePaper(open);
        var held=open.State;var next=PaperHoldingRecorder.Attach(held,new(held with{Revision=3,UpdatedAt=Now.AddMinutes(1)},2,[]),Markets());db.SavePaper(next);
        var repo=new PaperRepository(db);Assert.Equal(2,repo.HoldingSnapshotCount());
        var recent=Assert.Single(repo.HoldingHistory(p.Id,1));Assert.Equal(Now.AddMinutes(1),recent.CapturedAt);
        var old=Assert.Single(repo.HoldingHistory(p.Id,1,recent.CapturedAt.ToUnixTimeMilliseconds()));Assert.Equal(Now,old.CapturedAt);
        Assert.Throws<MappingConflictException>(()=>db.SavePaper(next));Assert.Equal(2,repo.HoldingSnapshotCount());
        Assert.Equal(next.State.LastHoldingSampleAt,repo.Restore()!.LastHoldingSampleAt);
        var invalid=next with{State=next.State with{Revision=4,UpdatedAt=Now.AddMinutes(2)},ExpectedRevision=3};
        Assert.Throws<InvalidDataException>(()=>db.SavePaper(invalid));Assert.Equal(3,repo.Restore()!.Revision);Assert.Equal(2,repo.HoldingSnapshotCount());
    }
    public void Dispose(){SqliteConnection.ClearAllPools();if(Directory.Exists(root))Directory.Delete(root,true);}
}
