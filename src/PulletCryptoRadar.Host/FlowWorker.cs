using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;

namespace PulletCryptoRadar.Host;

public sealed class FlowWorker(OnChainService chain,FlowService service,IFlowRepository repository,IFlowPersistence persistence,IFlowProvider provider,
    IWatchRepository watches,FlowOptions options,PersistenceQueue queue,IProviderHealthStore health,TimeProvider time,ILogger<FlowWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await queue.Ready.Task.WaitAsync(ct);await chain.RestoreAsync(ct);
        while(!ct.IsCancellationRequested)
        {
            try
            {
                service.Refresh();
                var watchIds=watches.Configs().Where(w=>w.Enabled).Select(w=>w.AssetId).ToHashSet();
                // This phase runs a single BSC pilot; no unbounded scan of every listed token.
                var target=chain.Overview().Where(v=>v.Binding.Chain=="bsc"&&v.Features.TrustedIdentity&&watchIds.Contains(v.Binding.AssetId)).OrderBy(v=>v.Binding.AssetId).FirstOrDefault();
                if(options.Enabled&&target is not null)
                {
                    var b=target.Binding;var now=time.GetUtcNow();var prior=repository.Attempt(b.Chain,b.ContractAddress);
                    if(prior?.Halted!=true&&!(prior?.NextAttempt>now))
                    {
                        var attempt=new FlowAttempt(b.Chain,b.ContractAddress,now.AddSeconds(options.PollSeconds),"RPC 请求已预留，等待完整区块范围返回");
                        if(await persistence.ReserveAsync(attempt,now,160,options.HourRequestBudget,options.DayRequestBudget,ct))
                        {
                            try
                            {
                                var inspected=repository.Inspections(b.Chain);var addresses=(target.Sample?.Holders??[]).Take(10).Select(h=>h.Address)
                                    .Where(a=>!inspected.Any(i=>i.Address==a&&now-i.CheckedAt<TimeSpan.FromHours(24))).ToArray();
                                var batch=await provider.FetchAsync(b,repository.Cursor(b.Chain,b.ContractAddress),addresses,ct);
                                await persistence.SaveAsync(batch,attempt with{Error=null},ct);health.Success("BscFlows",freshnessSeconds:options.PollSeconds+120);
                            }
                            catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
                            catch(Exception ex)
                            {
                                var message=ex is FlowReorgException or InvalidDataException or OnChainProviderException?ex.Message:ex is HttpRequestException or TaskCanceledException?"RPC 网络采集未完成；已保存断点不前移":"转账处理未完成；已保存断点不前移";
                                await persistence.FailAsync(attempt with{Error=message,Halted=ex is FlowReorgException,NextAttempt=ex is OnChainProviderException p?p.RetryAt:time.GetUtcNow().AddMinutes(15)},ct);
                                health.Failure("BscFlows",message);logger.LogWarning("BSC flow {Type}: {Reason}",ex.GetType().Name,message);
                            }
                        }
                        else health.Failure("BscFlows","RPC 预留或小时/日预算限制，等待下一窗口，不增加请求");
                    }
                }
                service.Refresh();
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
            catch(Exception ex){health.Failure("BscFlows","转账调度未完成，保留缓存");logger.LogWarning("BSC flow scheduler: {Type}",ex.GetType().Name);}
            await Task.Delay(TimeSpan.FromSeconds(30),time,ct);
        }
    }
}
