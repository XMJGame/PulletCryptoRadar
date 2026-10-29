using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Infrastructure;

public sealed class MarketEventWorker(IMarketEventBus bus, IMarketRuntimeStore runtime, IDerivativeRuntimeStore derivatives) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var e in bus.ReadAllAsync(stoppingToken)) { runtime.Apply(e); derivatives.Apply(e); }
    }
}

public sealed class FeatureWorker(IMarketRuntimeStore runtime, FeatureEngine engine, IProviderHealthStore health,
    TimeProvider time, ILogger<FeatureWorker> logger, IDerivativeRuntimeStore derivatives, DerivativeFeatureEngine derivativeEngine,
    ICapitalRuntimeStore capital, CapitalFeatureEngine capitalEngine,IOnChainFeatureSource? onChain=null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var now = time.GetUtcNow();
                var calculated = runtime.GetAll().Select(m => (Market: m, Features: engine.Calculate(m,
                    runtime.GetCandles(m.Instrument.AssetId, 1), runtime.GetCandles(m.Instrument.AssetId, 5), runtime.GetCandles(m.Instrument.AssetId, 15), now))).ToArray();
                var crossSection = calculated.Where(x => x.Features.Returns[5].Status == DataStatus.Valid)
                    .Select(x => (double)x.Features.Returns[5].Value!.Value).ToArray();
                foreach (var (market, original) in calculated)
                {
                    var features = original;
                    if (features.NewListing)
                    {
                        var ret = features.Returns[5];
                        var baseline = calculated.Where(x => x.Market.Instrument.AssetId != market.Instrument.AssetId && x.Features.Returns[5].Status == DataStatus.Valid)
                            .Select(x => (double)x.Features.Returns[5].Value!.Value).ToArray();
                        var percentile = ret.Status == DataStatus.Valid && crossSection.Length >= 30 ? RobustStatistics.Percentile((double)ret.Value!.Value, baseline) : null;
                        features = features with { PricePercentile = new(percentile, percentile is null ? DataStatus.Missing : DataStatus.Valid, ret.SourceTime, "CrossSection5mReturn"), PercentileBasis = "CrossSection" };
                    }
                    features = features with { Derivatives = derivativeEngine.Calculate(market with { Features = features }, derivatives.Get(market.Instrument.Symbol), now) };
                    features = features with { Capital = capitalEngine.Calculate(market with { Features = features }, capital.Get(market.Instrument.AssetId), now) };
                    features = features with { OnChain = onChain?.Features(market.Instrument.AssetId,now)??new() };
                    runtime.SetFeatures(market.Instrument.AssetId, features);
                }
                health.Success("Feature Engine", freshnessSeconds: 30);
            }
            catch (Exception ex) { health.Failure("Feature Engine", ex.Message); logger.LogError(ex, "Feature calculation failed"); }
        }
    }
}

public sealed class SnapshotWorker(IMarketRuntimeStore runtime, IProviderHealthStore health, PersistenceQueue queue,
    TimeProvider time, ILogger<SnapshotWorker> logger) : BackgroundService
{
    private readonly Dictionary<(int, int), HashSet<DateTimeOffset>> _saved = [];
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await queue.Ready.Task.WaitAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), time);
        DateOnly? lastMaintenance = null;
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var previous = _saved.ToDictionary(x => x.Key, x => new HashSet<DateTimeOffset>(x.Value));
            try
            {
            var rows = new List<(int, Candle, decimal?, decimal?)>();
            foreach (var m in runtime.GetAll())
            {
                var id = m.Instrument.AssetId;
                var five = runtime.GetCandles(id, 5);
                Add(id, five, m.Volume24h, m.FundingRate, rows);
                var hours = five.GroupBy(x => x.OpenTime.ToUnixTimeSeconds() / 3600).Select(g => g.OrderBy(x => x.OpenTime).ToArray())
                    .Where(a => a.Length == 12 && a[0].OpenTime.Minute == 0 && FeatureEngine.Continuous(a))
                    .Select(a => new Candle(a[0].Symbol, 60, a[0].OpenTime, a[0].Open, a.Max(x => x.High), a.Min(x => x.Low),
                        a[^1].Close, a.Sum(x => x.QuoteVolume), a[^1].SourceTime, a.Max(x => x.ReceivedTime))).ToArray();
                Add(id, hours, null, null, rows);
            }
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await queue.Channel.Writer.WriteAsync(new SaveSnapshotsCommand(rows, health.GetAll(), completion), stoppingToken);
            await completion.Task.WaitAsync(stoppingToken);
            var now = time.GetUtcNow();
            var local = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(now, "China Standard Time");
            var date = DateOnly.FromDateTime(local.DateTime);
            if (local.TimeOfDay >= TimeSpan.FromHours(4.5) && lastMaintenance != date)
            {
                await queue.Channel.Writer.WriteAsync(new MaintenanceCommand(now), stoppingToken);
                lastMaintenance = date;
            }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _saved.Clear(); foreach (var entry in previous) _saved[entry.Key] = entry.Value;
                logger.LogError(ex, "Snapshot persistence failed; retained rows will retry on the next interval");
            }
        }
    }
    private void Add(int id, IReadOnlyList<Candle> bars, decimal? volume, decimal? funding, List<(int, Candle, decimal?, decimal?)> rows)
    {
        if (bars.Count == 0) return;
        var key = (id, bars[0].IntervalMinutes);
        var known = _saved.GetValueOrDefault(key) ?? [];
        foreach (var bar in bars.Where(x => !known.Contains(x.OpenTime)))
            // Historical candles must not be labeled with today's volume or funding.
            rows.Add((id, bar, bar == bars[^1] ? volume : null, bar == bars[^1] ? funding : null));
        // Exact timestamp tracking also persists repaired holes inside the historical range.
        // Drop timestamps outside the rolling window to bound cursor memory.
        _saved[key] = bars.Select(x => x.OpenTime).ToHashSet();
    }
}
