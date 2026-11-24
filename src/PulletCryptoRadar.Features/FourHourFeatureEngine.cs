using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Features;

public sealed class FourHourFeatureEngine
{
    public IReadOnlyList<Observation> Calculate(Guid assetId, IReadOnlyList<HourCandle> input, DateTimeOffset asOf)
    {
        if (input.Any(candle => candle.AssetId != assetId || candle.ReceivedAt > asOf || candle.ClosedAt > asOf ||
                candle.ClosedAt - candle.OpenAt != TimeSpan.FromHours(1) ||
                candle.OpenAt.ToUnixTimeMilliseconds() % 3600000 != 0 ||
                candle.Low <= 0 || candle.High < candle.Low || candle.Open < candle.Low || candle.Open > candle.High ||
                candle.Close < candle.Low || candle.Close > candle.High || candle.QuoteVolume < 0))
            throw new ArgumentException("Invalid 4h inputs.");
        var groups = input.GroupBy(candle => candle.OpenAt.ToUnixTimeMilliseconds() / 14400000)
            .OrderBy(group => group.Key);
        var complete = new List<(decimal Close, DateTimeOffset ClosedAt)>();
        foreach (var group in groups)
        {
            var hours = group.OrderBy(candle => candle.OpenAt).ToArray();
            if (hours.Length != 4 || hours[0].OpenAt.ToUnixTimeMilliseconds() % 14400000 != 0) continue;
            for (var i=1; i<4; i++)
                if (hours[i].OpenAt != hours[i-1].ClosedAt) throw new ArgumentException("4h candle gap.");
            if (complete.Count > 0 && hours[0].OpenAt != complete[^1].ClosedAt)
                throw new ArgumentException("Missing 4h candle.");
            complete.Add((hours[^1].Close, hours[^1].ClosedAt));
        }
        if (complete.Count < 61) return [];
        decimal Ema(int period)
        {
            var value = complete.Take(period).Average(candle => candle.Close);
            for (var i=period; i<complete.Count; i++) value += 2m/(period+1)*(complete[i].Close-value);
            return value;
        }
        Observation Value(Metric metric, decimal value) => new(assetId, metric, value, "Binance",
            "UTC-4H-from-closed-H1-2.0.0", complete[^1].ClosedAt, input.Max(candle => candle.ReceivedAt),
            ObservationStatus.Valid, TimeQuality.ProviderTime);
        return [Value(Metric.Ema20H4,Ema(20)),Value(Metric.Ema60H4,Ema(60)),Value(Metric.CloseH4,complete[^1].Close)];
    }
}
