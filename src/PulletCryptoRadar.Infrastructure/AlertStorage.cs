using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Infrastructure;

public sealed partial class SqliteDatabase
{
    private static void InitializeAlerts(SqliteConnection c)
    {
        using var tx = c.BeginTransaction();
        c.Execute("""
            CREATE TABLE IF NOT EXISTS AlertCheckpoint (Id INTEGER PRIMARY KEY CHECK(Id=1), ScanId TEXT NOT NULL, StateJson TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS AlertRun (ScanId TEXT PRIMARY KEY, Time INTEGER NOT NULL, ParametersJson TEXT NOT NULL, DecisionsJson TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS AlertEvent (Id TEXT PRIMARY KEY, DedupKey TEXT UNIQUE NOT NULL, Kind INTEGER NOT NULL,
                EpisodeId INTEGER, AssetId INTEGER, CreatedAt INTEGER NOT NULL, Status INTEGER NOT NULL, UpdatedAt INTEGER NOT NULL, Payload TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_AlertEvent_Time ON AlertEvent(CreatedAt DESC);
            CREATE INDEX IF NOT EXISTS IX_AlertEvent_Status ON AlertEvent(Status,CreatedAt);
            INSERT OR IGNORE INTO SchemaVersion VALUES(3, unixepoch());
            """, transaction: tx);
        tx.Commit();
    }
    public void SaveAlertRun(AlertRun run)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        if (c.QuerySingle<int>("SELECT count(*) FROM AlertRun WHERE ScanId=@id", new { id = run.ScanId.ToString() }, tx) > 0) { tx.Commit(); return; }
        var audit = run.Plan.Decisions.Where(d => d.Score >= 70 || d.EpisodeId is not null).ToArray();
        c.Execute("INSERT INTO AlertRun VALUES(@ScanId,@Time,@Parameters,@Decisions)", new { ScanId = run.ScanId.ToString(),
            Time = run.AsOf.ToUnixTimeMilliseconds(), Parameters = run.ParametersJson, Decisions = JsonSerializer.Serialize(audit) }, tx);
        c.Execute("INSERT OR REPLACE INTO AlertCheckpoint VALUES(1,@ScanId,@State)", new { ScanId = run.ScanId.ToString(), State = JsonSerializer.Serialize(run.Plan.State) }, tx);
        foreach (var n in run.Plan.Notifications)
        {
            c.Execute("INSERT INTO AlertEvent VALUES(@Id,@DedupKey,@Kind,@EpisodeId,@AssetId,@CreatedAt,@Status,@UpdatedAt,@Payload)",
                new { Id = n.Id.ToString(), n.DedupKey, Kind = (int)n.Kind, n.EpisodeId, n.AssetId, CreatedAt = n.CreatedAt.ToUnixTimeMilliseconds(),
                    Status = (int)n.Status, UpdatedAt = run.AsOf.ToUnixTimeMilliseconds(), Payload = JsonSerializer.Serialize(n) }, tx);
            if (n.Kind == NotificationKind.Normal)
            {
                var bytes = c.QuerySingleOrDefault<byte[]>("SELECT Payload FROM ReplayFrame WHERE ScanId=@id", new { id = run.ScanId.ToString() }, tx);
                var frame = bytes is null ? null : Decompress<ReplayFrame>(bytes);
                var m = frame?.Inputs.FirstOrDefault(m => m.Instrument.AssetId == n.AssetId);
                var usable = m?.Price > 0 && frame?.AsOf <= n.CreatedAt && n.CreatedAt - frame.AsOf <= TimeSpan.FromSeconds(90);
                var entry = new EvaluationSignal($"Notification:{n.Id}", n.AssetId ?? 0, n.Symbol, n.Direction ?? MoveDirection.Up,
                    n.CreatedAt, usable ? m!.Price!.Value : 0, n.Score, n.StrategyVersion ?? "Unknown", n.EpisodeId ?? 0,
                    usable ? PulletCryptoRadar.Application.SignalEvaluationEngine.ReferencePrice(m!) : null, true);
                c.Execute("INSERT OR IGNORE INTO EvaluationEntry VALUES(@Id,@Time,@Payload)", new { Id = n.Id.ToString(),
                    Time = n.CreatedAt.ToUnixTimeMilliseconds(), Payload = JsonSerializer.Serialize(entry) }, tx);
            }
        }
        c.Execute("DELETE FROM AlertRun WHERE Time < @cutoff", new { cutoff = run.AsOf.AddDays(-7).ToUnixTimeMilliseconds() }, tx);
        tx.Commit();
    }
    public bool TransitionNotification(Guid id, NotificationStatus expected, NotificationStatus next, string? error, DateTimeOffset now)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        var payload = c.QuerySingleOrDefault<string>("SELECT Payload FROM AlertEvent WHERE Id=@id AND Status=@status", new { id = id.ToString(), status = (int)expected }, tx);
        if (payload is null) { tx.Commit(); return false; }
        var n = JsonSerializer.Deserialize<NotificationRecord>(payload)! with { Status = next, Error = error, UpdatedAt = now };
        c.Execute("UPDATE AlertEvent SET Status=@Status,UpdatedAt=@Time,Payload=@Payload WHERE Id=@Id", new { Status = (int)next,
            Time = now.ToUnixTimeMilliseconds(), Payload = JsonSerializer.Serialize(n), Id = id.ToString() }, tx);
        tx.Commit(); return true;
    }
}

public sealed class AlertRepository(SqliteDatabase db) : IAlertRepository
{
    public Task<AlertRestoreState> RestoreAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); using var c = db.Open();
        var checkpoint = c.QuerySingleOrDefault("SELECT ScanId,StateJson FROM AlertCheckpoint WHERE Id=1");
        var records = c.Query<string>("SELECT Payload FROM AlertEvent ORDER BY CreatedAt DESC").Select(p => JsonSerializer.Deserialize<NotificationRecord>(p)!).ToArray();
        return Task.FromResult(new AlertRestoreState(checkpoint is null ? new([]) : JsonSerializer.Deserialize<AlertPolicyState>((string)checkpoint.StateJson)!,
            records, checkpoint is null ? null : Guid.Parse((string)checkpoint.ScanId)));
    }
    public Task<IReadOnlyList<NotificationRecord>> ReadNotificationsAsync(int limit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); using var c = db.Open();
        return Task.FromResult<IReadOnlyList<NotificationRecord>>(c.Query<string>("SELECT Payload FROM AlertEvent ORDER BY CreatedAt DESC,rowid DESC LIMIT @limit",
            new { limit = Math.Clamp(limit, 1, 200) }).Select(p => JsonSerializer.Deserialize<NotificationRecord>(p)!).ToArray());
    }
}

public sealed class AlertPersistence(PersistenceQueue queue) : IAlertPersistence
{
    public async Task SaveRunAsync(AlertRun run, CancellationToken ct)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await queue.Channel.Writer.WriteAsync(new SaveAlertRunCommand(run, completion), ct);
        await completion.Task.WaitAsync(ct);
    }
    public async Task<bool> TransitionAsync(Guid id, NotificationStatus expected, NotificationStatus next, string? error, DateTimeOffset now, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await queue.Channel.Writer.WriteAsync(new TransitionAlertCommand(id, expected, next, error, now, completion), ct);
        return await completion.Task.WaitAsync(ct);
    }
}
