using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;
namespace PulletCryptoRadar.Host;
public sealed class QuantShadowWorker(QuantShadowService service,IPriorityQuoteProvider quotes,IMarketEventBus bus,PersistenceQueue queue,
    TimeProvider time,IProviderHealthStore health,ILogger<QuantShadowWorker> logger):BackgroundService
{
    private readonly Dictionary<string,DateTimeOffset> next=[];
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await queue.Ready.Task.WaitAsync(ct);
        while(!ct.IsCancellationRequested)
        {
            try
            {
                foreach(var symbol in service.CostSymbols.Take(3))
                {
                    if(next.GetValueOrDefault(symbol)>time.GetUtcNow())continue;next[symbol]=time.GetUtcNow().AddSeconds(20);
                    using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(8));
                    try{foreach(var e in await quotes.GetPriorityQuotesAsync(symbol,timeout.Token))await bus.PublishAsync(e,ct);}
                    catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
                    catch(Exception ex){next[symbol]=time.GetUtcNow().AddSeconds(45);logger.LogDebug("Shadow quote {Symbol}: {Type}",symbol,ex.GetType().Name);}
                }
                await service.RefreshAsync(ct);if(service.Healthy)health.Success("Quant shadow",freshnessSeconds:120);else health.Failure("Quant shadow","影子快照失鲜或未完成");
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
            catch(Exception ex){service.Failure(ex);health.Failure("Quant shadow","影子分析未完成，原模拟盘隔离运行");logger.LogWarning("Quant shadow: {Type}",ex.GetType().Name);}
            await Task.Delay(TimeSpan.FromSeconds(10),time,ct);
        }
    }
}
