using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;

namespace PulletCryptoRadar.IntegrationTests;

public sealed class MappingStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PulletCryptoRadar-tests", Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset _now = new(2026, 10, 29, 4, 0, 0, TimeSpan.Zero);
    private SqliteDatabase Database() => new(Options.Create(new StorageOptions { Directory = _root, BackupDirectory = Path.Combine(_root, "Backup") }));
    private CoinCatalog Catalog() => new(_now, [new("test", "test", "Test", new Dictionary<string, string> { ["ethereum"] = "0x" + new string('a', 40) })]);
    private AssetMapping Mapping(int id, long revision = 1) => new(id, "test", MappingStatus.ManualVerified, ["test"], 1000,
        "ethereum", "0x" + new string('a', 40), revision, _now, "Fixture manual verification");
    private int Asset(SqliteDatabase db) => db.Synchronize([new("TESTUSDT", "TEST", _now.AddDays(-30))])[0].AssetId;
    [Fact]
    public void MappingCanonicalAssetChainAndAuditCommitTogetherAndRejectStaleEditor()
    {
        var db = Database(); db.Initialize(); var id = Asset(db); db.SaveCoinCatalog(Catalog()); db.SaveMappings([new(Mapping(id), 0)]);
        using var c = db.Open(); Assert.Equal("test", c.QuerySingle<string>("SELECT CoinGeckoId FROM Asset"));
        Assert.Equal(4, c.QuerySingle<int>("SELECT MappingStatus FROM Asset")); Assert.Equal(1, c.QuerySingle<int>("SELECT count(*) FROM ChainToken"));
        Assert.Single(new MappingRepository(db).ReadHistory(id));
        Assert.Throws<MappingConflictException>(() => db.SaveMappings([new(Mapping(id, 2), 0)]));
        Assert.Equal(1, c.QuerySingle<int>("SELECT Revision FROM AssetMapping"));
    }
    [Fact]
    public void InvalidBatchRollsBackEarlierMappingAndChainMutations()
    {
        var db = Database(); db.Initialize(); var id = Asset(db); db.SaveMappings([new(Mapping(id), 0)]);
        var clear = Mapping(id, 2) with { CoinGeckoId = null, Status = MappingStatus.Unknown, Platform = null, ContractAddress = null };
        Assert.Throws<MappingConflictException>(() => db.SaveMappings([new(clear, 1), new(Mapping(id, 3), 1)]));
        using var c = db.Open(); Assert.Equal(1, c.QuerySingle<int>("SELECT Revision FROM AssetMapping"));
        Assert.Equal(1, c.QuerySingle<int>("SELECT count(*) FROM ChainToken")); Assert.Equal("test", c.QuerySingle<string>("SELECT CoinGeckoId FROM Asset"));
    }
    [Fact]
    public void CatalogAndCapitalRestoreExactlyAndOlderReceivedRevisionCannotOverwrite()
    {
        var db = Database(); db.Initialize(); db.SaveCoinCatalog(Catalog());
        var s = new MarketCapitalSnapshot("test", _now, _now, .123456789012345678901m, null, 0, null, null, null, null);
        db.SaveMarketCapital([s]); db.SaveMarketCapital([s]); db.SaveMarketCapital([s with { MarketCapUsd = 999, ReceivedTime = _now.AddSeconds(-1) }]);
        var state = new MappingRepository(db).ReadState(); Assert.Single(state.Catalog!.Coins);
        Assert.Equal(s, Assert.Single(state.Markets)); using var c = db.Open(true); Assert.Equal(1, c.QuerySingle<int>("SELECT count(*) FROM MarketCapSnapshot"));
        Assert.Throws<InvalidDataException>(() => db.SaveCoinCatalog(new(_now, []))); Assert.NotNull(new MappingRepository(db).ReadState().Catalog);
    }
    [Fact]
    public void RuntimeSwitchingIdentityCannotReuseOldCoinMarketCap()
    {
        var store = new CapitalRuntimeStore(); store.SetCatalog(Catalog()); store.SetMappings([Mapping(1)]);
        store.SetMarkets([new("test", _now, _now, 100, null, null, null, null, null, null)]); Assert.NotNull(store.Get(1).Snapshot);
        store.SetMappings([Mapping(1, 2) with { CoinGeckoId = "other" }]); Assert.Null(store.Get(1).Snapshot); Assert.False(store.Get(1).CatalogContainsId);
        store.SetMappings([Mapping(1)]); Assert.Equal("other", store.GetMapping(1)!.CoinGeckoId);
    }
    [Fact]
    public async Task SingleWriterManualEditSurvivesRestartAndBackgroundSuggestions()
    {
        var db = Database(); var queue = new PersistenceQueue();
        using var worker = new PersistenceWorker(db, queue, new ProviderHealthStore(TimeProvider.System), NullLogger<PersistenceWorker>.Instance);
        await worker.StartAsync(CancellationToken.None); await queue.Ready.Task;
        var instrument = Assert.Single(await queue.SyncAsync([new("TESTUSDT", "TEST", _now.AddDays(-30))], CancellationToken.None));
        var market = new MarketRuntimeStore(); market.Register([instrument]); var runtime = new CapitalRuntimeStore();
        MappingService Service(ICapitalRuntimeStore target) => new(new MappingRepository(db), new MappingPersistence(queue), target, market, new(), new(), new Clock(_now));
        var service = Service(runtime); await service.RestoreAsync(CancellationToken.None); await service.ApplyCatalogAsync(Catalog(), CancellationToken.None);
        Assert.Equal(MappingStatus.SymbolMatched, runtime.GetMapping(instrument.AssetId)!.Status);
        var current = runtime.GetMapping(instrument.AssetId)!;
        await service.SetManualAsync(instrument.AssetId, new("test", current.Revision, true, "ethereum", "0x" + new string('a', 40)), CancellationToken.None);
        await service.ReconcileAsync(CancellationToken.None);
        var restored = new CapitalRuntimeStore(); await Service(restored).RestoreAsync(CancellationToken.None);
        Assert.Equal(MappingStatus.ManualVerified, restored.GetMapping(instrument.AssetId)!.Status); Assert.Equal(2, restored.GetMapping(instrument.AssetId)!.Revision);
        Assert.Equal(2, new MappingRepository(db).ReadHistory(instrument.AssetId).Count);
        await worker.StopAsync(CancellationToken.None);
    }
    private sealed class Clock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
