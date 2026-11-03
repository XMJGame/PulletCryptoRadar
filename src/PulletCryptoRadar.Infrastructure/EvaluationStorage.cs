using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Scanner;

namespace PulletCryptoRadar.Infrastructure;

public sealed partial class SqliteDatabase
{
    private static void InitializeEvaluation(SqliteConnection c)
    {
        c.Execute("""
            CREATE TABLE IF NOT EXISTS ReplayFrame (ScanId TEXT PRIMARY KEY REFERENCES RadarScan(Id), AsOf INTEGER NOT NULL, Payload BLOB NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_ReplayFrame_Time ON ReplayFrame(AsOf);
            CREATE TABLE IF NOT EXISTS SignalEvaluation (Id TEXT PRIMARY KEY, AsOf INTEGER NOT NULL, Version TEXT NOT NULL, Payload BLOB NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_SignalEvaluation_Time ON SignalEvaluation(AsOf);
            CREATE TABLE IF NOT EXISTS EvaluationEntry (NotificationId TEXT PRIMARY KEY REFERENCES AlertEvent(Id), Time INTEGER NOT NULL, Payload TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_EvaluationEntry_Time ON EvaluationEntry(Time);
            INSERT OR IGNORE INTO SchemaVersion VALUES(5,unixepoch());
            """);
    }
    internal static byte[] Compress<T>(T value)
    {
        using var output = new MemoryStream();
        using (var zip = new GZipStream(output, CompressionLevel.Fastest, true)) JsonSerializer.Serialize(zip, value);
        return output.ToArray();
    }
    internal static T Decompress<T>(byte[] value)
        =>StorageRecordCodec.Decode<T>(value);
    public void SaveEvaluation(EvaluationReport report)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        c.Execute("INSERT OR IGNORE INTO SignalEvaluation VALUES(@Id,@Time,@Version,@Payload)",
            new { Id = report.Id.ToString(), Time = report.AsOf.ToUnixTimeMilliseconds(), report.Version, Payload = Compress(report) }, tx);
        c.Execute("DELETE FROM SignalEvaluation WHERE Id NOT IN (SELECT Id FROM SignalEvaluation ORDER BY AsOf DESC LIMIT 168)", transaction: tx);
        tx.Commit();
    }
}

public sealed class EvaluationRepository(SqliteDatabase db) : IEvaluationRepository
{
    public EvaluationReport? ReadLatest()
    {
        using var c = db.Open(); var bytes = c.QueryFirstOrDefault<byte[]>("SELECT Payload FROM SignalEvaluation ORDER BY AsOf DESC,rowid DESC LIMIT 1");
        return bytes is null ? null : SqliteDatabase.Decompress<EvaluationReport>(bytes);
    }
    public ReplayAvailability GetAvailability()
    {
        using var c = db.Open(); var row = c.QuerySingle("SELECT MIN(AsOf) AS First,MAX(AsOf) AS Last,COUNT(*) AS Total FROM ReplayFrame");
        // The index stores milliseconds while the captured payload retains .NET ticks.
        // Expose the exact endpoints so an inclusive availability range also includes its final frame.
        DateTimeOffset? Endpoint(bool last) => row.First is null ? null : SqliteDatabase.Decompress<ReplayFrame>(
            c.QueryFirst<byte[]>($"SELECT Payload FROM ReplayFrame ORDER BY AsOf {(last ? "DESC" : "ASC")},rowid {(last ? "DESC" : "ASC")} LIMIT 1")).AsOf;
        return new(Endpoint(false), Endpoint(true), (int)(long)row.Total,
            c.QuerySingle<int>("SELECT count(*) FROM RadarEvent WHERE Kind='Created'"));
    }
    public IEnumerable<ReplayFrame> ReadFrames(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        using var c = db.Open();
        var args = new { from = from.ToUnixTimeMilliseconds(), to = to.ToUnixTimeMilliseconds() };
        if (c.QuerySingle<int>("SELECT count(*) FROM ReplayFrame WHERE AsOf>=@from AND AsOf<=@to", args) > 360)
            throw new ArgumentException("所选区间超过 360 个扫描快照，请缩短区间");
        foreach (var bytes in c.Query<byte[]>("SELECT Payload FROM ReplayFrame WHERE AsOf>=@from AND AsOf<=@to ORDER BY AsOf,ScanId", args, buffered: false))
        { ct.ThrowIfCancellationRequested(); yield return SqliteDatabase.Decompress<ReplayFrame>(bytes); }
    }
    public IReadOnlyList<ReplayAuditInput> ReadAuditInputs(int limit)
    {
        using var c = db.Open();
        return c.Query("SELECT e.Payload,s.ParametersJson FROM RadarEvent e JOIN RadarScan s ON e.ScanId=s.Id ORDER BY e.Time DESC,e.rowid DESC LIMIT @limit",
            new { limit = Math.Clamp(limit, 1, 200) }).Select(x => new ReplayAuditInput(JsonSerializer.Deserialize<RadarEvent>((string)x.Payload)!, (string)x.ParametersJson)).ToArray();
    }
    public EvaluationData ReadData(DateTimeOffset from, DateTimeOffset asOf, int maximumSignals, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var c = db.Open();
        var args = new { from = from.ToUnixTimeMilliseconds(), to = asOf.ToUnixTimeMilliseconds(), limit = maximumSignals + 1 };
        var events = c.Query<string>("SELECT Payload FROM RadarEvent WHERE Kind='Created' AND Time>=@from AND Time<=@to ORDER BY Time,Id LIMIT @limit", args)
            .Select(s => JsonSerializer.Deserialize<RadarEvent>(s)!).Where(e => e.Input?.Price > 0).ToArray();
        var signals = events.Take(maximumSignals).Select(e => new EvaluationSignal($"Episode:{e.EpisodeId}", e.AssetId, e.Symbol, e.Direction,
            e.Time, e.Input!.Price!.Value, e.Assessment?.Score, e.StrategyVersion, e.EpisodeId, SignalEvaluationEngine.ReferencePrice(e.Input))).ToList();
        var notices = c.Query<string>("SELECT Payload FROM AlertEvent WHERE Kind=0 AND CreatedAt>=@from AND CreatedAt<=@to ORDER BY CreatedAt,Id LIMIT @limit", args)
            .Select(s => JsonSerializer.Deserialize<NotificationRecord>(s)!).ToArray();
        // Notification entry needs the actual scan input, never the current runtime price.
        foreach (var n in notices.Take(maximumSignals))
        {
            ct.ThrowIfCancellationRequested();
            var entry = c.QuerySingleOrDefault<string>("SELECT Payload FROM EvaluationEntry WHERE NotificationId=@id", new { id = n.Id.ToString() });
            if (entry is not null) signals.Add(JsonSerializer.Deserialize<EvaluationSignal>(entry)!);
            else signals.Add(new($"Notification:{n.Id}", n.AssetId ?? 0, n.Symbol, n.Direction ?? MoveDirection.Up, n.CreatedAt, 0,
                n.Score, n.StrategyVersion ?? "Unknown", n.EpisodeId ?? 0, null, true));
        }
        var instruments = SqliteDatabase.ReadInstruments(c);
        var times = c.Query<long>("SELECT AsOf FROM ReplayFrame WHERE AsOf>=@from AND AsOf<=@to ORDER BY AsOf", args)
            .Select(DateTimeOffset.FromUnixTimeMilliseconds).ToArray();
        using var market = db.Open(true);
        var candles = new Dictionary<int, List<Candle>>();
        foreach (var row in market.Query("SELECT * FROM MarketSnapshot5m WHERE Timestamp>=@from AND Timestamp<@to ORDER BY AssetId,Timestamp", args, buffered: false))
        {
            ct.ThrowIfCancellationRequested();
            var id = (int)row.AssetId; if (!candles.TryGetValue(id, out var bars)) candles[id] = bars = [];
            decimal D(object value) => decimal.Parse((string)value, CultureInfo.InvariantCulture);
            bars.Add(new((string)row.Symbol, 5, DateTimeOffset.FromUnixTimeMilliseconds((long)row.Timestamp), D(row.Open), D(row.High), D(row.Low), D(row.Close),
                D(row.QuoteVolume), DateTimeOffset.FromUnixTimeMilliseconds((long)row.SourceTime), DateTimeOffset.FromUnixTimeMilliseconds((long)row.ReceivedTime)));
        }
        return new(signals, candles.ToDictionary(x => x.Key, x => (IReadOnlyList<Candle>)x.Value), instruments, times,
            events.Length > maximumSignals || notices.Length > maximumSignals);
    }
}

public sealed class EvaluationPersistence(PersistenceQueue queue) : IEvaluationPersistence
{
    public async Task SaveAsync(EvaluationReport report, CancellationToken ct)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await queue.Channel.Writer.WriteAsync(new SaveEvaluationCommand(report, completion), ct);
        await completion.Task.WaitAsync(ct);
    }
}
public sealed class EvaluationWorker(EvaluationService service, PersistenceQueue queue, IProviderHealthStore health,
    TimeProvider time, ILogger<EvaluationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await queue.Ready.Task.WaitAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), time);
        do
        {
            try { await service.RefreshAsync(stoppingToken); health.Success("Signal Evaluation", freshnessSeconds: 3900); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { health.Failure("Signal Evaluation", ex.Message); logger.LogError(ex, "Signal evaluation failed"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
