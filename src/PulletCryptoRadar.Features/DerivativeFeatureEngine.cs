using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Features;

public sealed class DerivativeFeatureEngine
{
    public IReadOnlyList<Observation> Calculate(Guid assetId, IEnumerable<Observation> evidence, DateTimeOffset asOf)
    {
        var inputs = evidence.Where(item => item.AssetId == assetId && item.Provider == "Binance" &&
            item.ReceivedTime <= asOf).ToArray();
        Observation? Latest(Metric metric) => inputs.Where(item => item.Metric == metric)
            .OrderByDescending(item => item.ReceivedTime).FirstOrDefault();
        bool Fresh(Observation? item) => item is { Status: ObservationStatus.Valid, Value: not null,
            TimeQuality: TimeQuality.ProviderTime, SourceTime: not null } && item.SourceTime <= item.ReceivedTime &&
            asOf - item.SourceTime <= TimeSpan.FromSeconds(90);
        var result = new List<Observation>();
        var quantity = Latest(Metric.OpenInterestQuantity);
        var mark = Latest(Metric.MarkPrice);
        if (Fresh(quantity) && Fresh(mark) && quantity!.Value >= 0 && mark!.Value > 0 &&
            Math.Abs((quantity.SourceTime!.Value - mark.SourceTime!.Value).TotalSeconds) <= 10 &&
            quantity.Methodology == "USDT-perpetual-base-quantity" && mark.Methodology == "USDT-perpetual-mark")
            result.Add(new(assetId, Metric.OpenInterestUsd, quantity.Value * mark.Value, "Binance",
                "USDT-perpetual-base-quantity-times-mark;max-skew=10s", quantity.SourceTime < mark.SourceTime ? quantity.SourceTime : mark.SourceTime,
                quantity.ReceivedTime > mark.ReceivedTime ? quantity.ReceivedTime : mark.ReceivedTime,
                ObservationStatus.Valid, TimeQuality.ProviderTime));
        else result.Add(new(assetId, Metric.OpenInterestUsd, null, "Binance", "USDT-perpetual-base-quantity-times-mark;max-skew=10s",
            null, asOf, ObservationStatus.Missing, TimeQuality.Unknown));

        var funding = Latest(Metric.FundingRaw);
        var interval = Latest(Metric.FundingIntervalHours);
        var intervalValid = interval is { Status: ObservationStatus.Valid, Value: >= 1 and <= 24,
            TimeQuality: TimeQuality.ReceivedOnly, SourceTime: null } && interval.Value == decimal.Truncate(interval.Value.Value) &&
            asOf - interval.ReceivedTime < TimeSpan.FromMinutes(5) &&
            interval.Methodology is "fundingInfo-adjusted-hours" or "fundingInfo-absent-default-8h";
        if (Fresh(funding) && funding!.Methodology == "USDT-perpetual-raw-current-interval" && intervalValid)
            // Linear comparison equivalent, not a compounded yield or a settled funding payment.
            result.Add(new(assetId, Metric.Funding8h, funding.Value * 8 / interval!.Value, "Binance",
                "Current-rate-linear-8h-equivalent;interval-source-time-unverified", null, asOf,
                ObservationStatus.Valid, TimeQuality.ReceivedOnly));
        else result.Add(new(assetId, Metric.Funding8h, null, "Binance", "Current-rate-linear-8h-equivalent",
            null, asOf, ObservationStatus.Missing, TimeQuality.Unknown));
        return result;
    }
}
