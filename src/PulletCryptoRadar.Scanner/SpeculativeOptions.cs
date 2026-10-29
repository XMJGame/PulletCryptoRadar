namespace PulletCryptoRadar.Scanner;

public sealed class SpeculativeOptions
{
    public int IntervalSeconds { get; set; } = 300;
    public decimal MinMarketCapUsd { get; set; } = 5_000_000;
    public decimal MaxMarketCapUsd { get; set; } = 1_000_000_000;
    public decimal MinVolume24hUsd { get; set; } = 5_000_000;
    public decimal MinOpenInterestUsd { get; set; } = 1_000_000;
    public double EntryScore { get; set; } = 45;
    public decimal VolumeRatioFull { get; set; } = 1;
    public decimal OiRatioFull { get; set; } = 0.2m;
    public decimal ExtremeOiRatio { get; set; } = 0.5m;
    public decimal ExtremeFunding8h { get; set; } = 0.003m;
    public decimal HighFdvRatio { get; set; } = 10;
    public int MinimumCoverage { get; set; } = 50;
    public bool IsValid() => IntervalSeconds is >= 30 and <= 3600 && MinMarketCapUsd > 0 && MaxMarketCapUsd > MinMarketCapUsd
        && MinVolume24hUsd > 0 && MinOpenInterestUsd > 0 && EntryScore is > 0 and <= 100
        && VolumeRatioFull > 0 && OiRatioFull > 0 && ExtremeOiRatio > OiRatioFull && ExtremeFunding8h > 0
        && HighFdvRatio > 1 && MinimumCoverage is >= 50 and <= 75;
}
