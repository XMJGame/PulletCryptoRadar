namespace PulletCryptoRadar.Core;

public interface INetworkTraffic { void BeforeRequest(); void Record(long bytes); }
public sealed class NetworkBudgetExceededException(string message) : InvalidOperationException(message);

public sealed class NetworkOptions
{
    public bool EconomyMode { get; set; } = true;
    public bool UseSystemProxy { get; set; } = false;
    public long DailyBudgetBytes { get; set; } = 150_000_000;
    public long MonthlyBudgetBytes { get; set; } = 5_000_000_000;
    public string[] PrioritySymbols { get; set; } = ["BTCUSDT", "ETHUSDT", "APRUSDT", "LABUSDT", "MYXUSDT", "BTWUSDT", "TRBUSDT"];
    public int HistorySymbols { get; set; } = 20;
    public int TickerSeconds { get; set; } = 20;
    public bool IsValid() => PrioritySymbols is {Length:<=20} && PrioritySymbols.Distinct(StringComparer.Ordinal).Count()==PrioritySymbols.Length
        && PrioritySymbols.All(s=>s is not null && System.Text.RegularExpressions.Regex.IsMatch(s,@"\A[A-Z0-9]{2,30}USDT\z"))
        && DailyBudgetBytes > 0 && MonthlyBudgetBytes >= DailyBudgetBytes
        && HistorySymbols is >= 2 and <= 100 && TickerSeconds is >= 10 and <= 60;
}
