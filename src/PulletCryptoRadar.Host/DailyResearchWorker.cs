using PulletCryptoRadar.Application;
using PulletCryptoRadar.Infrastructure;

namespace PulletCryptoRadar.Host;
public sealed class DailyResearchWorker(DailyResearchService service,PersistenceQueue queue,ILogger<DailyResearchWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await queue.Ready.Task.WaitAsync(ct);
        while(!ct.IsCancellationRequested)
        {
            try{service.CaptureToday();}catch(Exception ex)when(ex is not OperationCanceledException){logger.LogWarning("Daily research capture failed: {Type}; existing archive retained",ex.GetType().Name);}
            await Task.Delay(TimeSpan.FromMinutes(15),ct);
        }
    }
}
