using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Core.Tests;

public sealed class CapitalFeatureTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 4, 0, 0, TimeSpan.Zero);
    private static MarketView Market() => new(new(1, "1000TESTUSDT", "1000TEST", Now.AddDays(-30), true), 10, 2000, null, null, null,
        Now, Now, null, 100, 100, 100, new() { Derivatives = new() { OpenInterestUsd = new(1000, DataStatus.Valid, Now, "Fixture USD") } });
    private static CapitalInput Input() => new(new(1, "test", MappingStatus.ManualVerified, ["test"], 1000, null, null, 1, Now, "Manual"),
        new("test", Now.AddMinutes(-20), Now.AddMinutes(-19), 10000, 20000, 5000, 1000, 2000, null, 30), true);
    private static CapitalFeatures Calculate(CapitalInput? input = null, MarketView? market = null) => new CapitalFeatureEngine().Calculate(market ?? Market(), input ?? Input(), Now);
    [Fact]
    public void VerifiedRatiosUseUsdValuesWithoutApplyingContractMultiplierAgain()
    {
        var f = Calculate(); Assert.True(f.TrustedIdentity); Assert.Equal(.1m, f.OiMarketCapRatio.Value); Assert.Equal(.2m, f.VolumeMarketCapRatio.Value);
        Assert.Equal(Now.AddMinutes(-20), f.OiMarketCapRatio.SourceTime); Assert.Null(f.MaxSupply.Value);
    }
    [Theory] [InlineData(MappingStatus.SymbolMatched)] [InlineData(MappingStatus.Ambiguous)] [InlineData(MappingStatus.Unknown)]
    public void UntrustedIdentityCannotProduceRatiosEvenWithFreshMarketCap(MappingStatus status)
    {
        var i = Input(); var f = Calculate(i with { Mapping = i.Mapping! with { Status = status } });
        Assert.Equal(10000, f.MarketCapUsd.Value); Assert.False(f.TrustedIdentity); Assert.Null(f.OiMarketCapRatio.Value); Assert.Null(f.VolumeMarketCapRatio.Value);
    }
    [Fact]
    public void AbsentCatalogIdentityAndMismatchedSnapshotCannotBeUsed()
    {
        var i = Input(); Assert.Null(Calculate(i with { CatalogContainsId = false }).OiMarketCapRatio.Value);
        var mismatch = Calculate(i with { Snapshot = i.Snapshot! with { CoinGeckoId = "other" } });
        Assert.Null(mismatch.MarketCapUsd.Value); Assert.Null(mismatch.OiMarketCapRatio.Value);
    }
    [Theory] [InlineData(-61)] [InlineData(1)]
    public void OldAndFutureCapitalSamplesCannotGenerateRatios(int minutes)
    {
        var i = Input(); var f = Calculate(i with { Snapshot = i.Snapshot! with { SourceTime = Now.AddMinutes(minutes) } });
        Assert.NotEqual(DataStatus.Valid, f.MarketCapUsd.Status); Assert.Null(f.OiMarketCapRatio.Value);
    }
    [Fact]
    public void StaleNumeratorsAndZeroCapRemainUnknown()
    {
        var m = Market(); m = m with { PriceSourceTime = Now.AddSeconds(-31), Features = m.Features with { Derivatives = m.Features.Derivatives with { OpenInterestUsd = new(1000, DataStatus.Valid, Now.AddMinutes(-12), "Fixture") } } };
        var f = Calculate(market: m); Assert.Null(f.OiMarketCapRatio.Value); Assert.Null(f.VolumeMarketCapRatio.Value);
        var i = Input(); Assert.Null(Calculate(i with { Snapshot = i.Snapshot! with { MarketCapUsd = 0 } }).OiMarketCapRatio.Value);
    }
    [Fact]
    public void OverflowIsInvalidRatherThanCrashingTheFeatureLoop()
    {
        var i = Input(); var f = Calculate(i with { Snapshot = i.Snapshot! with { MarketCapUsd = .0000000000000000000000000001m } });
        Assert.Equal(DataStatus.Invalid, f.OiMarketCapRatio.Status); Assert.Null(f.OiMarketCapRatio.Value);
    }
}
