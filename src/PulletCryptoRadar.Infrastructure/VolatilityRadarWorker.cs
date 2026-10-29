using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Scanner;

namespace PulletCryptoRadar.Infrastructure;

public sealed class VolatilityRadarWorker(VolatilityRadarService radar, PersistenceQueue queue, VolatilityOptions options,
    IProviderHealthStore health, TimeProvider time, ILogger<VolatilityRadarWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await queue.Ready.Task.WaitAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var snapshot = await radar.ScanAsync(stoppingToken);
                if (snapshot.ScanId != Guid.Empty) health.Success("Volatility Radar", freshnessSeconds: options.IntervalSeconds * 2 + 15);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { health.Failure("Volatility Radar", ex.Message); logger.LogError(ex, "Volatility scan failed; last committed episode state retained"); }
            await Task.Delay(TimeSpan.FromSeconds(options.IntervalSeconds), time, stoppingToken);
        }
    }
}
