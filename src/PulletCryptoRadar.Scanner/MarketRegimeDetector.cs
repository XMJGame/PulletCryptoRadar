using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Scanner;

public sealed class MarketRegimeDetector(VolatilityOptions options)
{
    public MarketRegimeSnapshot Detect(IReadOnlyList<MarketView> assets, DateTimeOffset now)
    {
        var valid = assets.Where(m => Evidence.PriceFresh(m, now, options))
            .Select(m => (m.Instrument.Symbol, Return: Evidence.Return(m, 60, now, options)))
            .Where(x => x.Return is not null).Select(x => (x.Symbol, Return: x.Return!.Value)).ToArray();
        var ordered = valid.Select(x => x.Return).Order().ToArray();
        decimal? median = ordered.Length == 0 ? null : ordered.Length % 2 == 1 ? ordered[ordered.Length / 2]
            : ordered[ordered.Length / 2 - 1] / 2 + ordered[ordered.Length / 2] / 2;
        var up = valid.Count(x => x.Return > 0); var down = valid.Count(x => x.Return < 0);
        var coverage = assets.Count == 0 ? 0 : (double)valid.Length / assets.Count;
        MarketRegime? regime = null;
        var reason = $"有效 1h 样本 {valid.Length}/{assets.Count}，市场状态等待足够数据。";
        if (valid.Length >= options.MinimumMarketAssets && coverage >= options.MinimumMarketCoverage)
        {
            var upRatio = (double)up / valid.Length; var downRatio = (double)down / valid.Length;
            regime = upRatio >= options.SystemicBreadth && median >= options.SystemicMedianPercent ? MarketRegime.SystemicPump
                : downRatio >= options.SystemicBreadth && median <= -options.SystemicMedianPercent ? MarketRegime.SystemicCrash
                : upRatio >= options.BroadBreadth && median >= options.BroadMedianPercent ? MarketRegime.BroadRiskOn
                : downRatio >= options.BroadBreadth && median <= -options.BroadMedianPercent ? MarketRegime.BroadRiskOff : MarketRegime.Normal;
            reason = $"1h 上涨 {up}/{valid.Length}，下跌 {down}/{valid.Length}，中位收益 {median:0.00}%；按市场广度与幅度共同识别。";
        }
        return new(now, regime, regime is null ? DataStatus.Missing : DataStatus.Valid, assets.Count, valid.Length,
            up, down, valid.Count(x => x.Return > 5), valid.Count(x => x.Return < -5), valid.Count(x => x.Return > 10),
            valid.Count(x => x.Return < -10), median, Find("BTCUSDT"), Find("ETHUSDT"), reason);

        decimal? Find(string symbol) => valid.Where(x => x.Symbol == symbol).Select(x => (decimal?)x.Return).FirstOrDefault();
    }
}
