using PulletCryptoRadar.Application;

namespace PulletCryptoRadar.Core.Tests;

public sealed class StatisticsTests
{
    [Theory]
    [InlineData(new double[] { 1, 2, 3 }, 2)]
    [InlineData(new double[] { 4, 1, 2, 3 }, 2.5)]
    [InlineData(new double[] { 1, 2, 3, 4, 1000000 }, 3)]
    public void MedianIsResistantToOutliers(double[] values, double expected) => Assert.Equal(expected, RobustStatistics.Median(values));
    [Fact] public void MadUsesMedianAbsoluteDeviations() => Assert.Equal(1, RobustStatistics.Mad([1, 2, 3, 4, 1000000]));
    [Fact] public void RobustZMatchesFormula() => Assert.Equal(2 / (1.4826 * 1), RobustStatistics.RobustZ(5, new double[] { 1, 2, 3, 4, 1000000 }, 5)!.Value, 10);
    [Fact] public void FlatBaselineIsUnknown() => Assert.Null(RobustStatistics.RobustZ(10, Enumerable.Repeat(1d, 40).ToArray()));
    [Fact] public void InsufficientSamplesAreUnknown() => Assert.Null(RobustStatistics.RobustZ(5, [1, 2, 3]));
    [Fact] public void EmptyMedianIsUnknown() => Assert.Null(RobustStatistics.Median([]));
    [Theory]
    [InlineData(0, 0)] [InlineData(1, 12.5)] [InlineData(2, 37.5)] [InlineData(3, 62.5)] [InlineData(4, 87.5)] [InlineData(5, 100)]
    public void PercentileUsesMidrank(double current, double expected)
        => Assert.Equal(expected, RobustStatistics.Percentile(current, [1, 2, 3, 4], 4));
    [Fact] public void TiesHaveFiftyPercentile() => Assert.Equal(50, RobustStatistics.Percentile(1, Enumerable.Repeat(1d, 40).ToArray()));
    [Fact] public void NonFiniteInputIsUnknown() => Assert.Null(RobustStatistics.RobustZ(double.NaN, Enumerable.Range(1, 40).Select(x => (double)x).ToArray()));
}
