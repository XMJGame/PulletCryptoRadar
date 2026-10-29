namespace PulletCryptoRadar.Providers.Binance;

public sealed class DerivativeOptions
{
    public bool Enabled { get; set; } = true;
    public int PollSeconds { get; set; } = 300;
    public int DeepCandidateCount { get; set; } = 80;
    public int HistoryLimit { get; set; } = 100;
    public int Concurrency { get; set; } = 3;
    public string[] PrioritySymbols { get; set; } = [];
    public bool IsValid() => PollSeconds is >= 300 and <= 3600 && DeepCandidateCount is >= 1 and <= 100
        && HistoryLimit is >= 35 and <= 500 && Concurrency is >= 1 and <= 5 && PrioritySymbols.Length <= 20;
}
