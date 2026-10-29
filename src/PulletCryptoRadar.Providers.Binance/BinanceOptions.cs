namespace PulletCryptoRadar.Providers.Binance;

public sealed class BinanceOptions
{
    public bool Enabled { get; set; } = true;
    public string RestBaseUrl { get; set; } = "https://fapi.binance.com";
    public string WebSocketUrl { get; set; } = "wss://fstream.binance.com/market/stream";
    public int RequestWeightPerMinute { get; set; } = 600;
    public int StatisticsRequestsPerMinute { get; set; } = 120;
    public int StreamSymbolsPerConnection { get; set; } = 150;
    public int HistoryConcurrency { get; set; } = 3;
    public int MinuteHistoryCount { get; set; } = 2880;
    public int FiveMinuteHistoryCount { get; set; } = 2016;
    public int FifteenMinuteHistoryCount { get; set; } = 2880;
    public int ReceiveTimeoutSeconds { get; set; } = 45;
}
