using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Scanner;

namespace PulletCryptoRadar.Infrastructure;

public sealed class WatchWorker(WatchService service,PersistenceQueue queue,WatchOptions options,IProviderHealthStore health,TimeProvider time,ILogger<WatchWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await queue.Ready.Task.WaitAsync(ct);
        while(!ct.IsCancellationRequested)
        {
            try{await service.TickAsync(ct);health.Success("WatchTower",freshnessSeconds:options.IntervalSeconds*3);}
            catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
            catch(Exception ex){health.Failure("WatchTower",ex.Message);logger.LogWarning(ex,"WatchTower evaluation failed");}
            await Task.Delay(TimeSpan.FromSeconds(options.IntervalSeconds),time,ct);
        }
    }
}
