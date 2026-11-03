using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;

namespace PulletCryptoRadar.Host;

public sealed class OnChainWorker(OnChainService service,IOnChainProvider provider,IWatchRepository watches,PersistenceQueue queue,
    OnChainOptions options,IProviderHealthStore health,TimeProvider time,ILogger<OnChainWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await queue.Ready.Task.WaitAsync(ct);await service.RestoreAsync(ct);if(!options.Enabled)return;
        while(!ct.IsCancellationRequested)
        {
            try
            {
                if(options.SeedAriaReference)await service.SeedAsync(ct);
                var watchIds=watches.Configs().Where(w=>w.Enabled).Select(w=>w.AssetId).ToHashSet();
                foreach(var binding in service.Bindings().Where(b=>b.Enabled&&watchIds.Contains(b.AssetId)))
                    await service.PromoteWatchAsync(binding,options.WatchMinutes,ct);
                foreach(var binding in service.DueBindings(watchIds,time.GetUtcNow(),options.MaximumTargets))
                {
                    var now=time.GetUtcNow();if(!service.Due(binding,now))continue;
                    var next=now+(watchIds.Contains(binding.AssetId)?TimeSpan.FromMinutes(options.WatchMinutes):TimeSpan.FromHours(options.CandidateHours));
                    var attempt=new ChainAttempt(binding.Chain,binding.ContractAddress,now,next,"采集已预留，等待返回");
                    if(!await service.ReserveAsync(attempt,options,ct))
                    {
                        health.Failure("OnChain","公开接口预算或已预留调度限制；等待下一可用窗口，不增加请求");continue;
                    }
                    try
                    {
                        var sample=await provider.FetchAsync(binding,ct);
                        var retry=sample.Security?.RetryAt;
                        await service.SaveSampleAsync(new(sample,attempt with{Error=sample.Security?.Error,NextAttempt=retry>attempt.NextAttempt?retry.Value:attempt.NextAttempt}),ct);
                        if(sample.Security?.Error is {} error)health.Failure("OnChain",error);
                        else health.Success("OnChain",freshnessSeconds:options.WatchMinutes*60+300);
                    }
                    catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
                    catch(Exception ex)
                    {
                        var message=ex is OnChainProviderException or InvalidDataException?ex.Message:"链上网络采集未完成，已保留缓存";
                        var retry=ex is OnChainProviderException p?p.RetryAt:time.GetUtcNow().AddMinutes(30);
                        await service.SaveSampleAsync(new(null,attempt with{NextAttempt=retry,Error=message}),ct);
                        health.Failure("OnChain",message);logger.LogWarning("OnChain {Chain}: {Error}",binding.Chain,message);
                    }
                }
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
            catch(Exception ex){health.Failure("OnChain", "链上调度失败，等待重试");logger.LogWarning("OnChain scheduler: {Type}",ex.GetType().Name);}
            await Task.Delay(TimeSpan.FromSeconds(30),time,ct);
        }
    }
}
