using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Scanner.Tests;

public sealed class DerivativeDetectorTests
{
    private static MarketView Market(bool down = false)
    {
        var m = Samples.Market(down ? -5 : 5, down ? -6 : 6, down ? -5 : 5, 5, .3m, -.3m);
        return m with { FundingRate = -.0001m, FundingSourceTime = Samples.Now, Features = m.Features with { Derivatives = new()
        {
            OiChanges = new() { [15] = Samples.Value(down ? -9m : 6m) }, OiRobustZ = Samples.Value(down ? -4d : 4d),
            TakerBuyShare = Samples.Value(down ? .3m : .7m), TopPositionChange15m = Samples.Value(3m),
            TopAccountLongShare = Samples.Value(.6m), GlobalLongShare = Samples.Value(.6m),
            ObservedLongLiquidation15mUsd = Samples.Value(150000m)
        } } };
    }
    [Fact]
    public void RealDerivativeEvidenceUpgradesVersionAndConfidenceWithoutInventingMarketCapRisk()
    {
        var r = new UpMoveDetector(new()).Detect(Market(), Samples.Now);
        Assert.Equal(DerivativeMoveDetector.UpVersion, r.StrategyVersion); Assert.True(r.IsCandidate);
        Assert.True(r.DataConfidence >= 70); Assert.Equal(SignalRisk.Unknown, r.Risk);
        Assert.Contains("OpenInterest", r.ConfirmedFamilies); Assert.Contains("Flow", r.ConfirmedFamilies);
        Assert.Contains("MarketCap", r.MissingFamilies); Assert.Equal(100, r.Components.Sum(c => c.Weight));
        Assert.Equal("Breakout", r.Stage);
    }
    [Fact]
    public void CrowdedLongsVetoUpwardNotification()
    {
        var r = new UpMoveDetector(new()).Detect(Market() with { FundingRate = .002m }, Samples.Now);
        Assert.Equal(SignalRisk.High, r.Risk); Assert.Equal("Overheated", r.Stage);
    }
    [Fact]
    public void OneMissingCoreDerivativeProducesUnknownScoreRatherThanRenormalization()
    {
        var m = Market(); m = m with { Features = m.Features with { Derivatives = m.Features.Derivatives with { TakerBuyShare = FeatureValue<decimal>.Missing() } } };
        var r = new UpMoveDetector(new()).Detect(m, Samples.Now);
        Assert.Null(r.Score); Assert.False(r.IsCandidate); Assert.Contains("Flow", r.MissingFamilies);
    }
    [Fact]
    public void DownCrashUsesObservedDeleveragingAndNeverClaimsShortBuildWithoutOi()
    {
        var r = new DownMoveDetector(new()).Detect(Market(true), Samples.Now);
        Assert.Equal(DerivativeMoveDetector.DownVersion, r.StrategyVersion); Assert.Equal("Crash", r.Stage);
        Assert.Contains("Liquidation", r.ConfirmedFamilies); Assert.Equal(100, r.Components.Sum(c => c.Weight));
    }
    [Fact]
    public void NewListingsAndStaleTickerCannotBypassSafetyUsingDeepData()
    {
        var m = Market(); m = m with { Features = m.Features with { NewListing = true } };
        Assert.InRange(new UpMoveDetector(new()).Detect(m, Samples.Now).DataConfidence, 0, 60);
        var stale = new UpMoveDetector(new()).Detect(m with { PriceSourceTime = Samples.Now.AddMinutes(-1) }, Samples.Now);
        Assert.Null(stale.Score); Assert.False(stale.IsCandidate); Assert.Equal(0, stale.DataConfidence);
    }
}
