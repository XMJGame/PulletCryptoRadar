using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Scanner;

public sealed class DownMoveDetector(VolatilityOptions options)
{
    public const string Version = "DownMove-Phase3-1.0";
    public MoveAssessment Detect(MarketView m, DateTimeOffset now)
    {
        var r1 = Evidence.Return(m, 1, now, options); var r5 = Evidence.Return(m, 5, now, options);
        var r15 = Evidence.Return(m, 15, now, options); var r1h = Evidence.Return(m, 60, now, options);
        var r4h = Evidence.Return(m, 240, now, options);
        var z = Evidence.Statistic(m.Features.PriceRobustZ5m, now, options);
        var volume = Evidence.Statistic(m.Features.VolumeRobustZ, now, options);
        var sharpDrop = r1 <= -3 || r5 <= -5 || r15 <= -8;
        var priceConfirmed = (r15 <= -1.5m || r5 <= -1.5m || r1 <= -1) && (z <= -1.5 || sharpDrop);
        // Downside has a separate short-horizon shock term and lower volume confirmation threshold.
        var loss = new[] { -(double)(r1 ?? 0) / 1.5, -(double)(r5 ?? 0) / 3, -(double)(r15 ?? 0) / 6,
            -(double)(r1h ?? 0) / 12, -(double)(r4h ?? 0) / 25 }.Max();
        double? priceStrength = r1 is null && r5 is null && r15 is null ? null :
            0.65 * Evidence.Clamp(loss) + 0.35 * Evidence.Clamp(-(z ?? 0) / 3);
        // An undefined Z does not become a fabricated zero: without it only a large price shock can qualify.
        if (z is null && !sharpDrop) priceStrength = null;
        var price = new ScoreComponent("Price", 20, priceStrength, priceConfirmed, $"下跌压力：1m {r1:0.00}% / 5m {r5:0.00}% / 15m {r15:0.00}%；价格 RZ {(z is null ? "Unknown" : z.Value.ToString("0.00"))}", m.PriceSourceTime);
        var vol = new ScoreComponent("Volume", 15, volume is null ? null : Evidence.Clamp(volume.Value / 3), volume >= 1.5,
            volume is null ? "成交量 RZ 缺失或过期" : $"5m 放量卖压待核实：成交量 RZ {volume:0.00}；主动卖盘 Unknown", m.Features.VolumeRobustZ.SourceTime);
        var structure = Evidence.Structure(m, false, now, options);
        var confirmation = priceConfirmed && vol.Confirmed;
        var stage = DownMoveStage.Normal;
        if (confirmation)
        {
            stage = DownMoveStage.Weakening;
            if (structure.Confirmed) stage = DownMoveStage.Breakdown;
            if (r5 <= -5 || r15 <= -8 || r1h <= -12) stage = DownMoveStage.Selloff;
            if (r1 <= -5 || r15 <= -15 || r1h <= -25) stage = DownMoveStage.Crash;
        }
        var basic = Evidence.Assessment(m, MoveDirection.Down, Version, stage.ToString(), confirmation,
            [price, vol, structure], options.DownEntryScore, now, options);
        return DerivativeMoveDetector.Upgrade(m, basic, now, options);
    }
}
