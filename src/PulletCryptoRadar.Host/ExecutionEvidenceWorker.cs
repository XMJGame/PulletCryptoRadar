using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;
namespace PulletCryptoRadar.Host;
public sealed class ExecutionEvidenceWorker(ExecutionEvidenceService evidence,PaperService paper,IMarketRuntimeStore market,PersistenceQueue queue,
    TimeProvider time,IProviderHealthStore health,ILogger<ExecutionEvidenceWorker> logger,IQuantShadowTargets? quant=null):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await queue.Ready.Task.WaitAsync(ct);
        while(!ct.IsCancellationRequested)
        {
            try
            {
                var context=await paper.ExecutionContextAsync(ct);await evidence.RefreshAsync(context.State,context.Trades,market.GetAll(),ct,quant?.CostSymbols);
                if(evidence.CollectionHealthy)health.Success("Execution audit",freshnessSeconds:180);
                else health.Failure("Execution audit","部分成本证据未齐或采集失败；本页为影子核验，不能视作成交许可");
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
            catch(Exception ex){health.Failure("Execution audit","成本核验未完成，原账本未改写");logger.LogWarning("Execution evidence: {Type}",ex.GetType().Name);}
            await Task.Delay(TimeSpan.FromSeconds(5),time,ct);
        }
    }
}
