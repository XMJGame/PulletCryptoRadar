using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Infrastructure;

public sealed partial class SqliteDatabase
{
    private static void InitializeWatch(SqliteConnection business,SqliteConnection market)
    {
        business.Execute("""
            CREATE TABLE IF NOT EXISTS WatchAsset(AssetId INTEGER PRIMARY KEY REFERENCES Asset(Id),Revision INTEGER NOT NULL,Payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS WatchAssetAudit(AssetId INTEGER NOT NULL,Revision INTEGER NOT NULL,Payload TEXT NOT NULL,PRIMARY KEY(AssetId,Revision));
            CREATE TABLE IF NOT EXISTS WatchCheckpoint(AssetId INTEGER PRIMARY KEY,Payload BLOB NOT NULL);
            CREATE TABLE IF NOT EXISTS WatchHistory(AssetId INTEGER NOT NULL,AsOf INTEGER NOT NULL,Payload BLOB NOT NULL,PRIMARY KEY(AssetId,AsOf));
            INSERT OR IGNORE INTO SchemaVersion VALUES(8,unixepoch());
            """);
        market.Execute("CREATE TABLE IF NOT EXISTS WatchCandle1m(AssetId INTEGER NOT NULL,Exchange TEXT NOT NULL,Symbol TEXT NOT NULL,OpenTime INTEGER NOT NULL,Payload TEXT NOT NULL,PRIMARY KEY(AssetId,Exchange,OpenTime));");
    }
    public void SaveWatchConfig(WatchAsset config,long expected)
    {
        using var c=Open();using var tx=c.BeginTransaction();
        var revision=c.QuerySingleOrDefault<long?>("SELECT Revision FROM WatchAsset WHERE AssetId=@AssetId",config,tx)??0;
        if(revision!=expected||config.Revision!=revision+1)throw new MappingConflictException("自选配置版本冲突，未覆盖新配置");
        var data=new{config.AssetId,config.Revision,Payload=JsonSerializer.Serialize(config)};
        c.Execute("INSERT INTO WatchAsset VALUES(@AssetId,@Revision,@Payload) ON CONFLICT(AssetId) DO UPDATE SET Revision=excluded.Revision,Payload=excluded.Payload",data,tx);
        c.Execute("INSERT INTO WatchAssetAudit VALUES(@AssetId,@Revision,@Payload)",data,tx);tx.Commit();
    }
    public void SaveWatchRun(WatchRun run)
    {
        // Candle writes are idempotent; the business checkpoint and notification reservation are atomic.
        using(var m=Open(true))using(var mt=m.BeginTransaction())
        {
            foreach(var row in run.Candles.Where(c=>c.Candle.IntervalMinutes==1&&c.Candle.IsValid&&c.Candle.EndTime<=run.AsOf&&c.Candle.SourceTime<=run.AsOf&&c.Candle.ReceivedTime<=run.AsOf))
                m.Execute("INSERT OR IGNORE INTO WatchCandle1m VALUES(@AssetId,'Binance',@Symbol,@Time,@Payload)",new{row.AssetId,row.Candle.Symbol,Time=row.Candle.OpenTime.ToUnixTimeMilliseconds(),Payload=JsonSerializer.Serialize(row.Candle)},mt);
            m.Execute("DELETE FROM WatchCandle1m WHERE OpenTime<@Time",new{Time=run.AsOf.AddDays(-14).ToUnixTimeMilliseconds()},mt);mt.Commit();
        }
        using var c=Open();using var tx=c.BeginTransaction();
        foreach(var point in run.Checkpoints)
        {
            var id=point.Result.AssetId;
            var revision=c.QuerySingleOrDefault<long?>("SELECT Revision FROM WatchAsset WHERE AssetId=@id",new{id},tx);
            if(revision!=point.Input.Config.Revision)throw new MappingConflictException("评分期间自选配置已变化，整批提醒取消，请重试");
            var bytes=c.QuerySingleOrDefault<byte[]>("SELECT Payload FROM WatchCheckpoint WHERE AssetId=@id",new{id},tx);
            var old=bytes is null?null:Decompress<WatchCheckpoint>(bytes);
            if(old?.Result.AsOf>point.Result.AsOf)throw new InvalidDataException("拒绝倒序自选快照");
            var payload=Compress(point);var data=new{AssetId=id,Time=point.Result.AsOf.ToUnixTimeMilliseconds(),Payload=payload};
            c.Execute("INSERT OR REPLACE INTO WatchCheckpoint VALUES(@AssetId,@Payload)",data,tx);
            if(old is null||old.Result.AsOf.ToUnixTimeSeconds()/60!=point.Result.AsOf.ToUnixTimeSeconds()/60||old.Result.State!=point.Result.State
                ||old.Result.Exchange!=point.Result.Exchange||old.Input.Config.Revision!=point.Input.Config.Revision)
                c.Execute("INSERT OR IGNORE INTO WatchHistory VALUES(@AssetId,@Time,@Payload)",data,tx);
        }
        foreach(var n in run.Notifications)
            c.Execute("INSERT OR IGNORE INTO AlertEvent VALUES(@Id,@DedupKey,@Kind,@EpisodeId,@AssetId,@CreatedAt,@Status,@UpdatedAt,@Payload)",
                new{Id=n.Id.ToString(),n.DedupKey,Kind=(int)n.Kind,n.EpisodeId,n.AssetId,CreatedAt=n.CreatedAt.ToUnixTimeMilliseconds(),Status=(int)n.Status,UpdatedAt=run.AsOf.ToUnixTimeMilliseconds(),Payload=JsonSerializer.Serialize(n)},tx);
        c.Execute("DELETE FROM WatchHistory WHERE AsOf<@Time",new{Time=run.AsOf.AddDays(-7).ToUnixTimeMilliseconds()},tx);tx.Commit();
    }
}
public sealed class WatchRepository(SqliteDatabase db):IWatchRepository
{
    public IReadOnlyList<WatchAsset> Configs(){using var c=db.Open();return c.Query<string>("SELECT Payload FROM WatchAsset ORDER BY AssetId").Select(p=>JsonSerializer.Deserialize<WatchAsset>(p)!).ToArray();}
    public IReadOnlyList<WatchCheckpoint> Checkpoints(){using var c=db.Open();return c.Query<byte[]>("SELECT Payload FROM WatchCheckpoint").Select(SqliteDatabase.Decompress<WatchCheckpoint>).ToArray();}
    public IReadOnlyList<WatchCheckpoint> History(int assetId,int limit){using var c=db.Open();return c.Query<byte[]>("SELECT Payload FROM WatchHistory WHERE AssetId=@assetId ORDER BY AsOf DESC LIMIT @limit",new{assetId,limit=Math.Clamp(limit,1,100)}).Select(SqliteDatabase.Decompress<WatchCheckpoint>).ToArray();}
}
public sealed class WatchPersistence(PersistenceQueue queue):IWatchPersistence
{
    public async Task SaveConfigAsync(WatchAsset config,long expectedRevision,CancellationToken ct){var ack=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);await queue.Channel.Writer.WriteAsync(new SaveWatchConfigCommand(config,expectedRevision,ack),ct);await ack.Task.WaitAsync(ct);}
    public async Task SaveRunAsync(WatchRun run,CancellationToken ct){var ack=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);await queue.Channel.Writer.WriteAsync(new SaveWatchRunCommand(run,ack),ct);await ack.Task.WaitAsync(ct);}
}
