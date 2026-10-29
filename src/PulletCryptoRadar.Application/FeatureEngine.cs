using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Application;

public sealed class FeatureEngine
{
    public AssetFeatures Calculate(MarketView market, IReadOnlyList<Candle> minutes,
        IReadOnlyList<Candle> fiveMinutes, IReadOnlyList<Candle> fifteenMinutes, DateTimeOffset now)
    {
        var returns = new Dictionary<int, FeatureValue<decimal>>();
        foreach (var horizon in new[] { 1, 3, 5, 15, 30, 60, 240, 1440, 4320 })
        {
            var bars = horizon <= 240 ? minutes : horizon <= 1440 ? fiveMinutes : fifteenMinutes;
            returns[horizon] = Return(market.Price, market.PriceSourceTime, bars, horizon, now);
        }
        var (p5, b5, t5) = ReturnBaseline(fiveMinutes);
        var (p15, b15, t15) = ReturnBaseline(fifteenMinutes);
        var lastVolume = fiveMinutes.LastOrDefault();
        var volumeBaseline = fiveMinutes.SkipLast(1).Select(x => (double)x.QuoteVolume).ToArray();
        var distances = new Dictionary<int, FeatureValue<decimal>>();
        var lows = new Dictionary<int, FeatureValue<decimal>>();
        foreach (var horizon in new[] { 60, 240, 1440, 10080 })
        {
            var bars = horizon <= 240 ? minutes : horizon <= 1440 ? fiveMinutes : fifteenMinutes;
            var interval = bars.Count == 0 ? 1 : bars[0].IntervalMinutes;
            var end = market.PriceSourceTime is null ? (DateTimeOffset?)null : DateTimeOffset.FromUnixTimeSeconds(
                market.PriceSourceTime.Value.ToUnixTimeSeconds() / (interval * 60) * (interval * 60));
            // Compare with preceding completed candles, excluding the most recent bar.
            var selected = end is null ? [] : bars.Where(x => x.EndTime < end && x.EndTime >= end.Value.AddMinutes(-horizon)).ToArray();
            var required = bars.Count == 0 ? int.MaxValue : horizon / bars[0].IntervalMinutes;
            var complete = selected.Length == required && Continuous(selected) && bars.LastOrDefault()?.EndTime == end;
            var high = complete ? selected.Max(x => x.High) : (decimal?)null;
            var low = complete ? selected.Min(x => x.Low) : (decimal?)null;
            distances[horizon] = DecimalFeature(high > 0 && market.Price is not null ? (market.Price.Value / high.Value - 1) * 100 : null,
                market.PriceSourceTime, now, 30, "PriorCandleHigh");
            lows[horizon] = DecimalFeature(low > 0 && market.Price is not null ? (market.Price.Value / low.Value - 1) * 100 : null,
                market.PriceSourceTime, now, 30, "PriorCandleLow");
        }
        return new AssetFeatures
        {
            Returns = returns, DistanceToHigh = distances, DistanceToLow = lows,
            PriceRobustZ5m = Statistic(p5, b5, t5, now, 660, false),
            PriceRobustZ15m = Statistic(p15, b15, t15, now, 1860, false),
            PricePercentile = Statistic(p5, b5, t5, now, 660, true),
            VolumeRobustZ = Statistic(lastVolume is null ? null : (double)lastVolume.QuoteVolume, volumeBaseline, lastVolume?.SourceTime, now, 660, false),
            VolumePercentile = Statistic(lastVolume is null ? null : (double)lastVolume.QuoteVolume, volumeBaseline, lastVolume?.SourceTime, now, 660, true),
            BaselineSamples = b5.Length,
            NewListing = now - market.Instrument.OnboardTime < TimeSpan.FromDays(7)
        };
    }

    public static FeatureValue<decimal> Return(decimal? price, DateTimeOffset? sourceTime, IReadOnlyList<Candle> bars, int horizonMinutes, DateTimeOffset now)
    {
        if (price is null || price <= 0 || sourceTime is null || bars.Count == 0) return FeatureValue<decimal>.Missing();
        // Anchor to source time, not wall time, so stale prices never shift historical reference points.
        var interval = bars[0].IntervalMinutes;
        var anchor = DateTimeOffset.FromUnixTimeSeconds(sourceTime.Value.ToUnixTimeSeconds() / (interval * 60) * (interval * 60));
        var target = anchor.AddMinutes(-horizonMinutes);
        var reference = bars.LastOrDefault(x => x.EndTime == target);
        if (reference is null) return FeatureValue<decimal>.Missing();
        var required = horizonMinutes / interval;
        var span = bars.Where(x => x.EndTime > target && x.EndTime <= anchor).ToArray();
        if (span.Length != required || !Continuous(span)) return FeatureValue<decimal>.Missing();
        return DecimalFeature((price.Value / reference.Close - 1) * 100, sourceTime, now, 30, "CompletedCandles+Ticker");
    }

    public static bool Continuous(IReadOnlyList<Candle> bars)
        => bars.Zip(bars.Skip(1)).All(x => x.First.EndTime == x.Second.OpenTime);

    private static (double? Current, double[] Baseline, DateTimeOffset? Time) ReturnBaseline(IReadOnlyList<Candle> bars)
    {
        if (bars.Count < 2) return (null, [], null);
        var changes = bars.Zip(bars.Skip(1)).Where(x => x.First.EndTime == x.Second.OpenTime)
            .Select(x => (Value: (double)((x.Second.Close / x.First.Close - 1) * 100), Bar: x.Second)).ToArray();
        if (changes.Length == 0 || changes[^1].Bar.OpenTime != bars[^1].OpenTime) return (null, [], null);
        return (changes[^1].Value, changes.SkipLast(1).Select(x => x.Value).ToArray(), changes[^1].Bar.SourceTime);
    }
    private static FeatureValue<double> Statistic(double? current, double[] baseline, DateTimeOffset? time,
        DateTimeOffset now, int staleSeconds, bool percentile)
    {
        if (current is null) return FeatureValue<double>.Missing();
        var result = percentile ? RobustStatistics.Percentile(current.Value, baseline) : RobustStatistics.RobustZ(current.Value, baseline);
        return new(result, result is null ? DataStatus.Missing : now - time > TimeSpan.FromSeconds(staleSeconds) ? DataStatus.Stale : DataStatus.Valid, time, "HistoricalClosedCandles");
    }
    private static FeatureValue<decimal> DecimalFeature(decimal? value, DateTimeOffset? time, DateTimeOffset now, int staleSeconds, string source)
        => new(value, value is null ? DataStatus.Missing : now - time > TimeSpan.FromSeconds(staleSeconds) ? DataStatus.Stale : DataStatus.Valid, time, source);
}
