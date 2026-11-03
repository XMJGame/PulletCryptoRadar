using PulletCryptoRadar.Application;
using PulletCryptoRadar.Infrastructure;

namespace PulletCryptoRadar.Host;
public sealed class TokenMonitorWorker(TokenMonitorService service,PersistenceQueue queue,ILogger<TokenMonitorWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await queue.Ready.Task.WaitAsync(ct);
        while(!ct.IsCancellationRequested)
        {
            try{await service.TickAsync(ct);}
            catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
            catch(Exception ex){logger.LogWarning("Token monitor cycle failed: {Type}; saved records retained",ex.GetType().Name);}
            await Task.Delay(TimeSpan.FromSeconds(30),ct);
        }
    }
}
