using System.Text.Json;
using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Scanner;

namespace PulletCryptoRadar.Core.Tests;

public sealed class ReplayTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 4, 0, 0, TimeSpan.Zero);
    private static FeatureValue<T> F<T>(T value, DateTimeOffset time) where T : struct => new(value, DataStatus.Valid, time, "Fixture");
    private static MarketView Market(DateTimeOffset t) => new(new(1, "TESTUSDT", "TEST", Now.AddDays(-30), true), 105, 10000000, 5,
        105, -.0001m, t, t, t, 2880, 100, 100, new()
        {
            Returns = new() { [5] = F(5m, t), [15] = F(6m, t), [60] = F(7m, t), [240] = F(8m, t) }, PriceRobustZ5m = F(4d, t), VolumeRobustZ = F(4d, t),
            DistanceToHigh = new() { [60] = F(.3m, t) }, DistanceToLow = new() { [60] = F(10m, t) },
            Derivatives = new() { OiChanges = new() { [15] = F(6m, t) }, OiRobustZ = F(4d, t), TakerBuyShare = F(.7m, t),
                TopPositionChange15m = F(3m, t), TopAccountLongShare = F(.6m, t), GlobalLongShare = F(.6m, t) }
        });
    private static ReplayFrame Frame(int minutes = 0) => new(Guid.NewGuid(), Now.AddMinutes(minutes), [Market(Now.AddMinutes(minutes))],
        JsonSerializer.Serialize(new VolatilityOptions()), JsonSerializer.Serialize(new AlertOptions()), ReplayEngine.CaptureVersion);
    private static ReplayRequest Request() => new(Now, Now.AddHours(6), "TESTUSDT");
    [Fact]
    public void ReplayReusesProductionScoresEpisodesAndBudgetPolicyWithoutSending()
    {
        var a = Frame(); var b = Frame(1); var result = new ReplayEngine().Run([a, b], Request(), CancellationToken.None);
        var expected = new UpMoveDetector(new()).Detect(a.Inputs[0], Now);
        Assert.Equal(expected.Score, result.Trace[0].Up.Score); Assert.Equal(expected.Stage, result.Trace[0].Up.Stage);
        Assert.Equal(2, result.Frames); Assert.Single(result.Signals, s => !s.IsNotification);
        Assert.Single(result.SimulatedNotifications); Assert.Equal(NotificationStatus.Pending, result.SimulatedNotifications[0].Status);
        Assert.Equal(1, result.Signals.Count(s => s.IsNotification)); Assert.Equal(result.Trace[0].EpisodeId, result.Trace[1].EpisodeId);
    }
    [Fact]
    public void DisabledNotificationPolicyIsRespectedInSimulation()
    {
        var frames = new[] { Frame(), Frame(1) }.Select(f => f with { AlertParametersJson = JsonSerializer.Serialize(new AlertOptions { ToastEnabled = false }) });
        Assert.Empty(new ReplayEngine().Run(frames, Request(), CancellationToken.None).SimulatedNotifications);
    }
    [Fact]
    public void FutureFramesCannotChangeEarlierTraceOrEnterRequestedRange()
    {
        var frames = new[] { Frame(), Frame(1), Frame(2) };
        var shortRun = new ReplayEngine().Run(frames, Request() with { To = Now.AddMinutes(1) }, CancellationToken.None);
        var longRun = new ReplayEngine().Run(frames, Request(), CancellationToken.None);
        Assert.Equal(2, shortRun.Frames); Assert.Equal(shortRun.Trace.Select(p => p.Up.Score), longRun.Trace.Take(2).Select(p => p.Up.Score));
    }
    [Fact]
    public void OutOfOrderDuplicateAndUnknownEngineVersionsAreRejected()
    {
        var engine = new ReplayEngine(); var a = Frame();
        Assert.Throws<InvalidDataException>(() => engine.Run([Frame(1), a], Request(), CancellationToken.None));
        Assert.Throws<InvalidDataException>(() => engine.Run([a, a], Request(), CancellationToken.None));
        Assert.Throws<InvalidDataException>(() => engine.Run([a with { EngineVersion = "UnknownFutureEngine" }], Request(), CancellationToken.None));
    }
    [Fact]
    public void FutureReceivedPriceAndFeatureSourceAreRejected()
    {
        var a = Frame(); var m = a.Inputs[0]; var engine = new ReplayEngine();
        Assert.Throws<InvalidDataException>(() => engine.Run([a with { Inputs = [m with { PriceReceivedTime = Now.AddSeconds(1) }] }], Request(), CancellationToken.None));
        m = m with { Features = m.Features with { VolumeRobustZ = F(5d, Now.AddSeconds(1)) } };
        Assert.Throws<InvalidDataException>(() => engine.Run([a with { Inputs = [m] }], Request(), CancellationToken.None));
    }
    [Fact]
    public void GapsAreCountedAndDataMissingCannotBeCalledContinuous()
    {
        var result = new ReplayEngine().Run([Frame(), Frame(3)], Request(), CancellationToken.None);
        Assert.Equal(1, result.GapCount); Assert.Equal(2, result.Frames);
    }
    [Fact]
    public void AuditUsesHistoricalParametersAndFlagsDrift()
    {
        var m = Market(Now); var options = new VolatilityOptions { UpEntryScore = 99 };
        var a = new UpMoveDetector(options).Detect(m, Now);
        var e = new RadarEvent(Guid.NewGuid(), Guid.NewGuid(), 1, 1, "TESTUSDT", MoveDirection.Up, "Created", Now, a.StrategyVersion, m, a, new());
        var engine = new ReplayEngine(); var input = new ReplayAuditInput(e, JsonSerializer.Serialize(options));
        Assert.Equal("Match", engine.Audit(input).Status);
        Assert.Equal("Mismatch", engine.Audit(input with { Event = e with { Assessment = a with { Score = a.Score - 1 } } }).Status);
        Assert.Equal("UnsupportedVersion", engine.Audit(input with { Event = e with { StrategyVersion = "OldUnavailable" } }).Status);
        Assert.Equal("MissingInput", engine.Audit(input with { Event = e with { Input = null } }).Status);
    }
    [Fact]
    public void RawFeatureReplayFiltersClosedButNotYetReceivedCandlesAndDerivativeBackfill()
    {
        var m = Market(Now); var bars = Enumerable.Range(0, 40).Select(i => new Candle("TESTUSDT", 5, Now.AddMinutes((i - 40) * 5),
            100, 102, 99, 100 + i % 3, 100 + i % 5, Now.AddMinutes((i - 39) * 5).AddMilliseconds(-1), Now.AddMinutes((i - 39) * 5))).ToArray();
        var late = bars[^1] with { ReceivedTime = Now.AddSeconds(1), QuoteVolume = 1000000 };
        var derivative = new DerivativeSample("TESTUSDT", DerivativeKind.CurrentOpenInterest, Now.AddMinutes(-1), Now.AddSeconds(1), Quantity: 100);
        var raw = new DerivativeRuntimeSnapshot("TESTUSDT", new Dictionary<DerivativeKind, IReadOnlyList<DerivativeSample>> { [derivative.Kind] = [derivative] }, [], null, null);
        var expected = new FeatureEngine().Calculate(m, [], bars.SkipLast(1).ToArray(), [], Now);
        var replayed = ReplayEngine.Recalculate(m, [.. bars.SkipLast(1), late], raw, Now);
        Assert.Equal(expected.VolumeRobustZ, replayed.Features.VolumeRobustZ); Assert.Null(replayed.Features.Derivatives.OpenInterestQuantity.Value);
    }
    [Fact]
    public void CancellationIsObservedBeforeAdvancingState()
    {
        using var source = new CancellationTokenSource(); source.Cancel();
        Assert.Throws<OperationCanceledException>(() => new ReplayEngine().Run([Frame()], Request(), source.Token));
    }
}
