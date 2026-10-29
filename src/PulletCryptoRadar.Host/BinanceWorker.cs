using Microsoft.Extensions.Options;
using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;
using PulletCryptoRadar.Providers.Binance;

namespace PulletCryptoRadar.Host;

public sealed class BinanceWorker(IMarketRestProvider rest, IPriorityQuoteProvider priorityQuotes, IMarketStreamProvider stream, IMarketEventBus bus,
    IMarketRuntimeStore runtime, PersistenceQueue persistence, AssetRepository repository, IProviderHealthStore health,
    IOptions<BinanceOptions> options, IOptions<NetworkOptions> network, TimeProvider time, MarketHistoryLoader historyLoader, ILogger<BinanceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        await persistence.Ready.Task.WaitAsync(stoppingToken);
        runtime.Register(await repository.ReadInstrumentsAsync(stoppingToken));
        foreach (var instrument in runtime.GetAll())
        {
            var candles = await repository.ReadCandlesAsync(instrument.Instrument.AssetId, 5, 2016, stoppingToken);
            await bus.PublishAsync(new HistoryEvent(instrument.Instrument.Symbol, candles, time.GetUtcNow()), stoppingToken);
        }
        await Task.WhenAll(SymbolLoop(stoppingToken), TickerLoop(stoppingToken), HistoryLoop(stoppingToken));
    }
    private async Task SymbolLoop(CancellationToken ct)
    {
        using var streamLifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        CancellationTokenSource? generation = null;
        Task? streamTask = null;
        var currentSymbols = "";
        try
        {
            var cached = runtime.GetAll().Select(x => x.Instrument.Symbol).Order().ToArray();
            if (cached.Length > 0 && !network.Value.EconomyMode)
            {
                generation = CancellationTokenSource.CreateLinkedTokenSource(streamLifetime.Token);
                streamTask = stream.RunAsync(cached, generation.Token);
                currentSymbols = string.Join(',', cached);
            }
            while (!ct.IsCancellationRequested)
            {
                var delay = TimeSpan.FromDays(1);
                try
                {
                    var symbols = await rest.GetSymbolsAsync(ct);
                    if (symbols.Count == 0) throw new InvalidDataException("ExchangeInfo returned no active USDT perpetuals");
                    var instruments = await persistence.SyncAsync(symbols, ct);
                    runtime.Register(instruments);
                    var active = instruments.Where(x => x.Active).Select(x => x.Symbol).Order().ToArray();
                    var key = string.Join(',', active);
                    if (key != currentSymbols && !network.Value.EconomyMode)
                    {
                        if (generation is not null) await generation.CancelAsync();
                        if (streamTask is not null) { try { await streamTask; } catch (OperationCanceledException) { } }
                        generation?.Dispose(); generation = CancellationTokenSource.CreateLinkedTokenSource(streamLifetime.Token);
                        streamTask = stream.RunAsync(active, generation.Token); currentSymbols = key;
                    }
                    health.Success("Symbol Sync", freshnessSeconds: 90000);
                    logger.LogInformation("Synchronized {Count} active USDT perpetual instruments", active.Length);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex) { health.Failure("Symbol Sync", ex.Message); logger.LogWarning(ex, "Symbol synchronization failed"); delay = TimeSpan.FromMinutes(1); }
                await Task.Delay(delay, time, ct);
            }
        }
        finally
        {
            if (generation is not null) await generation.CancelAsync();
            if (streamTask is not null) { try { await streamTask; } catch (OperationCanceledException) { } }
            generation?.Dispose();
        }
    }
    private async Task TickerLoop(CancellationToken ct)
    {
        var nextMarket=DateTimeOffset.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if(time.GetUtcNow()>=nextMarket)
                {
                    foreach (var ticker in await rest.GetTickersAsync(ct)) await bus.PublishAsync(ticker, ct);
                    nextMarket=time.GetUtcNow().AddSeconds(60);
                }
                if(network.Value.EconomyMode)
                    await Parallel.ForEachAsync(runtime.GetAll().Where(a=>a.Instrument.Active&&network.Value.PrioritySymbols.Contains(a.Instrument.Symbol)),
                        new ParallelOptions{CancellationToken=ct,MaxDegreeOfParallelism=2},async(asset,token)=>
                        {
                            try { foreach(var e in await priorityQuotes.GetPriorityQuotesAsync(asset.Instrument.Symbol,token))await bus.PublishAsync(e,token); }
                            catch(OperationCanceledException)when(token.IsCancellationRequested){}
                            catch(Exception ex){logger.LogWarning("Priority quote {Symbol}: {Reason}",asset.Instrument.Symbol,ex.Message);}
                        });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning("REST ticker fallback failed: {Reason}", ex.Message); }
            await Task.Delay(TimeSpan.FromSeconds(network.Value.EconomyMode ? network.Value.TickerSeconds : 60), time, ct);
        }
    }
    private async Task HistoryLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var assets = runtime.GetAll().Where(x=>x.Instrument.Active).OrderByDescending(x=>network.Value.PrioritySymbols.Contains(x.Instrument.Symbol)).ThenByDescending(x => x.Volume24h ?? 0).Take(network.Value.EconomyMode ? network.Value.HistorySymbols : int.MaxValue).ToArray();
            if (assets.Length == 0) { await Task.Delay(TimeSpan.FromSeconds(2), time, ct); continue; }
            // First give the entire market a usable baseline, then fill the complete retention windows.
            foreach (var quick in network.Value.EconomyMode ? new[] { true } : new[] { true, false })
                await Parallel.ForEachAsync(assets, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = options.Value.HistoryConcurrency }, async (asset, token) =>
                {
                    try
                    {
                        foreach (var interval in new[] { 1, 5, 15 })
                        {
                            var target = network.Value.EconomyMode ? interval switch { 1=>240, 5=>288, _=>192 } : quick ? interval == 1 ? 65 : 40 : interval switch
                            { 1 => options.Value.MinuteHistoryCount, 5 => options.Value.FiveMinuteHistoryCount, _ => options.Value.FifteenMinuteHistoryCount };
                            await historyLoader.FillAsync(asset.Instrument, interval, target, token);
                        }
                        health.Success("History Backfill", freshnessSeconds: 900);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                    catch (Exception ex) { health.Failure("History Backfill", ex.Message); logger.LogWarning("History backfill {Symbol}: {Reason}", asset.Instrument.Symbol, ex.Message); }
                });
            await Task.Delay(TimeSpan.FromMinutes(5), time, ct);
        }
    }
}
