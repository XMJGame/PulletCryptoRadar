using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Scanner.Tests;

public sealed class RegimeTests
{
    [Theory]
    [InlineData(0.1, "Normal")] [InlineData(2, "BroadRiskOn")] [InlineData(-2, "BroadRiskOff")]
    [InlineData(7, "SystemicPump")] [InlineData(-7, "SystemicCrash")]
    public void BreadthAndMedianJointlyDetermineRegime(decimal value, string expected)
    {
        var assets = Enumerable.Range(1, 40).Select(id => Samples.Market(r15: value, id: id)).ToArray();
        var result = new MarketRegimeDetector(new()).Detect(assets, Samples.Now);
        Assert.Equal(expected, result.Regime.ToString()); Assert.Equal(100, result.CoveragePercent);
    }
    [Fact]
    public void LowCoverageIsUnknownInsteadOfNormal()
    {
        var assets = Enumerable.Range(1, 40).Select(id => id < 10 ? Samples.Market(id: id) : Samples.Market(id: id) with { PriceSourceTime = Samples.Now.AddHours(-1) }).ToArray();
        var result = new MarketRegimeDetector(new()).Detect(assets, Samples.Now);
        Assert.Null(result.Regime); Assert.Equal(DataStatus.Missing, result.Status); Assert.Equal(9, result.ValidAssets);
    }
    [Fact]
    public void TooFewAssetsIsUnknownEvenAtFullCoverage()
        => Assert.Null(new MarketRegimeDetector(new()).Detect([Samples.Market()], Samples.Now).Regime);
    [Fact]
    public void CountsAndBenchmarksUseSameValidOneHourReturns()
    {
        var assets = Enumerable.Range(1, 40).Select(id => Samples.Market(r15: id % 2 == 0 ? 12 : -6, id: id,
            symbol: id == 1 ? "BTCUSDT" : id == 2 ? "ETHUSDT" : $"TEST{id}USDT")).ToArray();
        var result = new MarketRegimeDetector(new()).Detect(assets, Samples.Now);
        Assert.Equal(20, result.Above10); Assert.Equal(20, result.BelowMinus5); Assert.Equal(-6, result.BtcReturn1h);
        Assert.Equal(12, result.EthReturn1h); Assert.Equal(3, result.MedianReturn1h);
    }
}
