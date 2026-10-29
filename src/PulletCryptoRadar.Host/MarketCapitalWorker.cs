using Microsoft.Extensions.Options;
using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;
using PulletCryptoRadar.Providers.Market;

namespace PulletCryptoRadar.Host;

public sealed class MarketCapitalWorker(IMarketCapitalProvider provider, MappingService service, ICapitalRuntimeStore capital,
    IMarketRuntimeStore market, PersistenceQueue queue, IOptions<CoinGeckoOptions> options, IProviderHealthStore health,
    TimeProvider time, ILogger<MarketCapitalWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await queue.Ready.Task.WaitAsync(stoppingToken); await service.RestoreAsync(stoppingToken);
        if (!options.Value.Enabled) return;
        var lastMarketPoll = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (market.GetAll().Count == 0) { await Task.Delay(TimeSpan.FromSeconds(2), time, stoppingToken); continue; }
            try
            {
                if (capital.Catalog is null || time.GetUtcNow() - capital.Catalog.ReceivedTime >= TimeSpan.FromHours(options.Value.CatalogHours))
                {
                    await service.ApplyCatalogAsync(await provider.GetCatalogAsync(stoppingToken), stoppingToken);
                    health.Success("CoinGecko Catalog", freshnessSeconds: options.Value.CatalogHours * 3600 + 300);
                }
                else await service.ReconcileAsync(stoppingToken);
                var requested = service.TakeRefreshRequest();
                if (requested || time.GetUtcNow() - lastMarketPoll >= TimeSpan.FromMinutes(options.Value.PollMinutes))
                {
                    var ids = market.GetAll().Select(m => capital.GetMapping(m.Instrument.AssetId)?.CoinGeckoId).OfType<string>()
                        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                    foreach (var batch in ids.Chunk(options.Value.BatchSize))
                    {
                        var snapshots = await provider.GetMarketsAsync(batch, stoppingToken);
                        await service.SaveMarketsAsync(snapshots, stoppingToken);
                        if (snapshots.Count > 0) health.Success("CoinGecko MarketCap", freshnessSeconds: options.Value.PollMinutes * 60 + 300);
                    }
                    lastMarketPoll = time.GetUtcNow();
                }
                await Task.Delay(TimeSpan.FromMinutes(1), time, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                health.Failure("CoinGecko MarketCap", ex.Message); logger.LogWarning("CoinGecko collection: {Error}", ex.Message);
                await Task.Delay(TimeSpan.FromMinutes(2), time, stoppingToken);
            }
        }
    }
}
