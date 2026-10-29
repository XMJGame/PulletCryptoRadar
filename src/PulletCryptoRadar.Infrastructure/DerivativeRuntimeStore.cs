using System.Collections.Concurrent;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Infrastructure;

public sealed record DerivativeWriteBatch(IReadOnlyList<DerivativeSample> Samples, IReadOnlyList<LiquidationObservation> Liquidations);
public sealed class DerivativeRuntimeStore : IDerivativeRuntimeStore
{
    private sealed class State
    {
        public readonly object Gate = new();
        public readonly Dictionary<DerivativeKind, SortedDictionary<DateTimeOffset, DerivativeSample>> Series = [];
        public readonly Dictionary<string, LiquidationObservation> Liquidations = [];
        public DateTimeOffset? RetentionStart;
    }
    private readonly ConcurrentDictionary<string, State> _states = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly Dictionary<string, DerivativeSample> _pending = [];
    private readonly Dictionary<string, LiquidationObservation> _pendingLiquidations = [];
    private DateTimeOffset? _coverageStart, _lastActivity;
    private static string Key(DerivativeSample s) => $"{s.Symbol}:{s.Kind}:{s.SourceTime.ToUnixTimeMilliseconds()}";
    public void Apply(MarketEvent marketEvent)
    {
        if (marketEvent is LiquidationCoverageEvent coverage)
        {
            lock (_gate)
            {
                if (coverage.Time < _lastActivity) return;
                _coverageStart = coverage.Connected ? coverage.SessionStart : null;
                _lastActivity = coverage.Time;
            }
            return;
        }
        if (marketEvent is not (DerivativeEvent or LiquidationEvent)) return;
        var state = _states.GetOrAdd(marketEvent.Symbol, _ => new());
        lock (state.Gate)
        {
            if (marketEvent is DerivativeEvent e)
            foreach (var s in e.Samples)
            {
                if (s.Symbol != e.Symbol || s.SourceTime > s.ReceivedTime) continue;
                if (!state.Series.TryGetValue(s.Kind, out var series)) state.Series[s.Kind] = series = [];
                if (series.TryGetValue(s.SourceTime, out var old) && (old.ReceivedTime > s.ReceivedTime || old with { ReceivedTime = s.ReceivedTime } == s)) continue;
                series[s.SourceTime] = s;
                while (series.Count > 2016) series.Remove(series.First().Key);
                lock (_gate) _pending[Key(s)] = s;
            }
            else if (marketEvent is LiquidationEvent l)
            {
                var s = l.Observation;
                if (s.Quantity <= 0 || s.AveragePrice <= 0 || s.SourceTime > s.ReceivedTime) return;
                if (state.Liquidations.TryGetValue(s.Key, out var old) && old.Quantity >= s.Quantity) return;
                state.Liquidations[s.Key] = s;
                foreach (var key in state.Liquidations.Values.Where(x => x.SourceTime < s.SourceTime.AddDays(-1)).Select(x => x.Key).ToArray()) state.Liquidations.Remove(key);
                var cutoff = s.SourceTime.AddDays(-1);
                if (state.Liquidations.Count > 4096)
                {
                    var oldest = state.Liquidations.MinBy(x => x.Value.SourceTime);
                    state.Liquidations.Remove(oldest.Key); cutoff = oldest.Value.SourceTime.AddMilliseconds(1);
                }
                if (state.RetentionStart is null || cutoff > state.RetentionStart) state.RetentionStart = cutoff;
                lock (_gate) _pendingLiquidations[s.Key] = s;
            }
        }
    }
    public DerivativeRuntimeSnapshot Get(string symbol)
    {
        DateTimeOffset? start, last;
        lock (_gate) { start = _coverageStart; last = _lastActivity; }
        if (!_states.TryGetValue(symbol, out var s)) return new(symbol, new Dictionary<DerivativeKind, IReadOnlyList<DerivativeSample>>(), [], start, last);
        lock (s.Gate) return new(symbol, s.Series.ToDictionary(x => x.Key, x => (IReadOnlyList<DerivativeSample>)x.Value.Values.ToArray()),
            s.Liquidations.Values.OrderBy(x => x.SourceTime).ToArray(), s.RetentionStart > start ? s.RetentionStart : start, last);
    }
    public DerivativeWriteBatch Pending()
    {
        lock (_gate) return new(_pending.Values.Take(10000).ToArray(), _pendingLiquidations.Values.Take(5000).ToArray());
    }
    public void Acknowledge(DerivativeWriteBatch batch)
    {
        lock (_gate)
        {
            foreach (var s in batch.Samples) if (_pending.GetValueOrDefault(Key(s)) == s) _pending.Remove(Key(s));
            foreach (var s in batch.Liquidations) if (_pendingLiquidations.GetValueOrDefault(s.Key) == s) _pendingLiquidations.Remove(s.Key);
        }
    }
}
