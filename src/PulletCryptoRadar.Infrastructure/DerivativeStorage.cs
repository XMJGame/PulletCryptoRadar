using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Infrastructure;

public sealed partial class SqliteDatabase
{
    private static void InitializeDerivatives(SqliteConnection market)
        => market.Execute("""
            CREATE TABLE IF NOT EXISTS DerivativeSnapshot (Symbol TEXT NOT NULL, Kind INTEGER NOT NULL, SourceTime INTEGER NOT NULL,
                ReceivedTime INTEGER NOT NULL, Payload TEXT NOT NULL, PRIMARY KEY(Symbol,Kind,SourceTime));
            CREATE INDEX IF NOT EXISTS IX_Derivative_Time ON DerivativeSnapshot(SourceTime);
            CREATE TABLE IF NOT EXISTS LiquidationObservation (Key TEXT PRIMARY KEY, Symbol TEXT NOT NULL, SourceTime INTEGER NOT NULL,
                ReceivedTime INTEGER NOT NULL, Payload TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_Liquidation_Time ON LiquidationObservation(SourceTime);
            """);
    public void SaveDerivatives(DerivativeWriteBatch batch)
    {
        using var c = Open(true); using var tx = c.BeginTransaction();
        foreach (var s in batch.Samples)
            c.Execute("""
                INSERT INTO DerivativeSnapshot VALUES(@Symbol,@Kind,@SourceTime,@ReceivedTime,@Payload)
                ON CONFLICT(Symbol,Kind,SourceTime) DO UPDATE SET ReceivedTime=excluded.ReceivedTime,Payload=excluded.Payload
                WHERE excluded.ReceivedTime>=DerivativeSnapshot.ReceivedTime
                """, new { s.Symbol, Kind = (int)s.Kind, SourceTime = s.SourceTime.ToUnixTimeMilliseconds(),
                ReceivedTime = s.ReceivedTime.ToUnixTimeMilliseconds(), Payload = JsonSerializer.Serialize(s) }, tx);
        foreach (var s in batch.Liquidations)
            c.Execute("""
                INSERT INTO LiquidationObservation VALUES(@Key,@Symbol,@SourceTime,@ReceivedTime,@Payload)
                ON CONFLICT(Key) DO UPDATE SET ReceivedTime=excluded.ReceivedTime,Payload=excluded.Payload
                WHERE excluded.ReceivedTime>=LiquidationObservation.ReceivedTime
                """, new { s.Key, s.Symbol, SourceTime = s.SourceTime.ToUnixTimeMilliseconds(),
                ReceivedTime = s.ReceivedTime.ToUnixTimeMilliseconds(), Payload = JsonSerializer.Serialize(s) }, tx);
        tx.Commit();
    }
}

public sealed class DerivativeRepository(SqliteDatabase db)
{
    public IReadOnlyList<DerivativeSample> Read(string symbol)
    {
        using var c = db.Open(true);
        return c.Query<string>("""
            SELECT Payload FROM (SELECT Payload,ROW_NUMBER() OVER(PARTITION BY Kind ORDER BY SourceTime DESC) AS RowNum
                FROM DerivativeSnapshot WHERE Symbol=@symbol) WHERE RowNum<=2016
            """, new { symbol }).Select(x => JsonSerializer.Deserialize<DerivativeSample>(x)!).OrderBy(x => x.SourceTime).ToArray();
    }
}

public sealed class DerivativePersistenceWorker(DerivativeRuntimeStore runtime, PersistenceQueue queue, IProviderHealthStore health,
    TimeProvider time, ILogger<DerivativePersistenceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await queue.Ready.Task.WaitAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var batch = runtime.Pending(); if (batch.Samples.Count + batch.Liquidations.Count == 0) continue;
            try
            {
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await queue.Channel.Writer.WriteAsync(new SaveDerivativesCommand(batch, completion), stoppingToken);
                await completion.Task.WaitAsync(stoppingToken); runtime.Acknowledge(batch);
                health.Success("Derivative Persistence", freshnessSeconds: 900);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { health.Failure("Derivative Persistence", ex.Message); logger.LogError(ex, "Derivative batch retained for retry"); }
        }
    }
}
