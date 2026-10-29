using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Application;

/// <summary>Fixed capacity chronological ring. Late backfill merges without replacing newer bars.</summary>
public sealed class RollingWindow(int capacity)
{
    private Candle?[] _items = new Candle?[capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity))];
    private int _start;
    public int Count { get; private set; }
    public int Capacity => _items.Length;

    public void Add(Candle value)
    {
        if (!value.IsValid) return;
        if (Count == 0 || value.OpenTime > _items[(_start + Count - 1) % Capacity]!.OpenTime)
        {
            if (Count == Capacity) { _items[_start] = value; _start = (_start + 1) % Capacity; }
            else { _items[(_start + Count) % Capacity] = value; Count++; }
            return;
        }
        // Backfills arrive rarely and in chronological batches. Normal streaming stays O(1).
        var ordered = Snapshot().ToDictionary(x => x.OpenTime);
        if (!ordered.TryGetValue(value.OpenTime, out var old) || value.SourceTime >= old.SourceTime) ordered[value.OpenTime] = value;
        var merged = ordered.Values.OrderBy(x => x.OpenTime).TakeLast(Capacity).ToArray();
        Array.Clear(_items); _start = 0; Count = merged.Length;
        Array.Copy(merged, _items, Count);
    }
    public Candle[] Snapshot() => Enumerable.Range(0, Count).Select(i => _items[(_start + i) % Capacity]!).ToArray();
    public void AddRange(IEnumerable<Candle> values)
    {
        var merged = Snapshot().Concat(values.Where(x => x.IsValid)).GroupBy(x => x.OpenTime)
            .Select(g => g.MaxBy(x => x.SourceTime)!).OrderBy(x => x.OpenTime).TakeLast(Capacity).ToArray();
        Array.Clear(_items); _start = 0; Count = merged.Length; Array.Copy(merged, _items, Count);
    }
}
