using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Infrastructure;

public sealed partial class SqliteDatabase
{
    private static void InitializeRadar(SqliteConnection connection)
    {
        using var tx = connection.BeginTransaction();
        connection.Execute("""
            CREATE TABLE IF NOT EXISTS RadarScan (Id TEXT PRIMARY KEY, AsOf INTEGER NOT NULL,
                MarketJson TEXT NOT NULL, ParametersJson TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS MoveEpisode (Id INTEGER PRIMARY KEY, AssetId INTEGER NOT NULL REFERENCES Asset(Id),
                State INTEGER NOT NULL, UpdatedAt INTEGER NOT NULL, Payload TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_MoveEpisode_AssetId ON MoveEpisode(AssetId,Id DESC);
            CREATE TABLE IF NOT EXISTS RadarEvent (Id TEXT PRIMARY KEY, ScanId TEXT NOT NULL REFERENCES RadarScan(Id),
                EpisodeId INTEGER NOT NULL REFERENCES MoveEpisode(Id), AssetId INTEGER NOT NULL REFERENCES Asset(Id),
                Time INTEGER NOT NULL, StrategyVersion TEXT NOT NULL, Kind TEXT NOT NULL, Payload TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_RadarEvent_AssetTime ON RadarEvent(AssetId,Time DESC);
            CREATE INDEX IF NOT EXISTS IX_RadarEvent_Time ON RadarEvent(Time DESC);
            INSERT OR IGNORE INTO SchemaVersion VALUES(2, unixepoch());
            """, transaction: tx);
        tx.Commit();
    }

    public void SaveRadar(RadarPersistenceBatch batch)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        var s = batch.Snapshot;
        c.Execute("INSERT OR IGNORE INTO RadarScan VALUES(@Id,@AsOf,@Market,@Parameters)",
            new { Id = s.ScanId.ToString(), AsOf = s.AsOf.ToUnixTimeMilliseconds(), Market = JsonSerializer.Serialize(s.Market), Parameters = s.ParametersJson }, tx);
        foreach (var e in batch.ChangedEpisodes)
            c.Execute("""
                INSERT INTO MoveEpisode VALUES(@Id,@AssetId,@State,@UpdatedAt,@Payload)
                ON CONFLICT(Id) DO UPDATE SET State=excluded.State,UpdatedAt=excluded.UpdatedAt,Payload=excluded.Payload
                WHERE excluded.UpdatedAt >= MoveEpisode.UpdatedAt
                """, new { e.Id, e.AssetId, State = (int)e.State, UpdatedAt = s.AsOf.ToUnixTimeMilliseconds(), Payload = JsonSerializer.Serialize(e) }, tx);
        foreach (var e in batch.NewEvents)
            c.Execute("INSERT OR IGNORE INTO RadarEvent VALUES(@Id,@ScanId,@EpisodeId,@AssetId,@Time,@StrategyVersion,@Kind,@Payload)",
                new { Id = e.Id.ToString(), ScanId = e.ScanId.ToString(), e.EpisodeId, e.AssetId, Time = e.Time.ToUnixTimeMilliseconds(),
                    e.StrategyVersion, e.Kind, Payload = JsonSerializer.Serialize(e) }, tx);
        if (batch.ReplayFrame is { } frame)
        {
            c.Execute("INSERT OR IGNORE INTO ReplayFrame VALUES(@Id,@Time,@Payload)",
                new { Id = frame.ScanId.ToString(), Time = frame.AsOf.ToUnixTimeMilliseconds(), Payload = Compress(frame) }, tx);
            c.Execute("DELETE FROM ReplayFrame WHERE AsOf<@cutoff", new { cutoff = frame.AsOf.AddDays(-Math.Clamp(batch.ReplayRetentionDays, 2, 30)).ToUnixTimeMilliseconds() }, tx);
        }
        tx.Commit();
    }
}

public sealed class RadarRepository(SqliteDatabase database) : IRadarRepository
{
    public Task<RadarRestoreState> RestoreAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); using var c = database.Open();
        var latest = c.Query<string>("SELECT Payload FROM MoveEpisode WHERE Id IN (SELECT MAX(Id) FROM MoveEpisode GROUP BY AssetId)")
            .Select(x => JsonSerializer.Deserialize<MoveEpisode>(x)!).ToArray();
        return Task.FromResult(new RadarRestoreState(latest.Where(e => e.State != EpisodeState.Closed).ToArray(),
            c.QuerySingle<long>("SELECT COALESCE(MAX(Id),0) FROM MoveEpisode"), latest));
    }
    public Task<IReadOnlyList<RadarEvent>> ReadEventsAsync(int? assetId, int limit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); using var c = database.Open();
        var result = c.Query<string>("SELECT Payload FROM RadarEvent WHERE (@assetId IS NULL OR AssetId=@assetId) ORDER BY Time DESC,rowid DESC LIMIT @limit",
            new { assetId, limit = Math.Clamp(limit, 1, 200) }).Select(x => JsonSerializer.Deserialize<RadarEvent>(x)!).ToArray();
        return Task.FromResult<IReadOnlyList<RadarEvent>>(result);
    }
    public Task<IReadOnlyList<MoveEpisode>> ReadEpisodesAsync(int? assetId, int limit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); using var c = database.Open();
        var result = c.Query<string>("SELECT Payload FROM MoveEpisode WHERE (@assetId IS NULL OR AssetId=@assetId) ORDER BY Id DESC LIMIT @limit",
            new { assetId, limit = Math.Clamp(limit, 1, 200) }).Select(x => JsonSerializer.Deserialize<MoveEpisode>(x)!).ToArray();
        return Task.FromResult<IReadOnlyList<MoveEpisode>>(result);
    }
}

public sealed class RadarPersistence(PersistenceQueue queue) : IRadarPersistence
{
    public async Task SaveAsync(RadarPersistenceBatch batch, CancellationToken ct)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await queue.Channel.Writer.WriteAsync(new SaveRadarCommand(batch, completion), ct);
        await completion.Task.WaitAsync(ct);
    }
}
