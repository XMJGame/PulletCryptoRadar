using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Scanner;

internal static class Evidence
{
    public static bool Fresh<T>(FeatureValue<T>? feature, DateTimeOffset now, int seconds) where T : struct
        => feature is { Status: DataStatus.Valid, Value: not null, SourceTime: not null }
            && now >= feature.SourceTime && now - feature.SourceTime <= TimeSpan.FromSeconds(seconds);
    public static decimal? Return(MarketView m, int minutes, DateTimeOffset now, VolatilityOptions options)
    {
        var f = m.Features.Returns.GetValueOrDefault(minutes);
        return Fresh(f, now, options.PriceFreshnessSeconds) ? f!.Value : null;
    }
    public static double? Statistic(FeatureValue<double> feature, DateTimeOffset now, VolatilityOptions options)
        => Fresh(feature, now, options.StatisticFreshnessSeconds) && double.IsFinite(feature.Value!.Value) ? feature.Value : null;
    public static bool PriceFresh(MarketView m, DateTimeOffset now, VolatilityOptions o)
        => m.Price > 0 && m.PriceSourceTime is not null && now >= m.PriceSourceTime
            && now - m.PriceSourceTime <= TimeSpan.FromSeconds(o.PriceFreshnessSeconds);
    public static double Clamp(double value) => Math.Clamp(value, 0, 1);
    public static ScoreComponent Structure(MarketView m, bool up, DateTimeOffset now, VolatilityOptions o)
    {
        var distances = up ? m.Features.DistanceToHigh : m.Features.DistanceToLow;
        var available = new[] { 60, 240, 1440 }.Select(h => (Horizon: h, Value: distances.GetValueOrDefault(h)))
            .Where(x => Fresh(x.Value, now, o.PriceFreshnessSeconds)).ToArray();
        var confirmed = available.Where(x => up ? x.Value!.Value >= o.StructureBufferPercent : x.Value!.Value <= -o.StructureBufferPercent)
            .OrderByDescending(x => x.Horizon).ToArray();
        var best = confirmed.FirstOrDefault();
        double? strength = available.Length == 0 ? null : confirmed.Length == 0 ? 0 : best.Horizon == 1440 ? 1 : best.Horizon == 240 ? 0.8 : 0.55;
        return new("Structure", 10, strength, confirmed.Length > 0,
            available.Length == 0 ? "结构历史不足或已过期" : confirmed.Length == 0 ? "尚未确认突破/跌破" : $"{(up ? "突破前高" : "跌破前低")} · {best.Horizon}m · {best.Value!.Value:0.00}%",
            available.Length == 0 ? null : available.Max(x => x.Value!.SourceTime));
    }
    public static MoveAssessment Assessment(MarketView m, MoveDirection direction, string version, string stage,
        bool directionConfirmed, ScoreComponent[] components, double entryScore, DateTimeOffset now, VolatilityOptions o)
    {
        var fresh = PriceFresh(m, now, o);
        var complete = components[0].Strength is not null && components[1].Strength is not null;
        // Fixed Phase-3 denominator; a missing structure never boosts remaining components.
        var score = !fresh || !complete ? (double?)null : Math.Round(100 * components.Sum(c => c.Weight * (c.Strength ?? 0)) / 45, 2);
        var confidence = fresh ? (int)Math.Floor(60 * components.Where(c => c.Strength is not null).Sum(c => c.Weight) / 45) : 0;
        if (m.Features.NewListing) confidence = Math.Min(confidence, 45);
        var missing = components.Where(c => c.Strength is null).Select(c => c.Family)
            .Concat(direction == MoveDirection.Up ? new[] { "OpenInterest", "Flow", "Trader", "Funding", "MarketCap" }
                : new[] { "OpenInterest", "Flow", "Liquidation", "Funding/Crowding" }).ToArray();
        var status = !fresh ? m.PriceSourceTime is null ? DataStatus.Missing : DataStatus.Stale : !complete ? DataStatus.Missing : DataStatus.Valid;
        var reasons = components.Select(c => c.Description).Append("Phase 3：仅价格、成交量与技术结构；置信度上限 60，未验证预测能力。").ToList();
        if (!fresh) reasons.Insert(0, "实时价格缺失、过期或源时间异常，暂停生成候选。");
        if (m.Features.NewListing) reasons.Add("新上市合约，置信度上限降低至 45。");
        return new(direction, version, score, confidence, status == DataStatus.Valid ? stage : "Unknown", status,
            status == DataStatus.Valid && directionConfirmed && score >= entryScore, components,
            components.Where(c => c.Confirmed).Select(c => c.Family).ToArray(), missing, reasons);
    }
}
