using System.Diagnostics;
using Microsoft.Extensions.Options;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;
using PulletCryptoRadar.Providers.Binance;
using PulletCryptoRadar.Application;

namespace PulletCryptoRadar.Host;

public sealed class DerivativeWorker(IDerivativeRestProvider provider, IMarketRuntimeStore market, IMarketEventBus bus,
    PersistenceQueue queue, DerivativeRepository repository, IDerivativeRuntimeStore runtime, IOptions<DerivativeOptions> options,
    IOptions<BinanceOptions> binance, IOptions<NetworkOptions> network, IProviderHealthStore health, TimeProvider time, PaperService paper, ILogger<DerivativeWorker> logger) : BackgroundService
{
    private readonly HashSet<string> _restored = [];
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled || !binance.Value.Enabled) return;
        await queue.Ready.Task.WaitAsync(stoppingToken);
        await Task.WhenAll(OpenInterestLoop(stoppingToken), DeepLoop(stoppingToken));
    }
    private async Task OpenInterestLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var start = Stopwatch.StartNew();var state=(await paper.OverviewAsync(ct)).State;
            var assets=(network.Value.EconomyMode?PaperUniverse.Warmup(market.GetAll(),state,time.GetUtcNow(),network.Value.HistorySymbols):market.GetAll().Where(m=>m.Instrument.Active)).ToArray();
            if (assets.Length == 0) { await Task.Delay(TimeSpan.FromSeconds(2), time, ct); continue; }
            await Parallel.ForEachAsync(assets.OrderByDescending(x => Priority(x.Instrument.Symbol)).ThenByDescending(x => x.Volume24h),
                new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = options.Value.Concurrency }, async (asset, token) =>
            {
                try
                {
                    var point = await provider.GetOpenInterestAsync(asset.Instrument.Symbol, token);
                    await bus.PublishAsync(new DerivativeEvent(point.Symbol, [point], time.GetUtcNow()), token);
                    health.Success("Open Interest", freshnessSeconds: options.Value.PollSeconds + 120);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception ex) { health.Failure("Open Interest", ex.Message); logger.LogWarning("OI {Symbol}: {Error}", asset.Instrument.Symbol, ex.Message); }
            });
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.Value.PollSeconds - start.Elapsed.TotalSeconds)), time, ct);
        }
    }
    private async Task DeepLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var start = Stopwatch.StartNew();
            var state=(await paper.OverviewAsync(ct)).State;
            var candidates = (network.Value.EconomyMode?PaperUniverse.Warmup(market.GetAll(),state,time.GetUtcNow(),network.Value.HistorySymbols):market.GetAll().Where(m=>m.Instrument.Active).OrderByDescending(m=>Priority(m.Instrument.Symbol)).ThenByDescending(m=>Math.Abs(m.Features.PriceRobustZ5m.Value??0)+Math.Max(0,m.Features.VolumeRobustZ.Value??0)).Take(options.Value.DeepCandidateCount))
                .OrderByDescending(m=>state.Positions.Any(p=>p.Symbol==m.Instrument.Symbol)).ToArray();
            var detailed=candidates.Take(20).Select(m=>m.Instrument.Symbol).ToHashSet(StringComparer.Ordinal);
            if (candidates.Length == 0) { await Task.Delay(TimeSpan.FromSeconds(2), time, ct); continue; }
            // Rehydrate each selected symbol once; a new source time is never invented for restored data.
            foreach (var candidate in candidates)
                if (!_restored.Contains(candidate.Instrument.Symbol))
                {
                    try
                    {
                        runtime.Apply(new DerivativeEvent(candidate.Instrument.Symbol, repository.Read(candidate.Instrument.Symbol), time.GetUtcNow()));
                        _restored.Add(candidate.Instrument.Symbol);
                    }
                    catch (Exception ex) { health.Failure("Derivative Restore", ex.Message); logger.LogWarning(ex, "Cannot restore {Symbol}; fetching fresh data", candidate.Instrument.Symbol); }
                }
            await Parallel.ForEachAsync(candidates, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = options.Value.Concurrency }, async (asset, token) =>
            {
                foreach (var kind in new[] { DerivativeKind.OpenInterest5m, DerivativeKind.GlobalLongShort5m, DerivativeKind.TopAccount5m, DerivativeKind.TopPosition5m, DerivativeKind.Taker5m })
                {
                    if(network.Value.EconomyMode&&kind!=DerivativeKind.OpenInterest5m&&!detailed.Contains(asset.Instrument.Symbol))continue;
                    try
                    {
                        var points = await provider.GetSeriesAsync(asset.Instrument.Symbol, kind, network.Value.EconomyMode ? 35 : options.Value.HistoryLimit, token);
                        await bus.PublishAsync(new DerivativeEvent(asset.Instrument.Symbol, points, time.GetUtcNow()), token);
                        if (points.Count > 0) health.Success($"Deep {kind}", freshnessSeconds: options.Value.PollSeconds + 180);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                    catch (Exception ex) { health.Failure($"Deep {kind}", ex.Message); logger.LogWarning("Deep {Symbol} {Kind}: {Error}", asset.Instrument.Symbol, kind, ex.Message); }
                }
            });
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.Value.PollSeconds - start.Elapsed.TotalSeconds)), time, ct);
        }
    }
    private bool Priority(string symbol) => options.Value.PrioritySymbols.Contains(symbol, StringComparer.Ordinal) || network.Value.PrioritySymbols.Contains(symbol, StringComparer.Ordinal);
}
