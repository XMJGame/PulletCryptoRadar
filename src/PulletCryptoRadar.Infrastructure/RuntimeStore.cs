using System.Collections.Concurrent;
using System.Threading.Channels;
using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Infrastructure;

public sealed class MarketEventBus : IMarketEventBus
{
    private readonly Channel<MarketEvent> _channel = Channel.CreateBounded<MarketEvent>(new BoundedChannelOptions(20000)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    public ValueTask PublishAsync(MarketEvent marketEvent, CancellationToken ct) => _channel.Writer.WriteAsync(marketEvent, ct);
    public IAsyncEnumerable<MarketEvent> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

public sealed class MarketRuntimeStore : IMarketRuntimeStore
{
    private sealed class State(Instrument instrument)
    {
        public readonly object Gate = new();
        public Instrument Instrument = instrument;
        public TickerEvent? Ticker;
        public MarkPriceEvent? Mark;
        public AssetFeatures Features = new();
        public readonly Dictionary<int, RollingWindow> Windows = new() { [1] = new(2880), [5] = new(2016), [15] = new(2880) };
        public MarketView View() => new(Instrument, Ticker?.Price, Ticker?.QuoteVolume24h, Ticker?.Return24h,
            Mark?.MarkPrice, Mark?.FundingRate, Ticker?.SourceTime, Ticker?.ReceivedTime, Mark?.SourceTime,
            Windows[1].Count, Windows[5].Count, Windows[15].Count, Features){NextFundingTime=Mark?.NextFundingTime,MarkReceivedTime=Mark?.ReceivedTime};
    }
    private readonly ConcurrentDictionary<int, State> _states = new();
    private readonly ConcurrentDictionary<string, int> _symbols = new(StringComparer.Ordinal);
    private readonly object _registrationGate = new();

    public void Register(IReadOnlyList<Instrument> instruments)
    {
        lock (_registrationGate)
        {
            var active = instruments.Where(x => x.Active).Select(x => x.AssetId).ToHashSet();
            foreach (var state in _states.Values) lock (state.Gate) state.Instrument = state.Instrument with { Active = active.Contains(state.Instrument.AssetId) };
            foreach (var instrument in instruments)
            {
                _symbols[instrument.Symbol] = instrument.AssetId;
                var state = _states.GetOrAdd(instrument.AssetId, _ => new State(instrument));
                lock (state.Gate) state.Instrument = instrument;
            }
        }
    }
    public void Apply(MarketEvent marketEvent)
    {
        if(string.IsNullOrWhiteSpace(marketEvent.Symbol))return;
        if (!_symbols.TryGetValue(marketEvent.Symbol, out var id) || !_states.TryGetValue(id, out var state)) return;
        lock (state.Gate)
        {
            switch (marketEvent)
            {
                case TickerEvent ticker when ticker.Price > 0 && ticker.QuoteVolume24h >= 0 && (state.Ticker is null || ticker.SourceTime >= state.Ticker.SourceTime): state.Ticker = ticker; break;
                case MarkPriceEvent mark when mark.MarkPrice > 0 && (state.Mark is null || mark.SourceTime >= state.Mark.SourceTime): state.Mark = mark; break;
                case CandleEvent candle when candle.Candle.Symbol==state.Instrument.Symbol&&state.Windows.TryGetValue(candle.Candle.IntervalMinutes, out var window): window.Add(candle.Candle); break;
                case HistoryEvent history:
                    foreach (var group in history.Candles.Where(c=>c.Symbol==state.Instrument.Symbol).GroupBy(x => x.IntervalMinutes))
                        if (state.Windows.TryGetValue(group.Key, out var historyWindow)) historyWindow.AddRange(group);
                    break;
            }
        }
    }
    public IReadOnlyList<MarketView> GetAll()
    {
        lock (_registrationGate)
            return _states.Values.Select(s => { lock (s.Gate) return s.View(); }).Where(x => x.Instrument.Active).OrderBy(x => x.Instrument.Symbol).ToArray();
    }
    public MarketView? Get(int assetId) { if (!_states.TryGetValue(assetId, out var s)) return null; lock (s.Gate) return s.View(); }
    public IReadOnlyList<Candle> GetCandles(int assetId, int intervalMinutes)
    {
        if (!_states.TryGetValue(assetId, out var s)) return [];
        lock (s.Gate) return s.Windows.TryGetValue(intervalMinutes, out var w) ? w.Snapshot() : [];
    }
    public void SetFeatures(int assetId, AssetFeatures features) { if (_states.TryGetValue(assetId, out var s)) lock (s.Gate) s.Features = features; }
}

public sealed class ProviderHealthStore(TimeProvider time) : IProviderHealthStore
{
    private readonly ConcurrentDictionary<string, ProviderHealth> _health = new();
    public void Success(string provider, double latencyMs = 0, int freshnessSeconds = 60)
        => _health.AddOrUpdate(provider, new ProviderHealth(provider, time.GetUtcNow(), null, latencyMs, 0, null, freshnessSeconds),
            (_, old) => old with { LastSuccess = time.GetUtcNow(), LatencyMs = latencyMs, Error = null, FreshnessSeconds = freshnessSeconds });
    public void Failure(string provider, string error)
        => _health.AddOrUpdate(provider, new ProviderHealth(provider, null, time.GetUtcNow(), null, 1, error, 60),
            (_, old) => old with { LastFailure = time.GetUtcNow(), ErrorCount = old.ErrorCount + 1, Error = error });
    public IReadOnlyList<ProviderHealth> GetAll() => _health.Values.OrderBy(x => x.Name).ToArray();
}
