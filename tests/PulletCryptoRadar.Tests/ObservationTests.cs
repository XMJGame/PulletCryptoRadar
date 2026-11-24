using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;

namespace PulletCryptoRadar.Tests;

public sealed class ObservationTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026,11,3,10,0,0,TimeSpan.Zero);
    private readonly string folder = Path.Combine(Path.GetTempPath(),"radar-observation-"+Guid.NewGuid());
    private readonly FoundationStore store;
    public ObservationTests() { store = new(folder); store.Initialize(Now); }
    private static ObservationSample Sample(DateTimeOffset at) => new(at,true,true,0,0,0,0,1000,1000,"Idle");

    [Fact]
    public void FullDayRequiresHeartbeatsAndDoesNotMeanHealthOrProfitValidated()
    {
        var run = Guid.NewGuid(); store.BeginObservation(run,Now);
        for (var minute = 0; minute <= 1440; minute++)
            store.RecordObservation(run,Sample(Now.AddMinutes(minute)) with { CashReconciled = minute != 10 });
        var report = store.Observation(Now.AddDays(1));
        Assert.True(report.DurationTargetReached);
        Assert.Equal(86400,report.ContinuousSeconds);
        Assert.Equal(86400,report.Run!.ObservedSeconds);
        Assert.Equal(1,report.ProblemSamples24h);
        Assert.Equal(1441,report.Samples24h);
        Assert.Empty(report.LedgerActions24h);
        Assert.False(store.Observation(Now.AddDays(1).AddSeconds(91)).DurationTargetReached);
    }

    [Fact]
    public void SleepGapIsNotCountedAsContinuousOrObservedTime()
    {
        var run = Guid.NewGuid(); store.BeginObservation(run,Now);
        store.RecordObservation(run,Sample(Now));
        store.RecordObservation(run,Sample(Now.AddMinutes(1)));
        store.RecordObservation(run,Sample(Now.AddHours(25)));
        var report = store.Observation(Now.AddHours(25));
        Assert.False(report.DurationTargetReached);
        Assert.Equal(0,report.ContinuousSeconds);
        Assert.Equal(60,report.Run!.ObservedSeconds);
        Assert.Equal(1,report.Run.Interruptions);
        store.RecordObservation(run,Sample(Now.AddHours(25).AddMinutes(1)));
        Assert.Equal(60,store.Observation(Now.AddHours(25).AddMinutes(1)).ContinuousSeconds);
    }

    [Fact]
    public void RestartRetainsEvidenceButStartsANewContinuityWindow()
    {
        var run = Guid.NewGuid(); store.BeginObservation(run,Now);
        store.RecordObservation(run,Sample(Now.AddMinutes(1)),true);
        Assert.False(store.Observation(Now.AddMinutes(1)).HeartbeatFresh);
        Assert.Throws<InvalidOperationException>(() => store.RecordObservation(run,Sample(Now.AddMinutes(2))));
        var reopened = new FoundationStore(folder); reopened.Initialize(Now.AddHours(2));
        var next = Guid.NewGuid(); reopened.BeginObservation(next,Now.AddHours(2));
        reopened.RecordObservation(next,Sample(Now.AddHours(2)));
        var report = reopened.Observation(Now.AddHours(2));
        Assert.Equal(next,report.Run!.RunId);
        Assert.Equal(0,report.ContinuousSeconds);
        Assert.Equal(2,report.RecentRuns.Count);
        Assert.True(report.RecentRuns[1].Stopped);
        Assert.Equal(2,report.Samples24h);
    }

    [Fact]
    public void FutureClockAndBackwardSampleDoNotFabricateUptime()
    {
        var run = Guid.NewGuid(); store.BeginObservation(run,Now);
        store.RecordObservation(run,Sample(Now.AddMinutes(1)));
        Assert.False(store.Observation(Now).HeartbeatFresh);
        Assert.Throws<ArgumentException>(() => store.RecordObservation(run,Sample(Now)));
    }

    [Fact]
    public void SignalCountsUseWholeWindowRatherThanTruncatedHistoryAndExcludeFuture()
    {
        var asset = new AssetIdentity(Guid.NewGuid(),"TEST","Test","TESTUSDT",null,null,[],Now);
        store.Register(asset);
        var raw = new RawEvidence(Guid.NewGuid(),"Binance","*","Universe",Now,200,CollectionFailure.None,"{}");
        store.SaveMarketContext(new("test",Now,MarketState.Bull,1,1,1,1,1,[]),
            [new(raw,[],Universe:[new("TESTUSDT","TEST",Now)])]);
        for (var index = 0; index < 601; index++)
            store.SaveEntry(new("test",asset.AssetId,EntryStrategy.TrendPullback,TradeSide.Long,
                Now.AddSeconds(-index),"NoSetup",false,null,["ParticipationInsufficient"],Now,Now,null));
        store.SaveEntry(new("test",asset.AssetId,EntryStrategy.TrendPullback,TradeSide.Short,
            Now.AddDays(-2),"Blocked",false,null,["OldReason"],Now,Now,null));
        store.SaveEntry(new("test",asset.AssetId,EntryStrategy.BreakoutRetest,TradeSide.Short,
            Now.AddMinutes(1),"Blocked",false,null,["FutureReason"],Now,Now,null));
        var report = store.Observation(Now);
        Assert.Equal(601,Assert.Single(report.EvaluationStatuses24h).Count);
        Assert.Equal("ParticipationInsufficient",Assert.Single(report.Reasons24h).Name);
        Assert.Equal(1,report.Coverage.EligibleContracts);
        Assert.Equal(1,report.Coverage.FreshEvaluatedContracts);
        Assert.Equal(0,report.Coverage.ConfirmedPatterns);
        Assert.Equal(0,store.Observation(Now.AddMinutes(5)).Coverage.FreshEvaluatedContracts);
    }

    [Fact]
    public void MarketWarmupAndStaleHoldingAreRecordedAsProblemSamples()
    {
        var run = Guid.NewGuid(); store.BeginObservation(run,Now);
        store.RecordObservation(run,Sample(Now) with { MarketContextFresh = false });
        store.RecordObservation(run,Sample(Now.AddMinutes(1)) with { StalePositions = 1 });
        store.RecordObservation(run,Sample(Now.AddMinutes(2)));
        store.RecordObservation(run,Sample(Now.AddMinutes(3)) with { HttpResponsive = false });
        var report = store.Observation(Now.AddMinutes(3));
        Assert.Equal(3,report.ProblemSamples24h);
        Assert.Equal(4,report.Samples24h);
    }

    public void Dispose()
    {
        foreach (var file in Directory.GetFiles(folder)) File.Delete(file);
        Directory.Delete(folder);
    }
}
