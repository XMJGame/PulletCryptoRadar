using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Features;

public sealed class OpenInterestFeatureEngine
{
    public static readonly Metric[] Metrics = [Metric.OiQuantityChange1h, Metric.OiQuantityChange4h,
        Metric.OiQuantityChange24h, Metric.OiNotionalChange1h, Metric.OiNotionalChange4h, Metric.OiNotionalChange24h];

    public IReadOnlyList<Observation> Calculate(Guid assetId, IReadOnlyList<OpenInterestPoint> points, DateTimeOffset asOf)
    {
        if (points.Any(point => point.AssetId != assetId || point.ReceivedTime > asOf || point.SourceTime > point.ReceivedTime ||
            point.SourceTime.ToUnixTimeMilliseconds() % 300000 != 0 || point.Quantity < 0 || point.NotionalUsdt < 0))
            throw new ArgumentException("Invalid OI feature evidence.");
        for (var i = 1; i < points.Count; i++)
            if (points[i].SourceTime <= points[i-1].SourceTime)
                throw new ArgumentException("OI history must be strictly ordered.");
        var latest = points.LastOrDefault();
        var result = new List<Observation>();
        foreach (var (hours, quantityMetric, notionalMetric) in new[] {
            (1,Metric.OiQuantityChange1h,Metric.OiNotionalChange1h),
            (4,Metric.OiQuantityChange4h,Metric.OiNotionalChange4h),
            (24,Metric.OiQuantityChange24h,Metric.OiNotionalChange24h) })
        {
            var window = latest is null ? [] : points.Where(point => point.SourceTime >= latest.SourceTime.AddHours(-hours)).ToArray();
            var valid = latest is not null && asOf-latest.SourceTime <= TimeSpan.FromMinutes(10) &&
                window.Length == hours * 12 + 1 && window[0].SourceTime == latest.SourceTime.AddHours(-hours) &&
                window.Zip(window.Skip(1), (a,b) => b.SourceTime-a.SourceTime == TimeSpan.FromMinutes(5)).All(value => value);
            decimal? Change(bool quantity)
            {
                if (!valid) return null;
                var start = quantity ? window[0].Quantity : window[0].NotionalUsdt;
                var end = quantity ? latest!.Quantity : latest!.NotionalUsdt;
                return start > 0 ? (end / start - 1) * 100 : null;
            }
            foreach (var (metric,value) in new[] { (quantityMetric,Change(true)), (notionalMetric,Change(false)) })
                result.Add(new(assetId,metric,value,"Binance", $"OI-history-5m-contiguous-{hours}h-percent-2.0.0",
                    latest?.SourceTime, points.Count > 0 ? points.Max(point => point.ReceivedTime) : asOf,
                    value is null ? ObservationStatus.Missing : ObservationStatus.Valid,TimeQuality.ProviderTime));
        }
        return result;
    }
}
