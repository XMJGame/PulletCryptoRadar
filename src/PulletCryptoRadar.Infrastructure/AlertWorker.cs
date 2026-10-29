using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Scanner;

namespace PulletCryptoRadar.Infrastructure;

public sealed class AlertWorker(AlertService service, PersistenceQueue queue, AlertOptions options, IProviderHealthStore health,
    TimeProvider time, ILogger<AlertWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await queue.Ready.Task.WaitAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await service.TickAsync(stoppingToken); health.Success("Alert Policy", freshnessSeconds: options.WorkerIntervalSeconds * 3 + 10); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { health.Failure("Alert Policy", ex.Message); logger.LogError(ex, "Alert policy or outbox processing failed"); }
            await Task.Delay(TimeSpan.FromSeconds(options.WorkerIntervalSeconds), time, stoppingToken);
        }
    }
}
