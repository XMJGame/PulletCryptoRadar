using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Providers.Binance;

// Serialized, weighted pacing prevents bursts; a provider-wide cooldown honors Retry-After.
public sealed class RequestLimiter(IOptions<BinanceOptions> options, TimeProvider time, IProviderHealthStore? health = null) : IRequestLimiter
{
    private sealed class LimitState { public readonly SemaphoreSlim Gate = new(1, 1); public DateTimeOffset Next; }
    private readonly ConcurrentDictionary<string, LimitState> _states = new();
    private readonly object _pauseGate = new();
    private DateTimeOffset _pausedUntil;
    public async Task WaitAsync(string provider, int weight, CancellationToken ct)
    {
        var state = _states.GetOrAdd(provider, _ => new());
        await state.Gate.WaitAsync(ct);
        try
        {
            while (true)
            {
                DateTimeOffset until;
                lock (_pauseGate) until = state.Next > _pausedUntil ? state.Next : _pausedUntil;
                var delay = until - time.GetUtcNow();
                if (delay <= TimeSpan.Zero) break;
                await Task.Delay(delay, time, ct);
            }
            var capacity = provider == "BinanceStatistics" ? options.Value.StatisticsRequestsPerMinute : provider=="BinanceFunding"?90:options.Value.RequestWeightPerMinute;
            lock (_pauseGate)
            {
                if (_pausedUntil != default && _pausedUntil <= time.GetUtcNow())
                {
                    _pausedUntil = default;
                    health?.Success("Binance Cooldown", freshnessSeconds: int.MaxValue);
                }
            }
            state.Next = time.GetUtcNow().AddMinutes((double)Math.Max(weight, 1) / capacity);
        }
        finally { state.Gate.Release(); }
    }
    public void Pause(string provider, TimeSpan duration)
    {
        lock (_pauseGate)
        {
            _pausedUntil = new[] { _pausedUntil, time.GetUtcNow() + duration }.Max();
            health?.Failure("Binance Cooldown", $"REST 冷却至 UTC {_pausedUntil:O}；等待交易所恢复。实时流独立运行。");
        }
    }
}
