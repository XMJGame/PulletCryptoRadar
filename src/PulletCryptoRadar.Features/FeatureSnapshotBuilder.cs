using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Features;

public sealed class FeatureSnapshotBuilder
{
    public FeatureSnapshot Build(Guid assetId, DateTimeOffset asOf, IReadOnlyList<ResolvedMetric> metrics,
        MarketContext? marketContext = null)
    {
        if (metrics.GroupBy(item => item.Metric).Any(group => group.Count() > 1))
            throw new ArgumentException("Duplicate canonical metric.");
        if (metrics.SelectMany(metric => metric.Evidence).Any(item =>
                item.AssetId != assetId || item.ReceivedTime > asOf || item.SourceTime > asOf))
            throw new ArgumentException("Feature snapshot contains foreign or future evidence.");
        if (metrics.Any(item => item.UsableForScoring &&
                (item.Value is null || item.Status is not (ResolutionStatus.Confirmed or ResolutionStatus.SingleSource))))
            throw new ArgumentException("Invalid scoring eligibility.");
        if (metrics.Any(item => item.Evidence.Any(observation => observation.Metric != item.Metric)))
            throw new ArgumentException("Evidence metric does not match its canonical metric.");
        var state = marketContext is not null && marketContext.AsOf <= asOf &&
            asOf - marketContext.AsOf <= TimeSpan.FromMinutes(3) ? marketContext.State : MarketState.Unknown;
        return new("FeatureSnapshot-2.0.0", assetId, asOf, state, metrics.ToArray());
    }
}
