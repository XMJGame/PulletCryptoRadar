using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;

namespace PulletCryptoRadar.IntegrationTests;

public sealed class StorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PulletCryptoRadar-tests", Guid.NewGuid().ToString("N"));
    private SqliteDatabase CreateDb() => new(Options.Create(new StorageOptions { Directory = _root, BackupDirectory = Path.Combine(_root, "Backup") }));
    [Fact]
    public async Task WalSchemaIdentityAndExactDecimalsSurviveRestart()
    {
        var db = CreateDb(); db.Initialize(); var now = DateTimeOffset.UtcNow;
        var instruments = db.Synchronize([new("ARIAUSDT", "ARIA", now.AddDays(-20))]); var id = Assert.Single(instruments).AssetId;
        Assert.Equal(id, Assert.Single(db.Synchronize([new("ARIAUSDT", "ARIA", now.AddDays(-20))])).AssetId);
        var price = 0.1234567890123456789012345678m;
        var candle = new Candle("ARIAUSDT", 5, now.AddMinutes(-5), price, price, price, price, 123456.123456789m, now, now);
        db.Save([(id, candle, null, null)], []);
        using (var connection = db.Open(true))
        {
            Assert.Equal("wal", connection.QuerySingle<string>("PRAGMA journal_mode"));
            Assert.Equal(1, connection.QuerySingle<int>("PRAGMA foreign_keys"));
            Assert.Null(connection.QuerySingle<string?>("SELECT FundingRate FROM MarketSnapshot5m"));
        }
        db.Save([(id, candle, 10000m, 0.0001m)], []);
        db.Save([(id, candle, null, null)], []);
        using (var connection = db.Open(true))
            Assert.Equal("0.0001", connection.QuerySingle<string>("SELECT FundingRate FROM MarketSnapshot5m"));
        db.Initialize();
        var read = await new AssetRepository(db).ReadCandlesAsync(id, 5, 100, CancellationToken.None);
        Assert.Equal(price, Assert.Single(read).Close); Assert.Equal(candle.QuoteVolume, read[0].QuoteVolume);
        db.Synchronize([]);
        Assert.False(Assert.Single(await new AssetRepository(db).ReadInstrumentsAsync(CancellationToken.None)).Active);
    }
    [Fact]
    public void BackupIncludesCommittedWalAndCanBeOpened()
    {
        var db = CreateDb(); db.Initialize(); var now = DateTimeOffset.UtcNow;
        db.Synchronize([new("BTCUSDT", "BTC", now)]); db.Maintain(now);
        using var backup = new SqliteConnection($"Data Source={Path.Combine(_root, "Backup", $"PulletCryptoRadar-{now:yyyyMMdd}.db")}");
        backup.Open(); Assert.Equal(1, backup.QuerySingle<int>("SELECT count(*) FROM Asset"));
        Assert.Equal("ok", backup.QuerySingle<string>("PRAGMA integrity_check"));
    }
    [Fact]
    public async Task SingleWriterInitializesBeforeAcceptingAndDrainsCommands()
    {
        var db = CreateDb(); var q = new PersistenceQueue(); var health = new ProviderHealthStore(TimeProvider.System);
        using var worker = new PersistenceWorker(db, q, health, NullLogger<PersistenceWorker>.Instance);
        await worker.StartAsync(CancellationToken.None); await q.Ready.Task;
        var symbols = await q.SyncAsync([new("BTCUSDT", "BTC", DateTimeOffset.UtcNow)], CancellationToken.None);
        Assert.Single(symbols); await worker.StopAsync(CancellationToken.None);
        Assert.Equal("Healthy", Assert.Single(health.GetAll()).Status(DateTimeOffset.UtcNow));
    }
    [Fact]
    public void RuntimeIgnoresOutOfOrderTicksAndRetainsMissingFunding()
    {
        var store = new MarketRuntimeStore(); var now = DateTimeOffset.UtcNow;
        store.Register([new(1, "BTCUSDT", "BTC", now, true)]);
        store.Apply(new TickerEvent("BTCUSDT", 100, 10000, 2, now, now));
        store.Apply(new TickerEvent("BTCUSDT", 80, 10000, 2, now.AddMinutes(-1), now));
        Assert.Equal(100, store.Get(1)!.Price); Assert.Null(store.Get(1)!.FundingRate);
        store.Register([]); Assert.Empty(store.GetAll());
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
