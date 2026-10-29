using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;

namespace PulletCryptoRadar.Host;

public sealed class PaperWorker(PaperService service,PersistenceQueue queue,IProviderHealthStore health,TimeProvider time,ILogger<PaperWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await queue.Ready.Task.WaitAsync(ct);
        while(!ct.IsCancellationRequested)
        {
            try{await service.TickAsync(ct);health.Success("Paper",freshnessSeconds:20);}
            catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
            catch(Exception ex){health.Failure("Paper","模拟账本本轮未提交，禁止发布虚假成交");logger.LogWarning("Paper ledger: {Type}",ex.GetType().Name);}
            await Task.Delay(TimeSpan.FromSeconds(5),time,ct);
        }
    }
}
