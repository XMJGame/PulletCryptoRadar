using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Features;

public sealed class PrioritySelection
{
    public IReadOnlyList<PriorityCandidate> Select(IReadOnlyList<MarketInstrument> universe,
        IReadOnlyList<MarketTicker> tickers, DateTimeOffset now)
    {
        var symbols = universe.Select(item => item.Symbol).ToHashSet(StringComparer.Ordinal);
        var valid = tickers.Where(item => symbols.Contains(item.Symbol) && item.Symbol is not ("BTCUSDT" or "ETHUSDT") &&
                item.SourceTime <= now && now-item.SourceTime <= TimeSpan.FromSeconds(90) &&
                item.QuoteVolume >= 5_000_000 && item.Return24h >= -100)
            .GroupBy(item => item.Symbol).Select(group => group.OrderByDescending(item => item.SourceTime).First()).ToArray();
        var rising = valid.Where(item => item.Return24h >= 5).OrderByDescending(item => item.Return24h)
            .ThenBy(item => item.Symbol,StringComparer.Ordinal).Take(2).ToArray();
        var falling = valid.Where(item => item.Return24h <= -5).OrderBy(item => item.Return24h)
            .ThenBy(item => item.Symbol,StringComparer.Ordinal).Take(2).ToArray();
        return rising.Concat(falling).Select(item => new PriorityCandidate(item.Symbol,item.Return24h,item.QuoteVolume,
            item.SourceTime,item.Return24h > 0 ? "LiquidRisingMover" : "LiquidFallingMover")).ToArray();
    }
}
