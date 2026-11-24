using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Features;

public sealed class MarketStateEngine
{
    public MarketContext Calculate(IReadOnlyList<MarketInstrument> universe, DateTimeOffset universeReceived,
        IReadOnlyList<MarketTicker> tickers, FeatureSnapshot? btc, FeatureSnapshot? eth, DateTimeOffset now)
    {
        var eligible = universe.Select(item => item.Symbol).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var fresh = tickers.Where(item => eligible.Contains(item.Symbol) && item.SourceTime <= now &&
            now-item.SourceTime <= TimeSpan.FromSeconds(90)).GroupBy(item => item.Symbol)
            .Select(group => group.OrderByDescending(item => item.SourceTime).First()).ToArray();
        var coverage = eligible.Count > 0 ? (decimal)fresh.Length/eligible.Count : 0;
        var reasons = new List<string>();
        if (universeReceived > now || now-universeReceived > TimeSpan.FromHours(8)) reasons.Add("UniverseStale");
        if (eligible.Count < 30 || fresh.Length < 30 || coverage < .8m) reasons.Add("InsufficientMarketCoverage");
        int? Trend(FeatureSnapshot? snapshot)
        {
            if (snapshot is null || snapshot.AsOf > now || now-snapshot.AsOf > TimeSpan.FromMinutes(10)) return null;
            decimal? Value(Metric metric)
            {
                var field = snapshot.Metrics.SingleOrDefault(item => item.Metric == metric);
                if (field is not { UsableForScoring: true, Provider: "Binance" } ||
                    field.Evidence.Any(item => item.SourceTime > now || item.ReceivedTime > now) ||
                    !field.Evidence.Any(item => item.SourceTime is { } source && now-source <= TimeSpan.FromHours(4.25))) return null;
                return field.Value;
            }
            var fast = Value(Metric.Ema20H4); var slow = Value(Metric.Ema60H4); var close = Value(Metric.CloseH4);
            if (fast is null || slow is null || close is null) return null;
            return fast > slow && close > fast ? 1 : fast < slow && close < fast ? -1 : 0;
        }
        var bitcoin = Trend(btc); var ethereum = Trend(eth);
        if (bitcoin is null || ethereum is null) reasons.Add("BtcEth4hUnavailable");
        var returns = fresh.Select(item=>item.Return24h).Order().ToArray();
        decimal? median = returns.Length == 0 ? null :
            (returns[(returns.Length-1)/2]+returns[returns.Length/2])/2;
        decimal? breadth = fresh.Length == 0 ? null : (decimal)fresh.Count(item=>item.Return24h>0)/fresh.Length;
        var state = MarketState.Unknown;
        if (reasons.Count == 0)
        {
            // A versioned initial regime hypothesis based on 24h breadth and closed BTC/ETH 4h trend.
            state = bitcoin == 1 && ethereum == 1 && breadth >= .75m ? MarketState.StrongBull
                : bitcoin == 1 && ethereum == 1 && breadth >= .55m ? MarketState.Bull
                : bitcoin == -1 && ethereum == -1 && breadth <= .25m ? MarketState.StrongBear
                : bitcoin == -1 && ethereum == -1 && breadth <= .45m ? MarketState.Bear
                : MarketState.Mixed;
        }
        return new("MarketContext-Breadth24h-Trend4h-2.0.0",now,state,eligible.Count,fresh.Length,coverage,breadth,median,reasons);
    }
}
