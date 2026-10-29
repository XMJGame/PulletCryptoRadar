namespace PulletCryptoRadar.Core;

public interface IPriorityQuoteProvider
{
    Task<IReadOnlyList<MarketEvent>> GetPriorityQuotesAsync(string symbol, CancellationToken ct);
}

public interface IMarketEventBus
{
    ValueTask PublishAsync(MarketEvent marketEvent, CancellationToken ct);
    IAsyncEnumerable<MarketEvent> ReadAllAsync(CancellationToken ct);
}
public interface IMarketRestProvider
{
    Task<IReadOnlyList<SymbolDefinition>> GetSymbolsAsync(CancellationToken ct);
    Task<IReadOnlyList<TickerEvent>> GetTickersAsync(CancellationToken ct);
    Task<IReadOnlyList<Candle>> GetCandlesAsync(string symbol, int intervalMinutes, int limit, DateTimeOffset? endTime, CancellationToken ct);
}
public interface IMarketStreamProvider
{
    Task RunAsync(IReadOnlyCollection<string> symbols, CancellationToken ct);
}
public interface IRequestLimiter
{
    Task WaitAsync(string provider, int weight, CancellationToken ct);
    void Pause(string provider, TimeSpan duration);
}
public interface IProviderHealthStore
{
    void Success(string provider, double latencyMs = 0, int freshnessSeconds = 60);
    void Failure(string provider, string error);
    IReadOnlyList<ProviderHealth> GetAll();
}
public interface IMarketRuntimeStore
{
    void Register(IReadOnlyList<Instrument> instruments);
    void Apply(MarketEvent marketEvent);
    IReadOnlyList<MarketView> GetAll();
    MarketView? Get(int assetId);
    IReadOnlyList<Candle> GetCandles(int assetId, int intervalMinutes);
    void SetFeatures(int assetId, AssetFeatures features);
}
public interface IAssetRepository
{
    Task<IReadOnlyList<Instrument>> ReadInstrumentsAsync(CancellationToken ct);
    Task<IReadOnlyList<Candle>> ReadHourlyHistoryAsync(int assetId, int limit, CancellationToken ct);
}
