namespace PulletCryptoRadar.Scanner;

public sealed class VolatilityOptions
{
    public int IntervalSeconds { get; set; } = 60;
    public double UpEntryScore { get; set; } = 45;
    public double DownEntryScore { get; set; } = 45;
    public double EpisodeCloseScore { get; set; } = 40;
    public int EpisodeQuietMinutes { get; set; } = 120;
    public double EventScoreStep { get; set; } = 5;
    public int PriceFreshnessSeconds { get; set; } = 30;
    public int StatisticFreshnessSeconds { get; set; } = 660;
    public decimal StructureBufferPercent { get; set; } = 0.1m;
    public int MinimumMarketAssets { get; set; } = 30;
    public double MinimumMarketCoverage { get; set; } = 0.7;
    public double BroadBreadth { get; set; } = 0.7;
    public decimal BroadMedianPercent { get; set; } = 1;
    public double SystemicBreadth { get; set; } = 0.75;
    public decimal SystemicMedianPercent { get; set; } = 5;
    public decimal ObservedLiquidationConfirmationUsd { get; set; } = 100000;

    public bool IsValid() => IntervalSeconds is >= 5 and <= 3600 && UpEntryScore is > 0 and <= 100
        && DownEntryScore is > 0 and <= 100 && EpisodeCloseScore >= 0 && EpisodeCloseScore < Math.Min(UpEntryScore, DownEntryScore)
        && EpisodeQuietMinutes > 0 && EventScoreStep > 0 && PriceFreshnessSeconds > 0 && StatisticFreshnessSeconds >= 300
        && StructureBufferPercent >= 0 && MinimumMarketAssets >= 2 && MinimumMarketCoverage is > 0 and <= 1
        && BroadBreadth is > 0.5 and <= 1 && SystemicBreadth >= BroadBreadth && SystemicBreadth <= 1
        && BroadMedianPercent > 0 && SystemicMedianPercent > BroadMedianPercent && ObservedLiquidationConfirmationUsd > 0;
}
