namespace PulletCryptoRadar.Application;

public static class RobustStatistics
{
    public static double? Median(IEnumerable<double> values)
    {
        var a = values.Where(double.IsFinite).Order().ToArray();
        return a.Length == 0 ? null : a.Length % 2 == 1 ? a[a.Length / 2] : a[a.Length / 2 - 1] / 2 + a[a.Length / 2] / 2;
    }
    public static double? Mad(IEnumerable<double> values)
    {
        var a = values.Where(double.IsFinite).ToArray();
        var median = Median(a);
        return median is null ? null : Median(a.Select(x => Math.Abs(x - median.Value)));
    }
    // A flat baseline has no defined Z-score; never divide by zero or invent a huge score.
    public static double? RobustZ(double current, IReadOnlyList<double> baseline, int minSamples = 30)
    {
        if (!double.IsFinite(current) || baseline.Count < minSamples) return null;
        var median = Median(baseline); var mad = Mad(baseline);
        return median is null || mad is null || mad <= 1e-12 ? null : (current - median.Value) / (1.4826 * mad.Value);
    }
    // Mid-rank empirical percentile: ties split evenly; current value excluded from baseline.
    public static double? Percentile(double current, IReadOnlyList<double> baseline, int minSamples = 30)
        => !double.IsFinite(current) || baseline.Count < minSamples ? null
            : 100.0 * (baseline.Count(x => x < current) + 0.5 * baseline.Count(x => x == current)) / baseline.Count;
}
