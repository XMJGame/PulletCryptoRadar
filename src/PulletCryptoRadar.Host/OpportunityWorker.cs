using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;

namespace PulletCryptoRadar.Host;

public sealed class OpportunityWorker(OpportunityService service,PersistenceQueue queue,IProviderHealthStore health,TimeProvider time,ILogger<OpportunityWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await queue.Ready.Task.WaitAsync(ct);
        while(!ct.IsCancellationRequested)
        {
            try{await service.RefreshAsync(ct);health.Success("Opportunity pools",freshnessSeconds:150);}
            catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
            catch(Exception ex){health.Failure("Opportunity pools","本轮分析未完成，保留旧快照及时间");logger.LogWarning("Opportunity pools: {Type}",ex.GetType().Name);}
            await Task.Delay(TimeSpan.FromSeconds(service.Latest()?.Universe.Count>0?60:5),time,ct);
        }
    }
}
