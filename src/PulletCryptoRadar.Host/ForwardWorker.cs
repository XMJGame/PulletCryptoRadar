using PulletCryptoRadar.Application;
using PulletCryptoRadar.Infrastructure;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Host;

public sealed class ForwardWorker(ForwardService service,PersistenceQueue queue,IProviderHealthStore health,TimeProvider time,ILogger<ForwardWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await queue.Ready.Task.WaitAsync(ct);
        while(!ct.IsCancellationRequested)
        {
            try{await service.TickAsync(ct);health.Success("Forward Observation",freshnessSeconds:120);}
            catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
            catch(Exception ex){health.Failure("Forward Observation","本轮前瞻记录未成功提交，不能视为有效观察");logger.LogWarning("Forward observation: {Type}",ex.GetType().Name);}
            await Task.Delay(TimeSpan.FromSeconds(30),time,ct);
        }
    }
}
