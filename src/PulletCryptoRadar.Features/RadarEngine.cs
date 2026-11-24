using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Features;

public sealed class RadarEngine
{
    internal const decimal LowCapResearchLimit = 300_000_000;

    public RadarAssessment Calculate(AssetIdentity asset, FeatureSnapshot? snapshot, DateTimeOffset now)
    {
        var missing = new List<string>();
        var reasons = new List<string>();
        var evidence = new List<ResolvedMetric>();
        decimal? Value(Metric metric, TimeSpan maximumAge)
        {
            var item = snapshot?.Metrics.SingleOrDefault(item => item.Metric == metric);
            var allowed = metric switch
            {
                Metric.MarketCap => item?.Provider is "CoinGecko" or "CoinMarketCap",
                Metric.DexLiquidity => item?.Provider == "DEXScreener",
                Metric.Top10 or Metric.TotalClusterPercent => item?.Provider == "InsightX",
                _ => item?.Provider == "Binance"
            };
            if (item is not null) evidence.Add(item);
            if (snapshot is null || snapshot.AssetId != asset.AssetId || snapshot.AsOf > now ||
                !allowed || item is not { UsableForScoring: true, Value: not null } ||
                item.Status is not (ResolutionStatus.SingleSource or ResolutionStatus.Confirmed) ||
                !item.Evidence.Any(source => source.AssetId == asset.AssetId && source.Metric == metric &&
                    source.Provider == item.Provider &&
                    (metric is not (Metric.DexLiquidity or Metric.Top10 or Metric.TotalClusterPercent) ||
                        source.Chain is { } chain && source.Contract is { } contract &&
                        asset.Chains.Any(token => token.Chain == chain && string.Equals(token.Address,contract,
                            chain == ChainId.Solana ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))) &&
                    (metric is Metric.MarketCap or Metric.DexLiquidity or Metric.Top10 or Metric.TotalClusterPercent || source.Provider == "Binance") &&
                    source.Status == ObservationStatus.Valid && source.Value == item.Value &&
                    source.TimeQuality is TimeQuality.ProviderTime or TimeQuality.BlockTime &&
                    source.SourceTime <= source.ReceivedTime && source.ReceivedTime <= snapshot.AsOf &&
                    source.SourceTime <= now && now - source.SourceTime <= maximumAge))
            {
                missing.Add(metric.ToString());
                return null;
            }
            return item.Value;
        }
        var cap = Value(Metric.MarketCap,TimeSpan.FromHours(8));
        var liquidity = Value(Metric.DexLiquidity,TimeSpan.FromHours(8));
        var ownership = Value(Metric.Top10,TimeSpan.FromHours(8));
        var cluster = Value(Metric.TotalClusterPercent,TimeSpan.FromHours(8));
        var qualified = cap is > 0 and <= LowCapResearchLimit && liquidity is >= 500_000 &&
            ownership is > 0 and <= 100 && cluster is >= 0 and <= 100 && asset.Chains.Count > 0;
        if (asset.Chains.Count == 0) missing.Add("VerifiedChainIdentity");
        if (!qualified) reasons.Add("LowCapOwnershipLiquidityNotEstablished");
        if (cap is > LowCapResearchLimit) reasons.Add("AboveLowCapResearchLimit");
        reasons.Add("ResearchOnlyNoTradeInstruction");
        RadarAssessment Result(RadarStage stage) => new("Radar-Participation-2.0.0",asset.AssetId,now,stage,qualified,
            reasons.ToArray(),missing.Distinct().ToArray(),evidence.ToArray());
        if (asset.BinanceSymbol is "BTCUSDT" or "ETHUSDT")
        {
            reasons.Add("BenchmarkExcludedFromSpeculativeRadar");
            return Result(RadarStage.Excluded);
        }
        _ = Value(Metric.Price, TimeSpan.FromMinutes(15));
        var volume = Value(Metric.VolumeRatioH1,TimeSpan.FromMinutes(75));
        var change = Value(Metric.Return24hClosed,TimeSpan.FromMinutes(75));
        var taker = Value(Metric.TakerBuyShareH1,TimeSpan.FromMinutes(75));
        var oi1 = Value(Metric.OiQuantityChange1h,TimeSpan.FromMinutes(10));
        var oi4 = Value(Metric.OiQuantityChange4h,TimeSpan.FromMinutes(10));
        if (volume is null || change is null || taker is null || oi1 is null || oi4 is null)
            return Result(RadarStage.InsufficientData);
        // Versioned research hypotheses; activity alone cannot establish accumulation by an operator.
        if (change >= 50 && volume >= 3)
        {
            reasons.Add("RapidRiseAndVolumeExpansion");
            return Result(RadarStage.Overheated);
        }
        if (change >= 10 && volume >= 1.5m && taker >= .55m && oi1 >= 1 && oi4 >= 3)
        {
            reasons.Add("PriceVolumeOiExpansion");
            return Result(RadarStage.Expansion);
        }
        if (Math.Abs(change.Value) <= 15 && volume >= 1.2m && taker >= .52m && oi1 >= 1 && oi4 >= 3)
        {
            reasons.Add("ParticipationBeforeLargePriceExpansion");
            return Result(RadarStage.Participation);
        }
        reasons.Add("NoCombinedParticipationPattern");
        return Result(RadarStage.Observing);
    }
}
