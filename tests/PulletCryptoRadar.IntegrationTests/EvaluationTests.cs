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

public sealed class EvaluationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PulletCryptoRadar-tests", Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset _now = new(2026, 10, 29, 4, 0, 0, TimeSpan.Zero);
    private SqliteDatabase Database() => new(Options.Create(new StorageOptions { Directory = _root, BackupDirectory = Path.Combine(_root, "Backup") }));
    private static string Parameters => JsonSerializer.Serialize(new VolatilityOptions());
    private RadarPersistenceBatch Batch(SqliteDatabase db, DateTimeOffset? at = null)
    {
        var time = at ?? _now;
        var id = db.Synchronize([new("TESTUSDT", "TEST", _now.AddDays(-30))])[0].AssetId;
        var m = new MarketView(new(id, "TESTUSDT", "TEST", _now.AddDays(-30), true), .123456789012345678901m,
            10000000, null, null, null, time, time, null, 0, 0, 0, new());
        var regime = new MarketRegimeDetector(new()).Detect([m], time); var scanId = Guid.NewGuid();
        var up = new UpMoveDetector(new()).Detect(m, time); var down = new DownMoveDetector(new()).Detect(m, time);
        var episode = new MoveEpisode { Id = time.ToUnixTimeMilliseconds(), AssetId = id, Symbol = m.Instrument.Symbol, StartTime = time,
            LastObservedTime = time, StartPrice = m.Price!.Value, Direction = MoveDirection.Up, StrategyVersion = up.StrategyVersion };
        var e = new RadarEvent(Guid.NewGuid(), scanId, episode.Id, id, m.Instrument.Symbol, MoveDirection.Up, "Created", time, up.StrategyVersion, m, up, episode);
        var snapshot = new RadarSnapshot(scanId, time, regime, [new(m, up, down)], [episode], [e], Parameters);
        return new(snapshot, [episode], [e]) { ReplayFrame = new(scanId, time, [m], Parameters, JsonSerializer.Serialize(new AlertOptions()), ReplayEngine.CaptureVersion) };
    }
    [Fact]
    public void FrameAndEventCommitIsAtomicAndIdempotentWithExactDecimal()
    {
        var db = Database(); db.Initialize(); var b = Batch(db); db.SaveRadar(b); db.SaveRadar(b);
        var repo = new EvaluationRepository(db); var frame = Assert.Single(repo.ReadFrames(_now, _now.AddSeconds(1), CancellationToken.None));
        Assert.Equal(b.ReplayFrame!.Inputs[0].Price, frame.Inputs[0].Price); Assert.Equal(b.Snapshot.ParametersJson, frame.ParametersJson);
        using var c = db.Open(); Assert.Equal(1, c.QuerySingle<int>("SELECT count(*) FROM ReplayFrame"));
        var broken = Batch(db, _now.AddMinutes(1));
        broken = broken with { ReplayFrame = broken.ReplayFrame! with { ScanId = Guid.NewGuid() } };
        Assert.Throws<SqliteException>(() => db.SaveRadar(broken));
        Assert.Equal(1, c.QuerySingle<int>("SELECT count(*) FROM ReplayFrame")); Assert.Equal(1, c.QuerySingle<int>("SELECT count(*) FROM RadarEvent"));
    }
    [Fact]
    public void OldSchemaIsMigratedWithoutDeletingExistingEventsOrMarketRows()
    {
        var db = Database(); db.Initialize(); var b = Batch(db); db.SaveRadar(b);
        using (var c = db.Open()) c.Execute("DROP TABLE ReplayFrame; DROP TABLE EvaluationEntry; DROP TABLE SignalEvaluation; DELETE FROM SchemaVersion WHERE Version=5");
        db.Initialize();
        using var migrated = db.Open(); Assert.Equal(11, migrated.QuerySingle<int>("SELECT MAX(Version) FROM SchemaVersion"));
        Assert.Equal(1, migrated.QuerySingle<int>("SELECT count(*) FROM RadarEvent")); Assert.Equal(1, migrated.QuerySingle<int>("SELECT count(*) FROM Asset"));
    }
    [Fact]
    public void FrameRetentionDoesNotDeleteDurableEpisodeAuditInput()
    {
        var db = Database(); db.Initialize(); var first = Batch(db); db.SaveRadar(first);
        var second = Batch(db, _now.AddDays(8)); db.SaveRadar(second);
        var repo = new EvaluationRepository(db); Assert.Equal(1, repo.GetAvailability().Frames);
        Assert.Equal(2, repo.ReadAuditInputs(10).Count); Assert.Empty(repo.ReadFrames(_now, _now.AddSeconds(1), CancellationToken.None));
    }
    [Fact]
    public void ReplayingRepositoryFramesCannotModifyLiveEpisodesOrNotifications()
    {
        var db = Database(); db.Initialize(); var b = Batch(db); db.SaveRadar(b);
        var repo = new EvaluationRepository(db);
        using var c = db.Open(); var before = c.QuerySingle<string>("SELECT Payload FROM MoveEpisode");
        var result = new ReplayEngine().Run(repo.ReadFrames(_now, _now.AddMinutes(1), CancellationToken.None), new(_now, _now.AddMinutes(1), "TESTUSDT"), CancellationToken.None);
        Assert.Single(result.Trace); Assert.Equal(before, c.QuerySingle<string>("SELECT Payload FROM MoveEpisode"));
        Assert.Equal(0, c.QuerySingle<int>("SELECT count(*) FROM AlertEvent")); Assert.Equal("Match", new ReplayEngine().Audit(Assert.Single(repo.ReadAuditInputs(1))).Status);
    }
    [Fact]
    public async Task EvaluationReportPersistsThroughSingleQueueAndRestoresAfterRestart()
    {
        var db = Database(); var queue = new PersistenceQueue();
        using var worker = new PersistenceWorker(db, queue, new ProviderHealthStore(TimeProvider.System), NullLogger<PersistenceWorker>.Instance);
        await worker.StartAsync(CancellationToken.None); await queue.Ready.Task;
        var b = Batch(db); db.SaveRadar(b); var repo = new EvaluationRepository(db);
        var service = new EvaluationService(repo, new EvaluationPersistence(queue), new(), new(), new(), new FixedClock(_now.AddHours(1)));
        var report = await service.RefreshAsync(CancellationToken.None);
        var restored = new EvaluationRepository(Database()).ReadLatest(); Assert.NotNull(restored);
        Assert.Equal(report.Id, restored.Id); Assert.Single(restored.Signals); Assert.Equal(EvaluationStatus.AwaitingEntry, restored.Signals[0].Status);
        Assert.Null(restored.Signals[0].Hit20); Assert.Equal(b.ReplayFrame!.Inputs[0].Price, restored.Signals[0].Signal.Price);
        await worker.StopAsync(CancellationToken.None);
    }
    [Fact]
    public void NotificationEntryIsFrozenBeforeReplayRetentionAndMissingOldInputsRemainUnknown()
    {
        var db = Database(); db.Initialize(); var b = Batch(db); db.SaveRadar(b);
        var notification = new NotificationRecord(Guid.NewGuid(), "Episode:1:1", NotificationKind.Normal, b.ChangedEpisodes[0].Id,
            b.ReplayFrame!.Inputs[0].Instrument.AssetId, "TESTUSDT", MoveDirection.Up, SignalGrade.A, 90, "Ignition", _now,
            _now.AddMinutes(2), NotificationStatus.Pending, "Fixture", "Fixture", StrategyVersion: UpMoveDetector.Version);
        db.SaveAlertRun(new(b.Snapshot.ScanId, _now, new(new([]), [notification], []), "{}"));
        using (var c = db.Open()) c.Execute("DELETE FROM ReplayFrame");
        var data = new EvaluationRepository(db).ReadData(_now.AddMinutes(-1), _now.AddHours(1), 100, CancellationToken.None);
        var captured = Assert.Single(data.Signals, s => s.IsNotification); Assert.Equal(b.ReplayFrame.Inputs[0].Price, captured.Price);
        using (var c = db.Open()) c.Execute("DELETE FROM EvaluationEntry");
        var old = new EvaluationRepository(db).ReadData(_now.AddMinutes(-1), _now.AddHours(1), 100, CancellationToken.None);
        Assert.Equal(0, Assert.Single(old.Signals, s => s.IsNotification).Price);
        var evaluation = new SignalEvaluationEngine().Build(old, _now.AddMinutes(-1), _now.AddDays(2), 3);
        Assert.Null(Assert.Single(evaluation.Precision).Percent);
    }
    [Fact]
    public async Task ReplayServiceRejectsFutureAndOversizedWindows()
    {
        var db = Database(); db.Initialize();
        var service = new EvaluationService(new EvaluationRepository(db), new NoWrites(), new(), new(), new(), new FixedClock(_now));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ReplayAsync(new(_now.AddHours(-7), _now), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ReplayAsync(new(_now, _now.AddHours(1)), CancellationToken.None));
    }
    [Fact]
    public void OverLimitSignalReadIsExplicitlyTruncatedRatherThanReportingGlobalAccuracy()
    {
        var db = Database(); db.Initialize();
        for (var i = 0; i < 4; i++) db.SaveRadar(Batch(db, _now.AddMinutes(i)));
        var data = new EvaluationRepository(db).ReadData(_now.AddMinutes(-1), _now.AddHours(1), 3, CancellationToken.None);
        Assert.True(data.Truncated); Assert.Equal(3, data.Signals.Count);
        var report = new SignalEvaluationEngine().Build(data, _now.AddMinutes(-1), _now.AddDays(2), 3);
        Assert.True(report.SignalsTruncated); Assert.All(report.Precision, p => Assert.Null(p.Percent)); Assert.All(report.Recall, r => Assert.Null(r.Percent));
    }
    [Fact]
    public void AvailabilityPreservesSubMillisecondEndpointsForInclusiveReplay()
    {
        var db = Database(); db.Initialize(); var b = Batch(db, _now.AddTicks(1234)); db.SaveRadar(b);
        var repo = new EvaluationRepository(db); var available = repo.GetAvailability();
        Assert.Equal(b.Snapshot.AsOf, available.First); Assert.Equal(b.Snapshot.AsOf, available.Last);
        var result = new ReplayEngine().Run(repo.ReadFrames(available.First!.Value, available.Last!.Value, CancellationToken.None),
            new(available.First.Value, available.Last.Value, "TESTUSDT"), CancellationToken.None);
        Assert.Equal(1, result.Frames);
    }
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    private sealed class NoWrites : IEvaluationPersistence { public Task SaveAsync(EvaluationReport report, CancellationToken ct) => throw new InvalidOperationException("Should never write"); }
    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
