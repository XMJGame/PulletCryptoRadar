using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;
using PulletCryptoRadar.Scanner;

namespace PulletCryptoRadar.IntegrationTests;

public sealed class AlertTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PulletCryptoRadar-tests", Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset _now = new(2026, 10, 29, 4, 0, 0, TimeSpan.Zero);
    private SqliteDatabase Database() { var db = new SqliteDatabase(Options.Create(new StorageOptions { Directory = _root })); db.Initialize(); return db; }
    private NotificationRecord Notification(NotificationStatus status = NotificationStatus.Pending)
        => new(Guid.NewGuid(), Guid.NewGuid().ToString(), NotificationKind.Normal, 1, 1, "TESTUSDT", MoveDirection.Up,
            SignalGrade.A, 88, "Ignition", _now, _now.AddMinutes(2), status, "Test", "Body");
    private AlertRun Run(params NotificationRecord[] notifications) => new(Guid.NewGuid(), _now, new(new([]), notifications, []), "{}");
    private RadarSnapshot SystemicScan() => new(Guid.NewGuid(), _now, new(_now, MarketRegime.SystemicCrash, DataStatus.Valid,
        40, 40, 0, 40, 0, 40, 0, 10, -8, -9, -9, "Systemic fixture"), [], [], [], "{}");
    [Fact]
    public async Task QueuePersistsRunAndAtomicClaimCannotBeRepeated()
    {
        var db = Database(); var queue = new PersistenceQueue();
        using var worker = new PersistenceWorker(db, queue, new ProviderHealthStore(TimeProvider.System), NullLogger<PersistenceWorker>.Instance);
        await worker.StartAsync(CancellationToken.None); await queue.Ready.Task;
        var write = new AlertPersistence(queue); var n = Notification(); var run = Run(n);
        await write.SaveRunAsync(run, CancellationToken.None); await write.SaveRunAsync(run, CancellationToken.None);
        Assert.Single((await new AlertRepository(db).RestoreAsync(CancellationToken.None)).Notifications);
        Assert.True(await write.TransitionAsync(n.Id, NotificationStatus.Pending, NotificationStatus.Dispatching, null, _now, CancellationToken.None));
        Assert.False(await write.TransitionAsync(n.Id, NotificationStatus.Pending, NotificationStatus.Dispatching, null, _now, CancellationToken.None));
        await worker.StopAsync(CancellationToken.None);
    }
    [Fact]
    public async Task ConflictingDedupKeyRollsBackWholePolicyRun()
    {
        var db = Database(); var n = Notification(); db.SaveAlertRun(Run(n));
        var duplicate = Run(n with { Id = Guid.NewGuid() });
        Assert.Throws<SqliteException>(() => db.SaveAlertRun(duplicate));
        using var c = db.Open(); Assert.Equal(1, c.QuerySingle<int>("SELECT count(*) FROM AlertRun"));
        Assert.Single((await new AlertRepository(db).RestoreAsync(CancellationToken.None)).Notifications);
    }
    [Fact]
    public async Task CrashDuringDispatchBecomesIndeterminateAndNeverResends()
    {
        var db = Database(); var n = Notification(NotificationStatus.Dispatching); db.SaveAlertRun(Run(n));
        var sender = new StubSender(); var repository = new AlertRepository(db); var write = new DirectPersistence(db);
        var service = Service(new SnapshotSource(null), repository, write, sender);
        await service.TickAsync(CancellationToken.None); await service.TickAsync(CancellationToken.None);
        var saved = Assert.Single(await repository.ReadNotificationsAsync(10, CancellationToken.None));
        Assert.Equal(NotificationStatus.Indeterminate, saved.Status); Assert.Equal(0, sender.Calls);
    }
    [Fact]
    public async Task SuccessfulDispatchIsSubmittedOnceAcrossRestart()
    {
        var db = Database(); var n = Notification(); db.SaveAlertRun(Run(n));
        var sender = new StubSender(); var source = new SnapshotSource(null); var repo = new AlertRepository(db); var write = new DirectPersistence(db);
        await Service(source, repo, write, sender).TickAsync(CancellationToken.None);
        await Service(source, repo, write, sender).TickAsync(CancellationToken.None);
        Assert.Equal(1, sender.Calls);
        Assert.Equal(NotificationStatus.Submitted, Assert.Single(await repo.ReadNotificationsAsync(10, CancellationToken.None)).Status);
    }
    [Fact]
    public async Task PendingExpiredNotificationIsNotSentOnRestart()
    {
        var db = Database(); db.SaveAlertRun(Run(Notification() with { ExpiresAt = _now.AddSeconds(-1) }));
        var sender = new StubSender(); var repo = new AlertRepository(db);
        await Service(new(null), repo, new DirectPersistence(db), sender).TickAsync(CancellationToken.None);
        Assert.Equal(0, sender.Calls); Assert.Equal(NotificationStatus.Expired, Assert.Single(await repo.ReadNotificationsAsync(10, CancellationToken.None)).Status);
    }
    [Fact]
    public async Task CommitFailureNeverCallsExternalSender()
    {
        var db = Database(); var sender = new StubSender(); var write = new DirectPersistence(db) { FailBeforeSave = true };
        await Assert.ThrowsAsync<IOException>(() => Service(new(SystemicScan()), new AlertRepository(db), write, sender).TickAsync(CancellationToken.None));
        Assert.Equal(0, sender.Calls); Assert.Empty((await new AlertRepository(db).RestoreAsync(CancellationToken.None)).Notifications);
    }
    [Fact]
    public async Task CommitAcknowledgementLostIsRecoveredFromAuthoritativeOutbox()
    {
        var db = Database(); var source = new SnapshotSource(SystemicScan()); var sender = new StubSender(); var repo = new AlertRepository(db);
        var write = new DirectPersistence(db) { FailAfterSaveOnce = true }; var service = Service(source, repo, write, sender);
        await Assert.ThrowsAsync<IOException>(() => service.TickAsync(CancellationToken.None)); Assert.Equal(0, sender.Calls);
        await service.TickAsync(CancellationToken.None); Assert.Equal(1, sender.Calls);
        Assert.Single(await repo.ReadNotificationsAsync(10, CancellationToken.None));
    }
    [Fact]
    public async Task FailureRecordingSendOutcomeDoesNotCauseSecondSubmission()
    {
        var db = Database(); db.SaveAlertRun(Run(Notification())); var sender = new StubSender(); var repo = new AlertRepository(db);
        var write = new DirectPersistence(db) { FailOutcomeOnce = true }; var service = Service(new(null), repo, write, sender);
        await Assert.ThrowsAsync<IOException>(() => service.TickAsync(CancellationToken.None)); Assert.Equal(1, sender.Calls);
        await service.TickAsync(CancellationToken.None); Assert.Equal(1, sender.Calls);
        Assert.Equal(NotificationStatus.Indeterminate, Assert.Single(await repo.ReadNotificationsAsync(10, CancellationToken.None)).Status);
    }
    [Fact]
    public async Task DisabledToastDoesNotCallSenderAndRetainsReservation()
    {
        var db = Database(); db.SaveAlertRun(Run(Notification())); var sender = new StubSender(); var repo = new AlertRepository(db);
        await Service(new(null), repo, new DirectPersistence(db), sender, new() { ToastEnabled = false }).TickAsync(CancellationToken.None);
        Assert.Equal(0, sender.Calls); Assert.Equal(NotificationStatus.Disabled, Assert.Single(await repo.ReadNotificationsAsync(10, CancellationToken.None)).Status);
    }
    private AlertService Service(SnapshotSource source, AlertRepository repo, DirectPersistence write, StubSender sender, AlertOptions? options = null)
    { options ??= new(); return new(source, new(options), options, repo, write, sender, new Clock(_now)); }
    private sealed class SnapshotSource(RadarSnapshot? snapshot) : IRadarSnapshotSource { public RadarSnapshot? GetSnapshot() => snapshot; }
    private sealed class Clock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    private sealed class StubSender : INotificationSender
    {
        public int Calls;
        public Task<ToastResult> SendAsync(NotificationRecord n, CancellationToken ct) { Calls++; return Task.FromResult(new ToastResult(NotificationStatus.Submitted)); }
    }
    private sealed class DirectPersistence(SqliteDatabase db) : IAlertPersistence
    {
        public bool FailBeforeSave, FailAfterSaveOnce, FailOutcomeOnce;
        public Task SaveRunAsync(AlertRun run, CancellationToken ct)
        {
            if (FailBeforeSave) throw new IOException("Before commit"); db.SaveAlertRun(run);
            if (FailAfterSaveOnce) { FailAfterSaveOnce = false; throw new IOException("Lost acknowledgement"); }
            return Task.CompletedTask;
        }
        public Task<bool> TransitionAsync(Guid id, NotificationStatus expected, NotificationStatus next, string? error, DateTimeOffset now, CancellationToken ct)
        {
            if (FailOutcomeOnce && next == NotificationStatus.Submitted) { FailOutcomeOnce = false; throw new IOException("Outcome save failure"); }
            return Task.FromResult(db.TransitionNotification(id, expected, next, error, now));
        }
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
