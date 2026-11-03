using PulletCryptoRadar.Application;
using PulletCryptoRadar.Infrastructure;
namespace PulletCryptoRadar.Host;
public sealed class QuantDailyWorker(QuantDailyService service,PersistenceQueue queue,ILogger<QuantDailyWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await queue.Ready.Task.WaitAsync(ct);
        while(!ct.IsCancellationRequested)
        {
            try{await service.CaptureAsync(ct);}catch(Exception ex)when(ex is not OperationCanceledException){logger.LogWarning("Quant daily capture failed: {Type}; account execution is independent",ex.GetType().Name);}
            await Task.Delay(TimeSpan.FromMinutes(5),ct);
        }
    }
}
