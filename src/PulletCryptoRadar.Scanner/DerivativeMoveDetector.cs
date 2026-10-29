using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Scanner;

public static class DerivativeMoveDetector
{
    public const string UpVersion = "UpMove-2.0";
    public const string DownVersion = "DownMove-2.0";
    public static MoveAssessment Upgrade(MarketView m, MoveAssessment basic, DateTimeOffset now, VolatilityOptions options)
    {
        var d = m.Features.Derivatives; var up = basic.Direction == MoveDirection.Up;
        decimal? Fresh(FeatureValue<decimal>? f) => Evidence.Fresh(f, now, 660) ? f!.Value : null;
        var oi = Fresh(d.OiChanges.GetValueOrDefault(15)); var flow = Fresh(d.TakerBuyShare);
        // The old, lower-confidence model stays available when no derivative evidence exists.
        if (oi is null && flow is null) return basic;
        var z = Evidence.Statistic(d.OiRobustZ, now, options);
        var top = Fresh(d.TopPositionChange15m); var account = Fresh(d.TopAccountLongShare); var global = Fresh(d.GlobalLongShare);
        var funding = m.FundingSourceTime <= now && now - m.FundingSourceTime <= TimeSpan.FromSeconds(30) ? m.FundingRate : null;
        var oiConfirmed = up ? oi >= 3 && z >= 2 : Math.Abs(oi ?? 0) >= 3 && (Math.Abs(z ?? 0) >= 2 || oi <= -8);
        double? oiStrength = oi is null ? null : up ? (Evidence.Clamp((double)oi / 6) + Evidence.Clamp((z ?? 0) / 3)) / 2
            : (Evidence.Clamp((double)Math.Abs(oi.Value) / 6) + Evidence.Clamp(Math.Abs(z ?? 0) / 3)) / 2;
        var flowConfirmed = up ? flow >= 0.6m : flow <= 0.4m;
        double? flowStrength = flow is null ? null : Evidence.Clamp((up ? (double)flow - 0.5 : 0.5 - (double)flow) / 0.15);
        var components = basic.Components.Select(c => c with { Weight = c.Family switch { "Price" => up ? 15 : 20, "Volume" => up ? 20 : 15, _ => 10 } }).ToList();
        components.Add(new("OpenInterest", 20, oiStrength, oiConfirmed,
            $"OI 数量 15m {oi:0.00}% · OI RZ {(z is null ? "Unknown" : z.Value.ToString("0.00"))} · {d.PriceOiStructure}", d.OiChanges.GetValueOrDefault(15)?.SourceTime));
        components.Add(new("Flow", 15, flowStrength, flowConfirmed, $"Taker 主动买量占比 {(flow is null ? "Unknown" : flow.Value.ToString("0.0%"))}", d.TakerBuyShare.SourceTime));
        var crowded = funding >= 0.001m || global >= 0.8m;
        if (up)
        {
            components.Add(new("Trader", 10, top is null || account is null ? null : Evidence.Clamp((double)top / 3),
                top >= 1 && account >= 0.5m, $"Top Position 15m 变化 {top:0.00} 个百分点；Top Account 多头占比 {account:0.0%}", d.TopPositionChange15m.SourceTime));
            components.Add(new("Funding", 5, funding is null ? null : crowded ? 0 : funding <= 0 ? 1 : 0.7,
                false, $"Funding {funding:0.000000}；拥挤状态 {(global is null ? "Unknown" : global.Value.ToString("0.0%"))}", m.FundingSourceTime));
            components.Add(new("MarketCap", 5, null, false, "市值/流动性映射未接入", null));
        }
        else
        {
            var liquidations = Fresh(d.ObservedLongLiquidation15mUsd);
            components.Add(new("Liquidation", 15, liquidations is null ? null : Evidence.Clamp((double)(liquidations / options.ObservedLiquidationConfirmationUsd)),
                liquidations >= options.ObservedLiquidationConfirmationUsd,
                $"观测多头强平 15m ${liquidations:0.00}（1s 抽样，非完整总额）", d.ObservedLongLiquidation15mUsd.SourceTime));
            components.Add(new("Funding", 5, funding is null || global is null ? null : crowded ? 1 : 0,
                false, "Funding/多头拥挤作为风险背景，不作为独立确认家族", m.FundingSourceTime));
        }
        var price = components.Single(c => c.Family == "Price"); var volume = components.Single(c => c.Family == "Volume");
        var structure = components.Single(c => c.Family == "Structure");
        var confirmationCount = new[] { volume.Confirmed, oiConfirmed, flowConfirmed, structure.Confirmed }.Count(x => x);
        var score = basic.Status != DataStatus.Valid || oi is null || flow is null ? (double?)null
            : Math.Round(components.Sum(c => c.Weight * (c.Strength ?? 0)), 2);
        var confidence = basic.Status != DataStatus.Valid ? 0 : (int)Math.Floor(90 * components.Where(c => c.Strength is not null).Sum(c => c.Weight) / 100);
        if (m.Features.NewListing) confidence = Math.Min(confidence, 60);
        var risk = crowded || basic.Stage == "Overheated" ? SignalRisk.High : SignalRisk.Unknown;
        var stage = basic.Stage;
        var r15 = Evidence.Return(m, 15, now, options); var r1h = Evidence.Return(m, 60, now, options);
        var candidate = score >= (up ? options.UpEntryScore : options.DownEntryScore) && price.Confirmed && confirmationCount >= 1;
        if (up && basic.Status == DataStatus.Valid)
        {
            stage = !candidate ? "Normal" : confirmationCount >= 2 ? "Ignition" : "Preheat";
            if (candidate && structure.Confirmed && (oiConfirmed || flowConfirmed)) stage = "Breakout";
            if (candidate && (r1h >= 20 || Evidence.Return(m, 240, now, options) >= 35)) stage = "Acceleration";
            if (crowded || basic.Stage == "Overheated") stage = "Overheated";
            if (r1h > 5 && oi < -3 && flow < 0.45m) { stage = "Distribution"; risk = SignalRisk.High; candidate = false; }
        }
        else if (!up && basic.Status == DataStatus.Valid)
        {
            stage = !candidate ? "Normal" : structure.Confirmed ? "Breakdown" : "Weakening";
            if (candidate && (r15 <= -8 || r1h <= -12)) stage = "Selloff";
            if (candidate && (r15 <= -15 || r1h <= -25 || oi <= -8 && components.Any(c => c.Family == "Liquidation" && c.Confirmed))) stage = "Crash";
        }
        var reasons = components.Select(c => c.Description).Append("V2 使用 OI 数量变化；市值/链上风险仍未知。强平为观测样本，无法保证完整。").ToArray();
        return new(basic.Direction, up ? UpVersion : DownVersion, score, confidence, score is null ? "Unknown" : stage,
            basic.Status != DataStatus.Valid ? basic.Status : score is null ? DataStatus.Missing : DataStatus.Valid, candidate && score is not null,
            components, components.Where(c => c.Confirmed).Select(c => c.Family).ToArray(),
            components.Where(c => c.Strength is null).Select(c => c.Family).Concat(["OnChain", "Wallet"]).ToArray(), reasons) { Risk = risk };
    }
}
