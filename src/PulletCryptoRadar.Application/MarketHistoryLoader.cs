using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Application;

public sealed class MarketHistoryLoader(IMarketRestProvider rest, IMarketEventBus bus, IMarketRuntimeStore runtime, TimeProvider time)
{
    public async Task FillAsync(Instrument instrument, int interval, int target, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var expected = DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds() / (interval * 60) * (interval * 60));
        var existing = runtime.GetCandles(instrument.AssetId, interval);
        var latest = existing.LastOrDefault();
        var merged = existing.ToDictionary(x => x.OpenTime);
        // Persisted history and a newly received live bar can enclose an internal gap after restart.
        // Repair those holes explicitly; fetching only before the oldest bar would never fill them.
        foreach (var pair in existing.Zip(existing.Skip(1)).Where(p => p.First.EndTime < p.Second.OpenTime))
        {
            var gapEnd = pair.Second.OpenTime.AddMilliseconds(-1);
            var gapCount = (int)((pair.Second.OpenTime - pair.First.EndTime).TotalMinutes / interval);
            while (gapCount > 0)
            {
                var page = await rest.GetCandlesAsync(instrument.Symbol, interval, Math.Min(499, gapCount), gapEnd, ct);
                if (page.Count == 0) break;
                foreach (var bar in page) merged[bar.OpenTime] = bar;
                gapEnd = page[0].OpenTime.AddMilliseconds(-1); gapCount -= page.Count;
            }
        }
        // Repair the newest gap (including disconnects), then backfill older history if needed.
        var needed = latest is null ? target : (int)Math.Ceiling((expected - latest.EndTime).TotalMinutes / interval);
        DateTimeOffset? end = expected.AddMilliseconds(-1);
        while (needed > 0)
        {
            var page = await rest.GetCandlesAsync(instrument.Symbol, interval, Math.Min(499, Math.Max(needed, 2)), end, ct);
            if (page.Count == 0) break;
            foreach (var bar in page) merged[bar.OpenTime] = bar;
            end = page[0].OpenTime.AddMilliseconds(-1);
            needed -= page.Count;
            if (latest is not null && page[0].EndTime <= latest.EndTime) break;
        }
        // Publish recent data before the deeper backfill so the UI warms up promptly.
        await bus.PublishAsync(new HistoryEvent(instrument.Symbol, merged.Values.OrderBy(x => x.OpenTime).ToArray(), now), ct);
        end = merged.Count == 0 ? expected.AddMilliseconds(-1) : merged.Values.Min(x => x.OpenTime).AddMilliseconds(-1);
        while (merged.Count < target && end >= instrument.OnboardTime)
        {
            var page = await rest.GetCandlesAsync(instrument.Symbol, interval, Math.Min(499, target - merged.Count), end, ct);
            if (page.Count == 0) break;
            foreach (var bar in page) merged[bar.OpenTime] = bar;
            end = page[0].OpenTime.AddMilliseconds(-1);
        }
        await bus.PublishAsync(new HistoryEvent(instrument.Symbol, merged.Values.OrderBy(x => x.OpenTime).ToArray(), now), ct);
    }
}
