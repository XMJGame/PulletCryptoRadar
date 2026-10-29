using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;
using PulletCryptoRadar.Scanner;

namespace PulletCryptoRadar.IntegrationTests;

public sealed class RadarTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PulletCryptoRadar-tests", Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset _now = new(2026, 10, 29, 4, 0, 0, TimeSpan.Zero);
    private SqliteDatabase Database() => new(Options.Create(new StorageOptions { Directory = _root, BackupDirectory = Path.Combine(_root, "Backup") }));
    private MarketRuntimeStore Runtime(int id)
    {
        var runtime = new MarketRuntimeStore(); runtime.Register([new(id, "TESTUSDT", "TEST", _now.AddDays(-30), true)]);
        runtime.Apply(new TickerEvent("TESTUSDT", 0.123456789012345678901m, 10000000, 5, _now, _now));
        runtime.SetFeatures(id, Features()); return runtime;
    }
    private AssetFeatures Features()
    {
        FeatureValue<decimal> D(decimal v) => new(v, DataStatus.Valid, _now, "Fixture");
        FeatureValue<double> F(double v) => new(v, DataStatus.Valid, _now, "Fixture");
        return new() { Returns = new() { [1] = D(1), [5] = D(3), [15] = D(5), [60] = D(7), [240] = D(8) },
            PriceRobustZ5m = F(3), VolumeRobustZ = F(3), DistanceToHigh = new() { [60] = D(0.2m) }, DistanceToLow = new() { [60] = D(10) } };
    }
    private VolatilityRadarService Service(IMarketRuntimeStore runtime, IRadarRepository repository, IRadarPersistence persistence, TimeProvider time)
    {
        var o = new VolatilityOptions();
        return new(runtime, repository, persistence, new(o), new(o), new(o), new(o), o, time);
    }
    [Fact]
    public async Task ScanPersistsEpisodeAndReplayEvidenceAtomicallyAndRestoresSameIdentity()
    {
        var db = Database(); var queue = new PersistenceQueue();
        using var worker = new PersistenceWorker(db, queue, new ProviderHealthStore(TimeProvider.System), NullLogger<PersistenceWorker>.Instance);
        await worker.StartAsync(CancellationToken.None); await queue.Ready.Task;
        var id = (await queue.SyncAsync([new("TESTUSDT", "TEST", _now.AddDays(-30))], CancellationToken.None))[0].AssetId;
        var repository = new RadarRepository(db); var runtime = Runtime(id); var persistence = new RadarPersistence(queue);
        var service = Service(runtime, repository, persistence, new FixedClock(_now));
        var snapshot = await service.ScanAsync(CancellationToken.None);
        var episode = Assert.Single(snapshot.ActiveEpisodes);
        var events = await repository.ReadEventsAsync(id, 20, CancellationToken.None);
        var e = Assert.Single(events); Assert.Equal(episode.Id, e.EpisodeId); Assert.NotNull(e.Input);
        Assert.Equal(0.123456789012345678901m, e.Input.Price);
        Assert.Equal(UpMoveDetector.Version, e.StrategyVersion);
        Assert.Equal(new UpMoveDetector(new()).Detect(e.Input, e.Time).Score, e.Assessment!.Score);
        Assert.Equal(3, e.Assessment.Components.Count);
        var restored = Service(runtime, repository, persistence, new FixedClock(_now.AddSeconds(10)));
        var second = await restored.ScanAsync(CancellationToken.None);
        Assert.Equal(episode.Id, Assert.Single(second.ActiveEpisodes).Id);
        Assert.Single(await repository.ReadEventsAsync(id, 20, CancellationToken.None));
        using (var c = db.Open())
        {
            Assert.Equal(2, c.QuerySingle<int>("SELECT count(*) FROM RadarScan"));
            Assert.Contains("EpisodeQuietMinutes", c.QueryFirst<string>("SELECT ParametersJson FROM RadarScan"));
            Assert.Equal(11, c.QuerySingle<int>("SELECT MAX(Version) FROM SchemaVersion"));
        }
        await worker.StopAsync(CancellationToken.None);
    }
    [Fact]
    public async Task FailedWriteDoesNotPublishUncommittedSnapshotOrLoseEpisode()
    {
        var db = Database(); db.Initialize(); var id = db.Synchronize([new("TESTUSDT", "TEST", _now.AddDays(-30))])[0].AssetId;
        var runtime = Runtime(id); var repository = new RadarRepository(db); var persistence = new SwitchPersistence(db);
        var service = Service(runtime, repository, persistence, new FixedClock(_now));
        var first = await service.ScanAsync(CancellationToken.None);
        persistence.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => service.ScanAsync(CancellationToken.None));
        Assert.Same(first, service.GetSnapshot());
        persistence.Fail = false; var recovered = await service.ScanAsync(CancellationToken.None);
        Assert.Equal(first.ActiveEpisodes[0].Id, recovered.ActiveEpisodes[0].Id);
        Assert.Single(await repository.ReadEventsAsync(id, 20, CancellationToken.None));
    }
    [Fact]
    public async Task EmptyStartupCacheDoesNotCloseRestoredActiveEpisode()
    {
        var db = Database(); db.Initialize(); var id = db.Synchronize([new("TESTUSDT", "TEST", _now)])[0].AssetId;
        var repo = new RadarRepository(db); var write = new SwitchPersistence(db);
        var first = await Service(Runtime(id), repo, write, new FixedClock(_now)).ScanAsync(CancellationToken.None);
        var second = await Service(new MarketRuntimeStore(), repo, write, new FixedClock(_now)).ScanAsync(CancellationToken.None);
        Assert.Equal(Guid.Empty, second.ScanId); Assert.Equal(first.ActiveEpisodes[0].Id, Assert.Single(second.ActiveEpisodes).Id);
        Assert.Null(Assert.Single((await repo.RestoreAsync(CancellationToken.None)).ActiveEpisodes).EndTime);
    }
    [Fact]
    public async Task RepeatedBatchIsIdempotentAndInvalidBatchRollsBackWholeTransaction()
    {
        var db = Database(); db.Initialize(); var id = db.Synchronize([new("TESTUSDT", "TEST", _now)])[0].AssetId;
        var capture = new SwitchPersistence(db); var repo = new RadarRepository(db);
        await Service(Runtime(id), repo, capture, new FixedClock(_now)).ScanAsync(CancellationToken.None);
        var batch = capture.Last!; db.SaveRadar(batch); db.SaveRadar(batch);
        using (var c = db.Open()) Assert.Equal(1, c.QuerySingle<int>("SELECT count(*) FROM RadarEvent"));
        var invalid = batch with { Snapshot = batch.Snapshot with { ScanId = Guid.NewGuid() },
            ChangedEpisodes = [batch.ChangedEpisodes[0] with { Id = batch.ChangedEpisodes[0].Id + 1, AssetId = 99999 }] };
        Assert.Throws<SqliteException>(() => db.SaveRadar(invalid));
        using (var c = db.Open()) Assert.Equal(1, c.QuerySingle<int>("SELECT count(*) FROM RadarScan"));
    }
    [Fact]
    public void Phase02MigrationRetainsExistingAssetAndSettings()
    {
        Directory.CreateDirectory(_root);
        using (var c = new SqliteConnection($"Data Source={Path.Combine(_root, "PulletCryptoRadar.db")}"))
        {
            c.Open(); c.Execute("""
                CREATE TABLE Asset (Id INTEGER PRIMARY KEY AUTOINCREMENT,Symbol TEXT NOT NULL,Name TEXT NOT NULL,CoinGeckoId TEXT,MappingStatus INTEGER NOT NULL DEFAULT 0);
                INSERT INTO Asset(Id,Symbol,Name) VALUES(123,'ARIA','ARIA');
                CREATE TABLE Settings (Key TEXT PRIMARY KEY,Json TEXT NOT NULL);
                INSERT INTO Settings VALUES('personal','{"keep":true}');
                """);
        }
        var db = Database(); db.Initialize(); db.Initialize();
        using var check = db.Open(); Assert.Equal("ARIA", check.QuerySingle<string>("SELECT Symbol FROM Asset WHERE Id=123"));
        Assert.Equal("{\"keep\":true}", check.QuerySingle<string>("SELECT Json FROM Settings"));
    }
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    private sealed class SwitchPersistence(SqliteDatabase db) : IRadarPersistence
    {
        public bool Fail; public RadarPersistenceBatch? Last;
        public Task SaveAsync(RadarPersistenceBatch batch, CancellationToken ct)
        { if (Fail) throw new IOException("Simulated disk failure"); Last = batch; db.SaveRadar(batch); return Task.CompletedTask; }
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
