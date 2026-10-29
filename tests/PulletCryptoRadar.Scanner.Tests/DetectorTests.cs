using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Scanner.Tests;

public sealed class DetectorTests
{
    private readonly VolatilityOptions _options = new();
    [Fact]
    public void PriceOnlyRiseIsNotCandidate()
    {
        var result = new UpMoveDetector(_options).Detect(Samples.Market(20, 30, 9, 0), Samples.Now);
        Assert.False(result.IsCandidate); Assert.DoesNotContain("Volume", result.ConfirmedFamilies);
    }
    [Theory]
    [InlineData(-1, "Preheat")] [InlineData(0.2, "Ignition")]
    public void UpStageRequiresIndependentVolumeAndStructure(decimal distance, string expected)
    {
        var result = new UpMoveDetector(_options).Detect(Samples.Market(highDistance: distance), Samples.Now);
        Assert.True(result.IsCandidate); Assert.Equal(expected, result.Stage);
        Assert.InRange(result.Score!.Value, 0, 100); Assert.InRange(result.DataConfidence, 0, 60);
    }
    [Fact]
    public void FourHourStructureConfirmsBreakout()
    {
        var m = Samples.Market(); m.Features.DistanceToHigh[240] = Samples.Value(0.3m);
        var result = new UpMoveDetector(_options).Detect(m, Samples.Now);
        Assert.Equal("Breakout", result.Stage); Assert.Equal(3, result.ConfirmedFamilies.Count);
    }
    [Fact]
    public void OverheatedIsExplicitlyLabeled()
    {
        var result = new UpMoveDetector(_options).Detect(Samples.Market(r15: 40), Samples.Now);
        Assert.Equal("Overheated", result.Stage); Assert.True(result.IsCandidate);
    }
    [Fact]
    public void MissingStructureDoesNotRenormalizeScoreUpward()
    {
        var m = Samples.Market(); var missing = m with { Features = m.Features with { DistanceToHigh = [] } };
        var detector = new UpMoveDetector(_options);
        Assert.Equal(detector.Detect(m, Samples.Now).Score, detector.Detect(missing, Samples.Now).Score);
        Assert.Contains("Structure", detector.Detect(missing, Samples.Now).MissingFamilies);
        Assert.True(detector.Detect(missing, Samples.Now).DataConfidence < detector.Detect(m, Samples.Now).DataConfidence);
    }
    [Fact]
    public void MissingVolumeProducesUnknownScoreNotZero()
    {
        var m = Samples.Market(); m = m with { Features = m.Features with { VolumeRobustZ = FeatureValue<double>.Missing() } };
        var result = new UpMoveDetector(_options).Detect(m, Samples.Now);
        Assert.Null(result.Score); Assert.False(result.IsCandidate); Assert.Equal("Unknown", result.Stage);
    }
    [Theory] [InlineData(-31)] [InlineData(10)]
    public void StaleOrFutureTickerCannotGenerateCandidate(int seconds)
    {
        var m = Samples.Market() with { PriceSourceTime = Samples.Now.AddSeconds(seconds) };
        foreach (var result in new[] { new UpMoveDetector(_options).Detect(m, Samples.Now), new DownMoveDetector(_options).Detect(m, Samples.Now) })
        { Assert.False(result.IsCandidate); Assert.Null(result.Score); Assert.Equal(0, result.DataConfidence); }
    }
    [Fact]
    public void ValidFlagAloneDoesNotMakeOldFeaturesFresh()
    {
        var m = Samples.Market(); m.Features.Returns[5] = Samples.Value(3m, Samples.Now.AddMinutes(-1));
        m.Features.Returns[15] = Samples.Value(5m, Samples.Now.AddMinutes(-1));
        Assert.Null(new UpMoveDetector(_options).Detect(m, Samples.Now).Score);
    }
    [Fact]
    public void OldVolumeIsNotUsedEvenIfMarkedValid()
    {
        var m = Samples.Market(); m = m with { Features = m.Features with { VolumeRobustZ = Samples.Value(8d, Samples.Now.AddMinutes(-20)) } };
        Assert.False(new UpMoveDetector(_options).Detect(m, Samples.Now).IsCandidate);
    }
    [Fact]
    public void NewlyListedAssetsHaveReducedConfidence()
    {
        var m = Samples.Market(); m = m with { Features = m.Features with { NewListing = true } };
        Assert.InRange(new UpMoveDetector(_options).Detect(m, Samples.Now).DataConfidence, 0, 45);
    }
    [Fact]
    public void DownDetectorHasDifferentSensitivityAndDoesNotInventShortBuild()
    {
        var m = Samples.Market(-2, -2, -2, 1.7, lowDistance: -0.3m);
        var down = new DownMoveDetector(_options).Detect(m, Samples.Now);
        Assert.True(down.IsCandidate); Assert.Equal("Breakdown", down.Stage);
        Assert.Contains("Liquidation", down.MissingFamilies); Assert.Contains("OpenInterest", down.MissingFamilies);
        var mirrorUp = new UpMoveDetector(_options).Detect(Samples.Market(2, 2, 2, 1.7, highDistance: 0.3m), Samples.Now);
        Assert.False(mirrorUp.IsCandidate);
    }
    [Theory] [InlineData(-6, -10, "Selloff")] [InlineData(-10, -16, "Crash")]
    public void DownsideStageTracksShockSize(decimal r5, decimal r15, string stage)
    {
        var result = new DownMoveDetector(_options).Detect(Samples.Market(r5, r15, -5, 3, lowDistance: -1), Samples.Now);
        Assert.True(result.IsCandidate); Assert.Equal(stage, result.Stage);
    }
    [Fact]
    public void DownsideShockCanQualifyWithUndefinedZButDisclosesIt()
    {
        var m = Samples.Market(-6, -10); m = m with { Features = m.Features with { PriceRobustZ5m = FeatureValue<double>.Missing() } };
        var result = new DownMoveDetector(_options).Detect(m, Samples.Now);
        Assert.True(result.IsCandidate); Assert.Contains(result.Reasons, r => r.Contains("Unknown"));
    }
    [Fact]
    public void DefaultOptionsValidAndInvalidThresholdsRejected()
    {
        Assert.True(_options.IsValid()); _options.EpisodeCloseScore = 80; Assert.False(_options.IsValid());
    }
}
