using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;
using PulletCryptoRadar.Scanner;

namespace PulletCryptoRadar.IntegrationTests;

public sealed class WatchStorageTests:IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"Pullet-Watch-"+Guid.NewGuid());
    private static readonly DateTimeOffset Now=new(2026,10,29,8,0,0,TimeSpan.Zero);
    private SqliteDatabase Db(){var db=new SqliteDatabase(Options.Create(new StorageOptions{Directory=_root}));db.Initialize();return db;}
    private static int Asset(SqliteDatabase db)=>db.Synchronize([new("ARIAUSDT","ARIA",Now.AddDays(-30))])[0].AssetId;
    private static WatchAsset Config(int id)=>new(id,true,.4m,.1m,null,.05m,.12m,true,[],1,Now,Now);
    private static WatchInput Input(WatchAsset config)
    {
        var market=new MarketView(new(config.AssetId,"ARIAUSDT","ARIA",Now,true),.088m,1_000_000,3,null,null,Now,Now,null,0,0,0,new());
        return new(config,SpeculativeService.Binance(market,Now),null,null,null);
    }
    private static WatchCheckpoint Point(WatchAsset config)=>new WatchStrategyEngine(new()).Advance(Input(config),new WatchStrategyEngine(new()).Evaluate(Input(config),Now),null,Now).Checkpoint with{ParametersJson=JsonSerializer.Serialize(new WatchOptions())};
    [Fact] public void ConfigCasPreservesExactDecimalsAndAudit()
    {
        var db=Db();var config=Config(Asset(db))with{CostPrice=.400000000000000001m};db.SaveWatchConfig(config,0);
        Assert.Throws<MappingConflictException>(()=>db.SaveWatchConfig(config with{Revision=2,CostPrice=9},0));
        Assert.Equal(JsonSerializer.Serialize(config),JsonSerializer.Serialize(new WatchRepository(db).Configs()[0]));using var c=db.Open();Assert.Equal(1,c.QuerySingle<int>("SELECT COUNT(*) FROM WatchAssetAudit"));
    }
    [Fact] public void StaleConfigRollsBackCheckpointAndPersonalOutbox()
    {
        var db=Db();var config=Config(Asset(db));db.SaveWatchConfig(config,0);var point=Point(config);
        db.SaveWatchConfig(config with{Revision=2},1);
        var notification=new NotificationRecord(Guid.NewGuid(),"watch-fixture",NotificationKind.WatchListCritical,null,config.AssetId,"ARIAUSDT",MoveDirection.Down,SignalGrade.Unknown,null,"CriticalRisk",Now,Now.AddMinutes(2),NotificationStatus.Pending,"Fixture","Fixture");
        Assert.Throws<MappingConflictException>(()=>db.SaveWatchRun(new(Guid.NewGuid(),Now,[point],[notification],[],"{}")));
        Assert.Empty(new WatchRepository(db).Checkpoints());using var c=db.Open();Assert.Equal(0,c.QuerySingle<int>("SELECT COUNT(*) FROM AlertEvent"));
    }
    [Fact] public void CheckpointAndDedupOutboxCommitTogetherAndSurviveRestore()
    {
        var db=Db();var config=Config(Asset(db));db.SaveWatchConfig(config,0);var point=Point(config);var engine=new WatchStrategyEngine(new());
        var critical=engine.Advance(point.Input,point.Result with{State=StrategyState.CriticalRisk,Risk=SignalRisk.Critical},null,Now);
        var run=new WatchRun(Guid.NewGuid(),Now,[critical.Checkpoint],[critical.Notification!],[],"{}");db.SaveWatchRun(run);db.SaveWatchRun(run);
        var restored=Assert.Single(new WatchRepository(db).Checkpoints());Assert.Equal(critical.Checkpoint.LastNotifiedCondition,restored.LastNotifiedCondition);
        Assert.Null(engine.Advance(restored.Input,restored.Result,restored,Now.AddSeconds(15)).Notification);
        using var c=db.Open();Assert.Equal(1,c.QuerySingle<int>("SELECT COUNT(*) FROM AlertEvent"));
    }
    [Fact] public void ClosedBinanceWatchCandlesAreIdempotentAndInvalidOrFutureCandlesIgnored()
    {
        var db=Db();var config=Config(Asset(db));db.SaveWatchConfig(config,0);var candle=new Candle("ARIAUSDT",1,Now.AddMinutes(-1),.08m,.1m,.07m,.088m,100,Now,Now);
        var run=new WatchRun(Guid.NewGuid(),Now,[Point(config)],[],[new(config.AssetId,candle),new(config.AssetId,candle),new(config.AssetId,candle with{OpenTime=Now})],"{}");
        db.SaveWatchRun(run);using var c=db.Open(true);Assert.Equal(1,c.QuerySingle<int>("SELECT COUNT(*) FROM WatchCandle1m"));
        Assert.Equal("Binance",c.QuerySingle<string>("SELECT Exchange FROM WatchCandle1m"));
    }
    [Fact] public async Task SingleWriterSeedEditPauseAuditAndValidationWorkWithoutRealNotifications()
    {
        var db=Db();var queue=new PersistenceQueue();var clock=new Clock();using var worker=new PersistenceWorker(db,queue,new ProviderHealthStore(clock),NullLogger<PersistenceWorker>.Instance);
        await worker.StartAsync(default);await queue.Ready.Task;
        try
        {
            var instruments=await queue.SyncAsync([new("ARIAUSDT","ARIA",Now.AddDays(-30))],default);var market=new MarketRuntimeStore();market.Register(instruments);
            market.Apply(new TickerEvent("ARIAUSDT",.088m,1_000_000,3,Now,Now));var settings=new WatchOptions();var repo=new WatchRepository(db);
            var service=new WatchService(market,repo,new WatchPersistence(queue),new(settings),settings,new(new()),new(new()),new EmptyRadar(),clock);
            await service.TickAsync(default);var first=Assert.Single(service.Overview());Assert.True(first.Config.IsSpecificationExample);Assert.False(first.Config.EnableAlerts);
            Assert.Equal(354.54545454545454545454545455m,first.Result!.ToCostPercent);
            var request=new WatchRequest(1,false,.40m,null,null,null,null,false);
            await Assert.ThrowsAsync<ArgumentException>(()=>service.SaveAsync(first.Config.AssetId,request with{CostPrice=-1},default));
            await service.SaveAsync(first.Config.AssetId,request,default);await service.TickAsync(default);
            Assert.Equal(DataStatus.Missing,service.Overview()[0].Result!.Status);settings.SevereDrop1h=-40;
            Assert.All(service.Audit(first.Config.AssetId,30),a=>Assert.Equal("Match",a.Status));
            using var c=db.Open();Assert.Equal(0,c.QuerySingle<int>("SELECT COUNT(*) FROM AlertEvent"));
            await Assert.ThrowsAsync<MappingConflictException>(()=>service.SaveAsync(first.Config.AssetId,request,default));
        }
        finally{await worker.StopAsync(default);}
    }
    private sealed class Clock:TimeProvider{public override DateTimeOffset GetUtcNow()=>Now;}
    private sealed class EmptyRadar:IRadarSnapshotSource{public RadarSnapshot? GetSnapshot()=>null;}
    public void Dispose(){SqliteConnection.ClearAllPools();if(Directory.Exists(_root))Directory.Delete(_root,true);}
}
