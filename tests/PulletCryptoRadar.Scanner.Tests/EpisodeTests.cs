using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Scanner.Tests;

public sealed class EpisodeTests
{
    private readonly EpisodeEngine _engine = new(new());
    private EpisodePlan First() => _engine.Advance([], [Samples.Observation()], Guid.NewGuid(), Samples.Now);
    [Fact]
    public void SameMoveRetainsIdentityAcrossStageEscalation()
    {
        var first = First(); var initial = Assert.Single(first.Active);
        var next = _engine.Advance(first.Tracking, [Samples.Observation(Samples.Assessment(stage: "Acceleration", score: 94), price: 120)], Guid.NewGuid(), Samples.Now.AddMinutes(1));
        var episode = Assert.Single(next.Active); Assert.Equal(initial.Id, episode.Id); Assert.Equal(120, episode.PeakPrice);
        Assert.Equal(EpisodeState.Escalating, episode.State); Assert.Equal("StageChanged", Assert.Single(next.Events).Kind);
    }
    [Fact]
    public void UnchangedScanDoesNotEmitDuplicateEvents()
    {
        var first = First(); var second = _engine.Advance(first.Tracking, [Samples.Observation()], Guid.NewGuid(), Samples.Now.AddMinutes(1));
        Assert.Empty(second.Events); Assert.Equal(Assert.Single(first.Active).Id, Assert.Single(second.Active).Id);
    }
    [Fact]
    public void ScoreChangesAccumulateAgainstLastRecordedScore()
    {
        var first = First(); var second = _engine.Advance(first.Tracking, [Samples.Observation(Samples.Assessment(score: 85))], Guid.NewGuid(), Samples.Now.AddMinutes(1));
        Assert.Empty(second.Events);
        var third = _engine.Advance(second.Tracking, [Samples.Observation(Samples.Assessment(score: 88))], Guid.NewGuid(), Samples.Now.AddMinutes(2));
        Assert.Equal("ScoreChanged", Assert.Single(third.Events).Kind);
    }
    [Fact]
    public void MissingDataPausesRatherThanClosesEpisode()
    {
        var first = First(); var missing = Samples.Observation(Samples.Assessment(score: null, candidate: false, status: DataStatus.Missing), price: 999);
        var second = _engine.Advance(first.Tracking, [missing], Guid.NewGuid(), Samples.Now.AddHours(5));
        var episode = Assert.Single(second.Active); Assert.Null(episode.EndTime); Assert.Equal(105, episode.PeakPrice);
        Assert.Equal(DataStatus.Missing, episode.DataStatus); Assert.Empty(second.Events);
    }
    [Fact]
    public void ConfirmedReversalClosesOldAndCreatesOppositeEpisode()
    {
        var first = First(); var observation = Samples.Observation(Samples.Assessment(score: 0, candidate: false), Samples.Assessment(MoveDirection.Down, "Crash", 99), 80);
        var next = _engine.Advance(first.Tracking, [observation], Guid.NewGuid(), Samples.Now.AddMinutes(1));
        Assert.Equal(MoveDirection.Down, Assert.Single(next.Active).Direction); Assert.Equal(2, next.Events.Count);
        Assert.Equal("DirectionReversed", next.Changed.First().EndReason);
        Assert.NotEqual(first.Active[0].Id, next.Active[0].Id);
    }
    [Fact]
    public void LowScoreClosesAndMildWeakeningCools()
    {
        var first = First(); var cooling = _engine.Advance(first.Tracking, [Samples.Observation(Samples.Assessment(score: 42, candidate: false))], Guid.NewGuid(), Samples.Now.AddMinutes(1));
        Assert.Equal(EpisodeState.Cooling, Assert.Single(cooling.Active).State);
        var closed = _engine.Advance(cooling.Tracking, [Samples.Observation(Samples.Assessment(score: 30, candidate: false))], Guid.NewGuid(), Samples.Now.AddMinutes(2));
        Assert.Empty(closed.Active); Assert.Equal("ScoreBelowThreshold", Assert.Single(closed.Changed).EndReason);
    }
    [Fact]
    public void QuietTimeoutDoesNotRepeatedlyRecreateSameEpisode()
    {
        var first = First(); var e = first.Active[0] with { LastObservedTime = Samples.Now.AddMinutes(119) };
        var closed = _engine.Advance([e], [Samples.Observation()], Guid.NewGuid(), Samples.Now.AddMinutes(121));
        Assert.Empty(closed.Active); Assert.Equal("NoNewExtreme", Assert.Single(closed.Changed).EndReason);
        var again = _engine.Advance(closed.Tracking, [Samples.Observation()], Guid.NewGuid(), Samples.Now.AddMinutes(122));
        Assert.Empty(again.Active); Assert.Empty(again.Events);
        var extreme = _engine.Advance(again.Tracking, [Samples.Observation(price: 106)], Guid.NewGuid(), Samples.Now.AddMinutes(123));
        Assert.Single(extreme.Active); Assert.NotEqual(e.Id, extreme.Active[0].Id);
    }
    [Fact]
    public void LongUnobservedGapStartsNewEpisodeInsteadOfJoiningUnseenMove()
    {
        var first = First(); var next = _engine.Advance(first.Tracking, [Samples.Observation(price: 120)], Guid.NewGuid(), Samples.Now.AddHours(3));
        Assert.Equal("DataGap", next.Changed[0].EndReason); Assert.NotEqual(first.Active[0].Id, Assert.Single(next.Active).Id);
    }
    [Fact]
    public void DelistedAssetClosesAndRestoredIdRemainsUnique()
    {
        var first = First(); var next = _engine.Advance(first.Tracking, [], Guid.NewGuid(), Samples.Now.AddMinutes(1));
        Assert.Equal("AssetInactive", Assert.Single(next.Changed).EndReason);
        _engine.RestoreId(first.Active[0].Id + 1000);
        var restarted = First(); Assert.True(restarted.Active[0].Id > first.Active[0].Id + 1000);
    }
    [Fact]
    public void RestoringClosedLowScoreEpisodeDoesNotBlockNewMove()
    {
        var first = First();
        var closed = first.Active[0] with { State = EpisodeState.Closed, EndReason = "ScoreBelowThreshold", EndTime = Samples.Now };
        var next = _engine.Advance([closed], [Samples.Observation(price: 103)], Guid.NewGuid(), Samples.Now.AddMinutes(1));
        Assert.Single(next.Active); Assert.NotEqual(closed.Id, next.Active[0].Id);
    }
    [Fact]
    public void ConfirmedOppositeRiskIsNotBlockedByMissingOldDirectionAfterQuietTimeout()
    {
        var closed = First().Active[0] with { State = EpisodeState.Closed, EndReason = "NoNewExtreme", EndTime = Samples.Now };
        var observation = Samples.Observation(Samples.Assessment(score: null, candidate: false, status: DataStatus.Missing),
            Samples.Assessment(MoveDirection.Down, "Crash", 95), 80);
        var next = _engine.Advance([closed], [observation], Guid.NewGuid(), Samples.Now.AddMinutes(1));
        Assert.Equal(MoveDirection.Down, Assert.Single(next.Active).Direction);
    }
    [Fact]
    public void QuietGuardResetIsPersistableSoRestartDoesNotSuppressNextMove()
    {
        var closed = First().Active[0] with { State = EpisodeState.Closed, EndReason = "NoNewExtreme", EndTime = Samples.Now };
        var reset = _engine.Advance([closed], [Samples.Observation(Samples.Assessment(score: 20, candidate: false))], Guid.NewGuid(), Samples.Now.AddMinutes(1));
        Assert.Equal("QuietPeriodReset", Assert.Single(reset.Changed).EndReason);
        var next = _engine.Advance(reset.Changed, [Samples.Observation(price: 103)], Guid.NewGuid(), Samples.Now.AddMinutes(2));
        Assert.Single(next.Active);
    }
}
