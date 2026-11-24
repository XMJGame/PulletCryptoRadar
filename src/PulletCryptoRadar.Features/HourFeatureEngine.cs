using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Features;

public sealed class HourFeatureEngine
{
    public IReadOnlyList<Observation> Calculate(Guid assetId, IReadOnlyList<HourCandle> candles, DateTimeOffset asOf)
    {
        if (candles.Count < 61) return [];
        if (candles.Any(candle => candle.AssetId != assetId || candle.ReceivedAt > asOf || candle.ClosedAt > asOf ||
                candle.ClosedAt - candle.OpenAt != TimeSpan.FromHours(1) || candle.Low <= 0 ||
                candle.High < candle.Low || candle.Open < candle.Low || candle.Open > candle.High ||
                candle.Close < candle.Low || candle.Close > candle.High || candle.QuoteVolume < 0 ||
                candle.TakerBuyQuoteVolume < 0 || candle.TakerBuyQuoteVolume > candle.QuoteVolume))
            throw new ArgumentException("Invalid hourly feature evidence.");
        for (var i = 1; i < candles.Count; i++)
            if (candles[i].OpenAt != candles[i-1].ClosedAt)
                throw new ArgumentException("Hourly feature history has gaps.");
        decimal Ema(int period)
        {
            var value = candles.Take(period).Average(candle => candle.Close);
            var alpha = 2m / (period + 1);
            for (var i = period; i < candles.Count; i++)
                value += alpha * (candles[i].Close - value);
            return value;
        }
        var ranges = candles.Select((candle, index) => index == 0 ? candle.High - candle.Low :
            Math.Max(candle.High - candle.Low, Math.Max(Math.Abs(candle.High - candles[index-1].Close),
                Math.Abs(candle.Low - candles[index-1].Close)))).ToArray();
        var atr = ranges.Take(14).Average();
        for (var i = 14; i < ranges.Length; i++) atr = (atr * 13 + ranges[i]) / 14;
        var last = candles[^1];
        var previousVolume = candles.Skip(candles.Count - 21).Take(20).Average(candle => candle.QuoteVolume);
        Observation Feature(Metric metric, decimal? value) =>
            new(assetId, metric, value, "Binance", "Closed-H1-Features-2.0.0", last.ClosedAt,
                candles.Max(candle => candle.ReceivedAt), value is null ? ObservationStatus.Missing : ObservationStatus.Valid,
                TimeQuality.ProviderTime);
        return [Feature(Metric.Ema20H1, Ema(20)), Feature(Metric.Ema60H1, Ema(60)), Feature(Metric.Atr14H1, atr),
            Feature(Metric.Return24hClosed, (last.Close / candles[^25].Close - 1) * 100),
            Feature(Metric.VolumeRatioH1, previousVolume > 0 ? last.QuoteVolume / previousVolume : null),
            Feature(Metric.TakerBuyShareH1, last.QuoteVolume > 0 ? last.TakerBuyQuoteVolume / last.QuoteVolume : null)];
    }
}
