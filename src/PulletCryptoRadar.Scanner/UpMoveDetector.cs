using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Scanner;

public sealed class UpMoveDetector(VolatilityOptions options)
{
    public const string Version = "UpMove-Phase3-1.0";
    public MoveAssessment Detect(MarketView m, DateTimeOffset now)
    {
        var r5 = Evidence.Return(m, 5, now, options); var r15 = Evidence.Return(m, 15, now, options);
        var r1h = Evidence.Return(m, 60, now, options); var r4h = Evidence.Return(m, 240, now, options);
        var z5 = Evidence.Statistic(m.Features.PriceRobustZ5m, now, options);
        var z15 = Evidence.Fresh(m.Features.PriceRobustZ15m, now, 1860) && double.IsFinite(m.Features.PriceRobustZ15m.Value!.Value)
            ? m.Features.PriceRobustZ15m.Value : null;
        var z = z5 is null ? z15 : z15 is null ? z5 : Math.Max(z5.Value, z15.Value);
        var volume = Evidence.Statistic(m.Features.VolumeRobustZ, now, options);
        var priceConfirmed = (r15 >= 2 || r5 >= 2) && z >= 2;
        double? priceStrength = (r5 is null && r15 is null) || z is null ? null :
            (Evidence.Clamp(Math.Max((double)(r15 ?? 0) / 6, (double)(r5 ?? 0) / 3)) + Evidence.Clamp(z.Value / 3)) / 2;
        var price = new ScoreComponent("Price", 15, priceStrength, priceConfirmed, $"上涨结构：5m {r5:0.00}% / 15m {r15:0.00}%；价格 RZ {z:0.00}", m.PriceSourceTime);
        var vol = new ScoreComponent("Volume", 20, volume is null ? null : Evidence.Clamp(volume.Value / 3), volume >= 2,
            volume is null ? "成交量 RZ 缺失或过期" : $"5m 成交量 RZ {volume:0.00}", m.Features.VolumeRobustZ.SourceTime);
        var structure = Evidence.Structure(m, true, now, options);
        var confirmation = priceConfirmed && vol.Confirmed;
        var stage = UpMoveStage.Normal;
        if (confirmation)
        {
            stage = UpMoveStage.Preheat;
            if (structure.Confirmed) stage = UpMoveStage.Ignition;
            if (structure.Confirmed && structure.Strength >= 0.8) stage = UpMoveStage.Breakout;
            if (r1h >= 20 || r4h >= 35) stage = UpMoveStage.Acceleration;
            if (r15 >= 25 || r1h >= 40 || r4h >= 70) stage = UpMoveStage.Overheated;
        }
        var basic = Evidence.Assessment(m, MoveDirection.Up, Version, stage.ToString(), confirmation,
            [price, vol, structure], options.UpEntryScore, now, options);
        return DerivativeMoveDetector.Upgrade(m, basic, now, options);
    }
}
