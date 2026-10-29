namespace PulletCryptoRadar.Scanner;

public sealed class AlertOptions
{
    public bool ToastEnabled { get; set; } = true;
    public int DailyNormalLimit { get; set; } = 5;
    public int WindowMinutes { get; set; } = 30;
    public int MaxNewAssetsPerWindow { get; set; } = 2;
    public int CompetitionDelaySeconds { get; set; } = 60;
    public int NotificationLifetimeSeconds { get; set; } = 120;
    public int MaxSnapshotAgeSeconds { get; set; } = 90;
    public int MaxPriceAgeSeconds { get; set; } = 30;
    public int SystemicResetMinutes { get; set; } = 120;
    public double EscalationScoreIncrease { get; set; } = 12;
    public int WorkerIntervalSeconds { get; set; } = 5;
    public string BudgetTimeZone { get; set; } = "Asia/Shanghai";
    public bool IsValid()
    {
        if (DailyNormalLimit is < 0 or > 100 || WindowMinutes is < 1 or > 1440 || MaxNewAssetsPerWindow is < 0 or > 20
            || CompetitionDelaySeconds < 0 || CompetitionDelaySeconds >= WindowMinutes * 60 || NotificationLifetimeSeconds is < 10 or > 600
            || MaxSnapshotAgeSeconds is < 5 or > 300 || MaxPriceAgeSeconds is < 1 or > 120 || SystemicResetMinutes < 1
            || EscalationScoreIncrease is < 1 or > 100 || WorkerIntervalSeconds is < 1 or > 60) return false;
        try { TimeZoneInfo.FindSystemTimeZoneById(BudgetTimeZone); return true; }
        catch (TimeZoneNotFoundException) { return false; }
        catch (InvalidTimeZoneException) { return false; }
    }
}
