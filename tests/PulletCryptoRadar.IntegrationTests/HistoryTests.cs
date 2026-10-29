using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;

namespace PulletCryptoRadar.IntegrationTests;

public sealed class HistoryTests
{
    [Fact]
    public async Task RestartBackfillRepairsInteriorGapAndNewestGap()
    {
        var now = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var bars = Enumerable.Range(0, 100).Select(i => new Candle("BTCUSDT", 1, now.AddMinutes(-100 + i), 100, 102, 99, 101,
            1000, now.AddMinutes(-99 + i).AddMilliseconds(-1), now)).ToArray();
        var store = new MarketRuntimeStore(); var instrument = new Instrument(1, "BTCUSDT", "BTC", now.AddDays(-30), true);
        store.Register([instrument]);
        // Simulate old persisted data plus a live bar received after restart.
        store.Apply(new HistoryEvent("BTCUSDT", bars.Take(40).Concat(bars.Skip(80).Take(15)).ToArray(), now));
        var rest = new CandleProvider(bars); var bus = new ApplyingBus(store);
        await new MarketHistoryLoader(rest, bus, store, new FixedTime(now)).FillAsync(instrument, 1, 100, CancellationToken.None);
        var actual = store.GetCandles(1, 1);
        Assert.Equal(100, actual.Count); Assert.True(FeatureEngine.Continuous(actual));
        Assert.Equal(bars[^1].OpenTime, actual[^1].OpenTime);
        Assert.Contains(rest.Requests, x => x.Limit == 40); // Internal hole, not merely the recent five bars.
    }
    [Fact]
    public async Task NewListingStopsAtOnboardTimeRatherThanLoopingForever()
    {
        var now = DateTimeOffset.UtcNow;
        var instrument = new Instrument(1, "NEWUSDT", "NEW", now.AddMinutes(-2), true);
        var store = new MarketRuntimeStore(); store.Register([instrument]); var rest = new CandleProvider([]);
        await new MarketHistoryLoader(rest, new ApplyingBus(store), store, new FixedTime(now)).FillAsync(instrument, 1, 2880, CancellationToken.None);
        Assert.InRange(rest.Requests.Count, 1, 2); Assert.Empty(store.GetCandles(1, 1));
    }
    private sealed class ApplyingBus(IMarketRuntimeStore store) : IMarketEventBus
    {
        public ValueTask PublishAsync(MarketEvent e, CancellationToken ct) { store.Apply(e); return ValueTask.CompletedTask; }
        public IAsyncEnumerable<MarketEvent> ReadAllAsync(CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    private sealed class CandleProvider(Candle[] bars) : IMarketRestProvider
    {
        public List<(int Limit, DateTimeOffset? End)> Requests = [];
        public Task<IReadOnlyList<Candle>> GetCandlesAsync(string symbol, int intervalMinutes, int limit, DateTimeOffset? endTime, CancellationToken ct)
        {
            Requests.Add((limit, endTime));
            return Task.FromResult<IReadOnlyList<Candle>>(bars.Where(x => x.OpenTime <= endTime).TakeLast(limit).ToArray());
        }
        public Task<IReadOnlyList<SymbolDefinition>> GetSymbolsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<TickerEvent>> GetTickersAsync(CancellationToken ct) => throw new NotSupportedException();
    }
}
