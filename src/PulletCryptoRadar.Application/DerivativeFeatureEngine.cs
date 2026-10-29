using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Application;

public sealed class DerivativeFeatureEngine
{
    public DerivativeFeatures Calculate(MarketView market, DerivativeRuntimeSnapshot runtime, DateTimeOffset now)
    {
        var current = Series(DerivativeKind.CurrentOpenInterest).LastOrDefault();
        var history = Series(DerivativeKind.OpenInterest5m);
        var oi = history.Count > 1 && now - history[^1].SourceTime <= TimeSpan.FromMinutes(11) ? history : Series(DerivativeKind.CurrentOpenInterest);
        var latest = oi.LastOrDefault();
        var changes = new Dictionary<int, FeatureValue<decimal>>();
        foreach (var h in new[] { 5, 15, 60, 240 }) changes[h] = Change(oi, h, p => p.Quantity, now);
        var sequence = oi.Zip(oi.Skip(1)).Where(p => Bucket(p.Second.SourceTime) - Bucket(p.First.SourceTime) == TimeSpan.FromMinutes(5) && p.First.Quantity > 0 && p.Second.Quantity is not null)
            .Select(p => (Sample: p.Second, Value: (double)((p.Second.Quantity!.Value / p.First.Quantity!.Value - 1) * 100))).ToArray();
        var baseline = sequence.SkipLast(1).Select(x => x.Value).ToArray();
        double? lastOiChange = sequence.Length > 0 && sequence[^1].Sample.SourceTime == latest?.SourceTime ? sequence[^1].Value : null;
        var taker = Series(DerivativeKind.Taker5m); var flow = taker.LastOrDefault();
        decimal? share = flow is not null && flow.BuyQuantity + flow.SellQuantity > 0 ? flow.BuyQuantity / (flow.BuyQuantity + flow.SellQuantity) : null;
        var flowBaseline = taker.SkipLast(1).Where(x => x.BuyQuantity + x.SellQuantity > 0)
            .Select(x => (double)(x.BuyQuantity!.Value / (x.BuyQuantity.Value + x.SellQuantity!.Value))).ToArray();
        var coverage = runtime.CoverageStart <= now.AddMinutes(-15) && runtime.LastStreamActivity <= now
            && now - runtime.LastStreamActivity <= TimeSpan.FromSeconds(45);
        var liquidations = runtime.Liquidations.Where(x => x.SourceTime >= now.AddMinutes(-15) && x.SourceTime <= now).ToArray();
        var liquidationBaseline = new List<double>();
        if (coverage && runtime.CoverageStart is { } coverageStart)
            for (var end = now.AddMinutes(-15); end.AddMinutes(-15) >= coverageStart && liquidationBaseline.Count < 96; end = end.AddMinutes(-15))
                liquidationBaseline.Add((double)runtime.Liquidations.Where(x => x.SourceTime >= end.AddMinutes(-15) && x.SourceTime < end).Sum(x => x.NotionalUsd));
        var top = Series(DerivativeKind.TopPosition5m); var global = Series(DerivativeKind.GlobalLongShort5m).LastOrDefault();
        var account = Series(DerivativeKind.TopAccount5m).LastOrDefault();
        var oi15 = changes[15]; var price15 = market.Features.Returns.GetValueOrDefault(15);
        var structure = price15 is { Status: DataStatus.Valid, Value: not null } && oi15 is { Status: DataStatus.Valid, Value: not null }
            ? price15.Value > 0 && oi15.Value > 0 ? "PriceUpOiUp" : price15.Value < 0 && oi15.Value > 0 ? "ShortBuild"
                : price15.Value < 0 && oi15.Value < 0 ? "Deleveraging" : price15.Value > 0 && oi15.Value < 0 ? "ShortCovering" : "Neutral" : "Unknown";
        var markFresh = market.FundingSourceTime <= now && now - market.FundingSourceTime <= TimeSpan.FromSeconds(30);
        decimal? estimatedValue = current?.Quantity * (markFresh ? market.MarkPrice : null);
        var quantityPoint = current ?? history.LastOrDefault() ?? latest;
        return new()
        {
            OpenInterestQuantity = Value(quantityPoint?.Quantity, quantityPoint?.SourceTime, now),
            OpenInterestUsd = estimatedValue is not null ? Value(estimatedValue, current?.SourceTime, now, "OI quantity × fresh mark price")
                : Value(history.LastOrDefault()?.NotionalUsd, history.LastOrDefault()?.SourceTime, now, "Binance OI history notional"),
            OiChanges = changes, OiRobustZ = Statistic(lastOiChange is null ? null : RobustStatistics.RobustZ(lastOiChange.Value, baseline), latest?.SourceTime, now),
            OiPercentile = Statistic(lastOiChange is null ? null : RobustStatistics.Percentile(lastOiChange.Value, baseline), latest?.SourceTime, now),
            OiBaselineSamples = baseline.Length, GlobalLongShare = Value(global?.LongShare, global?.SourceTime, now),
            TopAccountLongShare = Value(account?.LongShare, account?.SourceTime, now),
            TopPositionLongShare = Value(top.LastOrDefault()?.LongShare, top.LastOrDefault()?.SourceTime, now),
            TopPositionChange15m = Change(top, 15, p => p.LongShare, now, difference: true),
            TakerBuyShare = Value(share, flow?.SourceTime, now), TakerBuySellRatio = Value(flow?.Ratio, flow?.SourceTime, now),
            TakerRobustZ = Statistic(share is null ? null : RobustStatistics.RobustZ((double)share.Value, flowBaseline), flow?.SourceTime, now),
            ObservedLongLiquidation15mUsd = new(coverage ? liquidations.Where(x => x.Side == "SELL").Sum(x => x.NotionalUsd) : null,
                coverage ? DataStatus.Valid : DataStatus.Missing, coverage ? now : null, "Observed Binance forceOrder samples"),
            ObservedShortLiquidation15mUsd = new(coverage ? liquidations.Where(x => x.Side == "BUY").Sum(x => x.NotionalUsd) : null,
                coverage ? DataStatus.Valid : DataStatus.Missing, coverage ? now : null, "Observed Binance forceOrder samples"),
            LiquidationCoverageComplete = coverage, PriceOiStructure = structure,
            LiquidationRobustZ = Statistic(coverage ? RobustStatistics.RobustZ((double)liquidations.Sum(x => x.NotionalUsd), liquidationBaseline) : null, coverage ? now : null, now)
        };
        IReadOnlyList<DerivativeSample> Series(DerivativeKind kind) => runtime.Series.GetValueOrDefault(kind) ?? [];
    }
    public static DateTimeOffset Bucket(DateTimeOffset time) => DateTimeOffset.FromUnixTimeSeconds(time.ToUnixTimeSeconds() / 300 * 300);
    private static FeatureValue<decimal> Change(IReadOnlyList<DerivativeSample> series, int minutes, Func<DerivativeSample, decimal?> selector,
        DateTimeOffset now, bool difference = false)
    {
        if (series.Count == 0) return FeatureValue<decimal>.Missing();
        var last = series[^1]; var end = Bucket(last.SourceTime); var start = end.AddMinutes(-minutes);
        var buckets = series.GroupBy(x => Bucket(x.SourceTime)).ToDictionary(g => g.Key, g => g.Last());
        if (!Enumerable.Range(0, minutes / 5 + 1).All(i => buckets.ContainsKey(start.AddMinutes(i * 5)))) return FeatureValue<decimal>.Missing();
        var before = selector(buckets[start]); var after = selector(last);
        return Value((difference ? before >= 0 : before > 0) && after is not null ? difference ? (after - before) * 100 : (after / before - 1) * 100 : null,
            last.SourceTime, now, difference ? "Top position long share change (percentage points)" : "Open interest QUANTITY change (%)");
    }
    private static FeatureValue<decimal> Value(decimal? value, DateTimeOffset? timestamp, DateTimeOffset now, string source = "Binance derivatives 5m")
        => new(value, value is null ? DataStatus.Missing : timestamp > now ? DataStatus.Invalid : now - timestamp > TimeSpan.FromMinutes(11) ? DataStatus.Stale : DataStatus.Valid, timestamp, source);
    private static FeatureValue<double> Statistic(double? value, DateTimeOffset? timestamp, DateTimeOffset now)
        => new(value, value is null ? DataStatus.Missing : timestamp > now ? DataStatus.Invalid : now - timestamp > TimeSpan.FromMinutes(11) ? DataStatus.Stale : DataStatus.Valid, timestamp, "Derivative historical baseline; current sample excluded");
}
