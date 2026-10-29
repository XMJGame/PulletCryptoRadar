using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Core.Tests;

public sealed class SignalEvaluationTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.FromHours(8));
    private readonly SignalEvaluationEngine _engine = new();
    private static EvaluationSignal Signal(MoveDirection direction = MoveDirection.Up, bool notification = false, int id = 1, double score = 90)
        => new($"S:{id}:{notification}", id, $"TEST{id}USDT", direction, Start, 108, score, "Fixture", id, 100, notification);
    private static Candle[] Bars(EvaluationSignal s, decimal high = 162, decimal low = 86.4m)
    {
        var first = DateTimeOffset.FromUnixTimeMilliseconds((s.Time.ToUnixTimeMilliseconds() + 299999) / 300000 * 300000);
        return Enumerable.Range(0, 288).Select(i => new Candle(s.Symbol, 5, first.AddMinutes(i * 5), 108,
            i == 50 ? high : 108, i == 100 ? low : 108, 108, 1000,
            first.AddMinutes((i + 1) * 5).AddMilliseconds(-1), first.AddMinutes((i + 1) * 5))).ToArray();
    }
    [Fact]
    public void MatureUpSignalUsesDirectionalExcursionsAndCommonReferenceLead()
    {
        var s = Signal(); var e = _engine.Evaluate(s, Bars(s), Start.AddDays(2));
        Assert.Equal(EvaluationStatus.Complete, e.Status); Assert.Equal(50, e.Mfe); Assert.Equal(20, e.Mae);
        Assert.True(e.Hit20); Assert.True(e.Hit50); Assert.True(e.Drop20); Assert.False(e.Drop30);
        Assert.Equal(54, e.CaptureLead); Assert.Equal(21.6m, e.CrashLead);
    }
    [Fact]
    public void DownSignalSwapsFavorableAndAdverseAndMeasuresCrashLeadInPoints()
    {
        var s = Signal(MoveDirection.Down) with { Price = 92 };
        var bars = Bars(s, 120, 60).Select(b => b with { Open = 100, Close = 100, High = Math.Max(b.High, 100), Low = Math.Min(b.Low, 100) }).ToArray();
        var e = _engine.Evaluate(s, bars, Start.AddDays(2));
        Assert.Equal(40, e.Mfe); Assert.Equal(20, e.Mae); Assert.True(e.Drop30); Assert.Equal(32, e.CrashLead);
    }
    [Fact]
    public void UnfinishedWindowIsPendingRatherThanFailureAndDoesNotReadFutureBars()
    {
        var s = Signal(); var bars = Bars(s); var e = _engine.Evaluate(s, bars, Start.AddHours(1));
        Assert.Equal(EvaluationStatus.Pending, e.Status); Assert.Equal(12, e.ObservedBars);
        Assert.Null(e.Hit20); Assert.Null(e.Drop20); Assert.Equal(0, e.Mfe); Assert.Null(e.CaptureLead);
    }
    [Theory] [InlineData(1)] [InlineData(120)] [InlineData(287)]
    public void GapsRemainUnknownAfterHorizonEvenIfAnObservedHighExceededThreshold(int missing)
    {
        var s = Signal(); var bars = Bars(s).Where((_, i) => i != missing).ToArray();
        var e = _engine.Evaluate(s, bars, Start.AddDays(2));
        Assert.Equal(EvaluationStatus.Incomplete, e.Status); Assert.Null(e.Hit50); Assert.Null(e.Drop30); Assert.Equal(287, e.ObservedBars);
    }
    [Fact]
    public void MissingEntryDoesNotSubstituteLaterOpen()
    {
        var s = Signal(); var e = _engine.Evaluate(s, Bars(s).Skip(1).ToArray(), Start.AddDays(2));
        Assert.Equal(EvaluationStatus.AwaitingEntry, e.Status); Assert.Null(e.EntryPrice); Assert.Null(e.Mfe);
    }
    [Fact]
    public void MidCandleSignalExcludesPreSignalExtremeAndUsesNextOpen()
    {
        var s = Signal() with { Time = Start.AddMinutes(2) }; var bars = Bars(s, 108, 108);
        var straddling = bars[0] with { OpenTime = Start, High = 1000, Low = 1, SourceTime = Start.AddMinutes(5), ReceivedTime = Start.AddMinutes(5) };
        var e = _engine.Evaluate(s, [straddling, .. bars], Start.AddDays(2));
        Assert.Equal(Start.AddMinutes(5), e.EntryTime); Assert.Equal(0, e.Mfe); Assert.Equal(0, e.Mae);
    }
    [Fact]
    public void FutureReceivedRevisionCannotFillHistoricalEvaluationGap()
    {
        var s = Signal(); var bars = Bars(s); bars[20] = bars[20] with { ReceivedTime = Start.AddDays(3) };
        Assert.Equal(EvaluationStatus.Incomplete, _engine.Evaluate(s, bars, Start.AddDays(2)).Status);
    }
    [Fact]
    public void BarsOutsideWindowAndOtherSymbolsDoNotChangeMetrics()
    {
        var s = Signal(); var bars = Bars(s); var first = _engine.Evaluate(s, bars, Start.AddDays(2));
        var extra = bars[0] with { Symbol = "OTHERUSDT", High = 100000 };
        var after = extra with { Symbol = s.Symbol, OpenTime = Start.AddDays(1) };
        var second = _engine.Evaluate(s, [.. bars, extra, after], Start.AddDays(2));
        Assert.Equal(first, second);
    }
    [Fact]
    public void MissingReferenceKeepsLeadUnknownWithoutDiscardingCompleteLabels()
    {
        var s = Signal() with { ReferencePrice = null }; var e = _engine.Evaluate(s, Bars(s), Start.AddDays(2));
        Assert.True(e.Hit50); Assert.Null(e.CaptureLead); Assert.Null(e.CrashLead);
    }
    [Fact]
    public void DailyPrecisionDeduplicatesFirstNotificationAndDoesNotPickBestOutcome()
    {
        var a = Signal(notification: true); var duplicate = a with { Key = "later", Time = Start.AddHours(1), Score = 100 };
        var b = Signal(notification: true, id: 2, score: 85); var c = Signal(notification: true, id: 3, score: 80);
        var data = new EvaluationData([a, duplicate, b, c], new Dictionary<int, IReadOnlyList<Candle>>
            { [1] = Bars(a), [2] = Bars(b), [3] = Bars(c, 108, 108) }, [], [], false);
        var p = Assert.Single(_engine.Build(data, Start, Start.AddDays(2), 3).Precision);
        Assert.Equal(3, p.Selected); Assert.Equal(3, p.Completed); Assert.Equal(2, p.Hits); Assert.Equal(200m / 3, p.Percent);
        Assert.Null(Assert.Single(_engine.Build(data with { Truncated = true }, Start, Start.AddDays(2), 3).Precision).Percent);
    }
    [Fact]
    public void IncompleteSelectedNotificationPreventsMisleadingPrecision()
    {
        var a = Signal(notification: true); var b = Signal(notification: true, id: 2);
        var data = new EvaluationData([a, b], new Dictionary<int, IReadOnlyList<Candle>> { [1] = Bars(a) }, [], [], false);
        var p = Assert.Single(_engine.Build(data, Start, Start.AddDays(2), 3).Precision);
        Assert.Equal(2, p.Selected); Assert.Equal(1, p.Completed); Assert.Null(p.Percent);
    }
    [Fact]
    public void RecallRequiresWholeDayScanCoverageAndConservativeEarlyCrossing()
    {
        var s = Signal() with { Price = 110, Time = Start.AddMinutes(1) }; var late = s with { Key = "late", AssetId = 2, Symbol = "TEST2USDT", Time = Start.AddMinutes(251) };
        var candles = new Dictionary<int, IReadOnlyList<Candle>> { [1] = Bars(Signal(), 170, 108), [2] = Bars(Signal(id: 2), 170, 108) };
        var instruments = new[] { new Instrument(1, "TEST1USDT", "TEST1", Start.AddDays(-30), false), new Instrument(2, "TEST2USDT", "TEST2", Start.AddDays(-30), true) };
        var scans = Enumerable.Range(0, 1440).Select(i => Start.AddMinutes(i)).ToArray();
        var data = new EvaluationData([s, late], candles, instruments, scans, false);
        var r = _engine.Build(data, Start, Start.AddDays(1), 3).Recall.Single(x => x.Direction == MoveDirection.Up);
        Assert.Equal(2, r.ExtremeAssets); Assert.Equal(1, r.CapturedAssets); Assert.Equal(50, r.Percent);
        Assert.Equal(2, r.CoveredAssets); Assert.True(r.ScanCoverageComplete);
        Assert.Null(_engine.Build(data with { ScanTimes = scans.Skip(20).ToArray() }, Start, Start.AddDays(1), 3).Recall.First().Percent);
    }
    [Fact]
    public void NoExtremeAndEmptySignalsRemainUnknownRatherThanZeroAccuracy()
    {
        var report = _engine.Build(new([], new Dictionary<int, IReadOnlyList<Candle>>(), [], [], false), Start, Start.AddDays(1), 3);
        Assert.Empty(report.Signals); Assert.Empty(report.Precision); Assert.All(report.Recall, r => Assert.Null(r.Percent));
    }
    [Fact]
    public void PartialFirstCalendarDayCannotProduceDailyTopKPrecision()
    {
        var s = Signal(notification: true) with { Time = Start.AddHours(10) };
        var data = new EvaluationData([s], new Dictionary<int, IReadOnlyList<Candle>> { [1] = Bars(s) }, [], [], false);
        var p = Assert.Single(_engine.Build(data, Start.AddHours(1), Start.AddDays(2), 3).Precision);
        Assert.Equal(1, p.Completed); Assert.Null(p.Percent);
    }
    [Fact]
    public void MissingNotificationInputCannotBeRatedUsingMarketCandleAlone()
    {
        var s = Signal(notification: true) with { Price = 0 };
        var e = _engine.Evaluate(s, Bars(s), Start.AddDays(2));
        Assert.Equal(EvaluationStatus.AwaitingEntry, e.Status); Assert.Null(e.Hit20); Assert.Null(e.Mfe);
    }
}
