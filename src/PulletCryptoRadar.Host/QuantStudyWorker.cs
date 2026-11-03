using PulletCryptoRadar.Application;
using PulletCryptoRadar.Infrastructure;
namespace PulletCryptoRadar.Host;
public sealed class QuantStudyWorker(QuantStudyService service,PersistenceQueue queue,ILogger<QuantStudyWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await queue.Ready.Task.WaitAsync(ct);service.ResumeIncomplete();
        await foreach(var request in service.ReadJobs(ct))
        {
            try{await service.ProcessAsync(request,ct);}
            catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
            catch(Exception ex){logger.LogWarning("Quant study persistence: {Type}",ex.GetType().Name);}
        }
    }
}
