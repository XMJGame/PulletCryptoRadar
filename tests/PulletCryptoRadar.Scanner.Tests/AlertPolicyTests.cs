using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Scanner.Tests;

public sealed class AlertPolicyTests
{
    private readonly DateTimeOffset _now = Samples.Now;
    private static MoveAssessment Assessment(double score = 88, int confidence = 75, MoveDirection direction = MoveDirection.Up,
        string stage = "Ignition", SignalRisk risk = SignalRisk.Unknown)
        => Samples.Assessment(direction, stage, score) with { DataConfidence = confidence, Risk = risk };
    private RadarSnapshot Scan(params MoveAssessment[] assessments) => ScanAt(_now, assessments);
    private static RadarSnapshot ScanAt(DateTimeOffset now, MoveAssessment[] assessments, MarketRegime? regime = MarketRegime.Normal)
    {
        var observations = assessments.Select((a, i) => new RadarObservation(Samples.Market(id: i + 1, symbol: $"ASSET{i + 1}USDT") with { PriceSourceTime = now },
            a.Direction == MoveDirection.Up ? a : Samples.Assessment(candidate: false, score: 0),
            a.Direction == MoveDirection.Down ? a : Samples.Assessment(MoveDirection.Down, candidate: false, score: 0), i + 1)).ToArray();
        return new(Guid.NewGuid(), now, new(now, regime, regime is null ? DataStatus.Missing : DataStatus.Valid, 40, 40, 30, 10, 10, 0, 2, 0, 7, 8, 9, "Test"), observations,
            assessments.Select((a, i) => new MoveEpisode { Id = i + 1, AssetId = i + 1, Direction = a.Direction, State = EpisodeState.Active,
                DataStatus = DataStatus.Valid, Stage = a.Stage, StrategyVersion = a.StrategyVersion }).ToArray(), [], "{}");
    }
    private static AlertOptions Options() => new() { CompetitionDelaySeconds = 0 };
    private static NotificationRecord Record(DateTimeOffset time, long episodeId = 1, int assetId = 1, double score = 82,
        NotificationKind kind = NotificationKind.Normal, string stage = "Ignition")
        => new(Guid.NewGuid(), Guid.NewGuid().ToString(), kind, episodeId, assetId, "TESTUSDT", MoveDirection.Up, SignalGrade.A,
            score, stage, time, time.AddMinutes(2), NotificationStatus.Submitted, "Test", "Body");
    [Theory]
    [InlineData(69, 100, "C")] [InlineData(70, 60, "B")] [InlineData(95, 60, "B")]
    [InlineData(82, 70, "A")] [InlineData(91, 80, "A")]
    public void GradesRespectConfidenceThreshold(double score, int confidence, string grade)
        => Assert.Equal(grade, SignalGrader.Grade(Assessment(score, confidence)).ToString());
    [Fact]
    public void SRequiresFourDistinctFamiliesAndExplicitLowRisk()
    {
        var a = Assessment(94, 85) with { ConfirmedFamilies = ["Price", "Volume", "Structure", "OpenInterest"] };
        Assert.Equal(SignalGrade.A, SignalGrader.Grade(a));
        Assert.Equal(SignalGrade.S, SignalGrader.Grade(a with { Risk = SignalRisk.Low }));
        Assert.Equal(SignalGrade.A, SignalGrader.Grade(a with { Risk = SignalRisk.Low, ConfirmedFamilies = ["Price", "Price", "Volume", "Structure"] }));
    }
    [Fact]
    public void AliasesDoNotDoubleCountOpenInterest()
        => Assert.Equal(2, SignalGrader.FamilyCount(Assessment() with { ConfirmedFamilies = ["Price", "OI", "OpenInterest", "NotAFamily"] }));
    [Theory] [InlineData(double.NaN)] [InlineData(101)] [InlineData(-1)]
    public void InvalidScoreIsUnknown(double score) => Assert.Equal(SignalGrade.Unknown, SignalGrader.Grade(Assessment(score)));
    [Fact]
    public void DownsideAThresholdIs75()
        => Assert.Equal(SignalGrade.A, SignalGrader.Grade(Assessment(75, 70, MoveDirection.Down, "Breakdown")));
    [Fact]
    public void Phase3HighScoresNeverNotify()
    {
        var result = new AlertPolicyEngine(Options()).Evaluate(Scan(Assessment(99, 60)), new([]), [], _now);
        Assert.Empty(result.Notifications); Assert.Contains(result.Decisions, d => d.Reason.Contains("置信度不足"));
    }
    [Fact]
    public void TopNSelectsHighestScoresAcrossCurrentCandidates()
    {
        var result = new AlertPolicyEngine(Options()).Evaluate(Scan(Assessment(88), Assessment(94), Assessment(91), Assessment(84)), new([]), [], _now);
        Assert.Equal(new double?[] { 94, 91 }, result.Notifications.Select(n => n.Score));
        Assert.Equal(2, result.Decisions.Count(d => d.Eligible));
    }
    [Fact]
    public void CompetitionDelayWaitsAndRechecksCurrentEvidence()
    {
        var engine = new AlertPolicyEngine(new());
        var first = engine.Evaluate(Scan(Assessment()), new([]), [], _now); Assert.Empty(first.Notifications);
        var nextTime = _now.AddMinutes(1);
        var next = engine.Evaluate(ScanAt(nextTime, [Assessment()]), first.State, [], nextTime);
        Assert.Single(next.Notifications);
        var deteriorated = engine.Evaluate(ScanAt(nextTime, [Assessment(confidence: 60)]), first.State, [], nextTime);
        Assert.Empty(deteriorated.Notifications);
    }
    [Fact]
    public void DailyBudgetSurvivesHistoryAndFailedSendsStillReserveBudget()
    {
        var engine = new AlertPolicyEngine(Options());
        var history = Enumerable.Range(10, 5).Select(i => Record(_now.AddHours(-1), i, i) with { Status = NotificationStatus.Failed }).ToArray();
        Assert.Empty(engine.Evaluate(Scan(Assessment()), new([]), history, _now).Notifications);
        Assert.Equal(5, engine.DailyUsed(history, _now));
    }
    [Fact]
    public void DayResetUsesShanghaiRatherThanUtc()
    {
        var engine = new AlertPolicyEngine(Options()); var beforeMidnight = new DateTimeOffset(2026, 10, 29, 15, 59, 0, TimeSpan.Zero);
        var records = new[] { Record(beforeMidnight) };
        Assert.Equal(1, engine.DailyUsed(records, beforeMidnight));
        Assert.Equal(0, engine.DailyUsed(records, beforeMidnight.AddMinutes(2)));
        Assert.Equal("2026-10-30", engine.BudgetDate(beforeMidnight.AddMinutes(2)));
    }
    [Fact]
    public void RollingWindowLimitsNewAssetsAndReleasesAtBoundary()
    {
        var engine = new AlertPolicyEngine(Options()); var history = new[] { Record(_now.AddMinutes(-1), 10, 10), Record(_now.AddMinutes(-29), 11, 11) };
        Assert.Empty(engine.Evaluate(Scan(Assessment()), new([]), history, _now).Notifications);
        history[1] = history[1] with { CreatedAt = _now.AddMinutes(-30) };
        Assert.Single(engine.Evaluate(Scan(Assessment()), new([]), history, _now).Notifications);
    }
    [Fact]
    public void EpisodeAllowsOnlyMeaningfulSecondThenStops()
    {
        var engine = new AlertPolicyEngine(Options()); var prior = Record(_now.AddMinutes(-1));
        Assert.Empty(engine.Evaluate(Scan(Assessment(88)), new([]), [prior], _now).Notifications);
        var escalated = engine.Evaluate(Scan(Assessment(94)), new([]), [prior], _now);
        Assert.Single(escalated.Notifications);
        Assert.Empty(engine.Evaluate(Scan(Assessment(99)), new([]), [prior, escalated.Notifications[0]], _now).Notifications);
    }
    [Fact]
    public void AccelerationAllowsStageUpgradeButOverheatedNeverNewOpportunity()
    {
        var engine = new AlertPolicyEngine(Options()); var prior = Record(_now.AddMinutes(-1), score: 88);
        Assert.Single(engine.Evaluate(Scan(Assessment(88, stage: "Acceleration")), new([]), [prior], _now).Notifications);
        Assert.Empty(engine.Evaluate(Scan(Assessment(99, stage: "Overheated")), new([]), [], _now).Notifications);
    }
    [Fact]
    public void RiskGateVetoesUpAndDoesNotHideConfirmedDownRisk()
    {
        var engine = new AlertPolicyEngine(Options());
        Assert.Empty(engine.Evaluate(Scan(Assessment(risk: SignalRisk.High)), new([]), [], _now).Notifications);
        Assert.Single(engine.Evaluate(Scan(Assessment(direction: MoveDirection.Down, stage: "Crash", risk: SignalRisk.Critical)), new([]), [], _now).Notifications);
    }
    [Fact]
    public void MarketEventExemptsBudgetAndSuppressesSingleCoinAlerts()
    {
        var engine = new AlertPolicyEngine(Options());
        var history = Enumerable.Range(10, 5).Select(i => Record(_now.AddHours(-1), i, i)).ToArray();
        var scan = ScanAt(_now, [Assessment(direction: MoveDirection.Down, stage: "Crash")], MarketRegime.SystemicCrash);
        var result = engine.Evaluate(scan, new([]), history, _now);
        Assert.Equal(NotificationKind.Systemic, Assert.Single(result.Notifications).Kind);
        Assert.Equal(5, engine.DailyUsed(history.Concat(result.Notifications).ToArray(), _now));
        Assert.Empty(engine.Evaluate(scan, result.State, history.Concat(result.Notifications).ToArray(), _now).Notifications);
    }
    [Fact]
    public void UnknownMarketDoesNotResetSystemicDedup()
    {
        var engine = new AlertPolicyEngine(Options()); var first = engine.Evaluate(ScanAt(_now, [], MarketRegime.SystemicCrash), new([]), [], _now);
        var unknown = engine.Evaluate(ScanAt(_now.AddMinutes(1), [], null), first.State, first.Notifications, _now.AddMinutes(1));
        var again = engine.Evaluate(ScanAt(_now.AddMinutes(2), [], MarketRegime.SystemicCrash), unknown.State, first.Notifications, _now.AddMinutes(2));
        Assert.Empty(again.Notifications);
    }
    [Fact]
    public void MarketRecoveryStartsNewEventWhenCrashRecurs()
    {
        var engine = new AlertPolicyEngine(Options()); var first = engine.Evaluate(ScanAt(_now, [], MarketRegime.SystemicCrash), new([]), [], _now);
        var normal = engine.Evaluate(ScanAt(_now.AddMinutes(1), [], MarketRegime.Normal), first.State, first.Notifications, _now.AddMinutes(1));
        var again = engine.Evaluate(ScanAt(_now.AddMinutes(121), [], MarketRegime.SystemicCrash), normal.State, first.Notifications, _now.AddMinutes(121));
        Assert.Single(again.Notifications); Assert.NotEqual(first.Notifications[0].DedupKey, again.Notifications[0].DedupKey);
    }
    [Fact]
    public void StaleSnapshotOrPriceCannotNotify()
    {
        var engine = new AlertPolicyEngine(Options());
        Assert.Empty(engine.Evaluate(Scan(Assessment()), new([]), [], _now.AddMinutes(3)).Notifications);
        var scan = Scan(Assessment()); scan = scan with { Observations = [scan.Observations[0] with { Input = scan.Observations[0].Input with { PriceSourceTime = _now.AddMinutes(-1) } }] };
        Assert.Empty(engine.Evaluate(scan, new([]), [], _now).Notifications);
    }
    [Fact]
    public void OptionsRejectInvalidTimeZonesAndUnboundedBudget()
    {
        Assert.True(new AlertOptions().IsValid());
        Assert.False(new AlertOptions { BudgetTimeZone = "Invalid/Test" }.IsValid());
        Assert.False(new AlertOptions { DailyNormalLimit = -1 }.IsValid());
    }
    [Fact]
    public void VerifiedWatchCriticalBypassesBudgetAndMarketSuppressionButNotDataGates()
    {
        var options = Options(); options.DailyNormalLimit = 0; options.MaxNewAssetsPerWindow = 0;
        var engine = new AlertPolicyEngine(options);
        var critical = Assessment(direction: MoveDirection.Down, stage: "Crash", risk: SignalRisk.Critical) with { IsWatchListCritical = true };
        var scan = ScanAt(_now, [critical], MarketRegime.SystemicCrash);
        var result = engine.Evaluate(scan, new([]), [], _now);
        Assert.Contains(result.Notifications, n => n.Kind == NotificationKind.WatchListCritical);
        Assert.Equal(0, engine.DailyUsed(result.Notifications, _now));
        var missing = engine.Evaluate(Scan(critical with { DataConfidence = 60 }), new([]), [], _now);
        Assert.Empty(missing.Notifications);
    }
    [Fact]
    public void BriefNormalCrashFlappingIsDeduplicatedByRegimeCooldown()
    {
        var engine = new AlertPolicyEngine(Options()); var first = engine.Evaluate(ScanAt(_now, [], MarketRegime.SystemicCrash), new([]), [], _now);
        var normal = engine.Evaluate(ScanAt(_now.AddMinutes(1), [], MarketRegime.Normal), first.State, first.Notifications, _now.AddMinutes(1));
        var again = engine.Evaluate(ScanAt(_now.AddMinutes(2), [], MarketRegime.SystemicCrash), normal.State, first.Notifications, _now.AddMinutes(2));
        Assert.Empty(again.Notifications);
    }
}
