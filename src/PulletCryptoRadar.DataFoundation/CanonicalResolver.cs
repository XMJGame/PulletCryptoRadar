using PulletCryptoRadar.Core;
using PulletCryptoRadar.Features;

namespace PulletCryptoRadar.DataFoundation;

public sealed class CanonicalResolver
{
    public ResolvedMetric Resolve(Guid assetId, Metric metric, IEnumerable<Observation> input,
        DateTimeOffset asOf, TimeSpan maximumAge, string primary, string? secondary = null,
        bool compare = false, decimal relativeTolerance = .15m)
    {
        if (maximumAge <= TimeSpan.Zero || relativeTolerance < 0 || string.IsNullOrWhiteSpace(primary))
            throw new ArgumentException("Invalid resolution policy.");
        if ((OpenInterestFeatureEngine.Metrics.Contains(metric) || metric is Metric.Price or Metric.Volume24h or Metric.OpenInterestUsd or Metric.Funding8h or
            Metric.MarkPrice or Metric.FundingRaw or Metric.OpenInterestQuantity or Metric.Ema20H1 or Metric.Ema60H1 or
            Metric.Atr14H1 or Metric.Return24hClosed or Metric.VolumeRatioH1 or Metric.Ema20H4 or Metric.Ema60H4 or Metric.CloseH4 or
            Metric.FundingIntervalHours or Metric.TakerBuyShareH1) &&
            (primary != "Binance" || secondary is not null))
            throw new ArgumentException("Binance trading series require Binance-native evidence.");
        var evidence = input.Where(item => item.AssetId == assetId && item.Metric == metric &&
            item.ReceivedTime <= asOf).ToArray();
        bool DomainValid(decimal? value) => metric == Metric.TakerBuyShareH1 ? value is >= 0 and <= 1 :
            metric == Metric.FundingIntervalHours ? value is >= 1 and <= 24 && value == decimal.Truncate(value.Value) :
            OpenInterestFeatureEngine.Metrics.Contains(metric) || metric is Metric.FundingRaw or Metric.Funding8h or Metric.Return24hClosed
            ? value is not null : metric is Metric.Volume24h or Metric.OpenInterestQuantity or Metric.OpenInterestUsd or
                Metric.Gini or Metric.Hhi or Metric.Nakamoto or Metric.Top10 or Metric.TotalClusterPercent or Metric.Atr14H1 or Metric.VolumeRatioH1
                ? value is >= 0 : value is > 0;
        bool Valid(Observation item) => item.Status == ObservationStatus.Valid && DomainValid(item.Value) &&
            item.TimeQuality is TimeQuality.ProviderTime or TimeQuality.BlockTime &&
            item.SourceTime is { } source && source <= item.ReceivedTime && source <= asOf &&
            asOf - source <= maximumAge;
        Observation? Latest(string provider) => evidence.Where(item => item.Provider == provider)
            .OrderByDescending(item => item.ReceivedTime).FirstOrDefault();
        var first = Latest(primary);
        var second = secondary is null ? null : Latest(secondary);
        var validFirst = first is not null && Valid(first);
        var validSecond = second is not null && Valid(second);
        if (compare && validFirst && validSecond)
        {
            var difference = first!.Value == 0
                ? second!.Value == 0 ? 0 : decimal.MaxValue
                : Math.Abs(second!.Value!.Value - first.Value!.Value) / Math.Abs(first.Value.Value);
            if (difference > relativeTolerance)
                return new(metric, ResolutionStatus.Conflict, null, null, false, evidence);
            return new(metric, ResolutionStatus.Confirmed, first.Value, first.Provider, true, evidence);
        }
        var chosen = validFirst ? first : validSecond ? second : null;
        if (chosen is not null)
            return new(metric, ResolutionStatus.SingleSource, chosen.Value, chosen.Provider, true, evidence);
        // Recently received research evidence remains inspectable without inventing its original observation time.
        if ((primary != "Binance" || metric is Metric.FundingIntervalHours or Metric.Funding8h) &&
            first is { Status: ObservationStatus.Valid } && DomainValid(first.Value) &&
            first.TimeQuality is TimeQuality.HttpDate or TimeQuality.ReceivedOnly &&
            asOf - first.ReceivedTime <= maximumAge &&
            (first.SourceTime is null || first.SourceTime <= first.ReceivedTime))
            return new(metric, ResolutionStatus.UnverifiedTime, first.Value, first.Provider, false, evidence);
        var status = evidence.Any(item => item.Status == ObservationStatus.Valid && DomainValid(item.Value))
            ? ResolutionStatus.Stale : ResolutionStatus.Missing;
        return new(metric, status, null, null, false, evidence);
    }

    public static decimal? Change(Observation previous, Observation current)
    {
        if (previous.AssetId != current.AssetId || previous.Metric != current.Metric ||
            previous.Provider != current.Provider || previous.Methodology != current.Methodology ||
            previous.Chain != current.Chain || previous.Contract != current.Contract ||
            previous.Status != ObservationStatus.Valid || current.Status != ObservationStatus.Valid ||
            previous.Value is not > 0 || current.Value is null ||
            previous.SourceTime is null || current.SourceTime is null ||
            previous.SourceTime >= current.SourceTime ||
            previous.TimeQuality is not (TimeQuality.ProviderTime or TimeQuality.BlockTime) ||
            current.TimeQuality is not (TimeQuality.ProviderTime or TimeQuality.BlockTime))
            return null;
        return (current.Value - previous.Value) / previous.Value * 100;
    }
}
