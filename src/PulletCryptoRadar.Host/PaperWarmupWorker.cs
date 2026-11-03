using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;
using Microsoft.Extensions.Options;

namespace PulletCryptoRadar.Host;

// Small bounded warm-up, gated by virtual execution. Reuses the metered/limited provider clients.
public sealed class PaperWarmupWorker(PaperService paper,IMarketRestProvider prices,IPriorityQuoteProvider quotes,IDerivativeRestProvider derivatives,
    IMarketRuntimeStore market,IDerivativeRuntimeStore runtime,IAssetRepository history,PersistenceQueue queue,
    IMarketEventBus bus,IProviderHealthStore health,IOptions<NetworkOptions> network,TimeProvider time,ILogger<PaperWarmupWorker> logger,HourlyWarmupTracker tracker,IQuantShadowTargets? quant=null):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await queue.Ready.Task.WaitAsync(ct);
        while(!ct.IsCancellationRequested)
        {
            try
            {
                var state=(await paper.OverviewAsync(ct)).State;
                var all=market.GetAll();var now=time.GetUtcNow();var retries=tracker.RetrySymbols(now).ToHashSet(StringComparer.Ordinal);
                var required=(quant?.WarmupSymbols??[]).Concat(network.Value.PrioritySymbols).ToHashSet(StringComparer.Ordinal);
                foreach(var m in all.Where(m=>m.Instrument.Active&&retries.Contains(m.Instrument.Symbol)).Concat(PaperUniverse.Warmup(all,state,now,network.Value.HistorySymbols))
                    .Concat(all.Where(m=>m.Instrument.Active&&required.Contains(m.Instrument.Symbol))).DistinctBy(m=>m.Instrument.Symbol))
                {
                    if(!tracker.Due(m.Instrument.Symbol,time.GetUtcNow()))continue;
                    HourlyWarmupRecord? record=null;var plan=new HourlyWarmupPlan(true,0,0,0,"尚未读取缓存");var returned=0;var accepted=0;var phase="ReadCache";
                    try
                    {
                        var saved=await history.ReadHourlyHistoryAsync(m.Instrument.AssetId,800,ct);
                        var availableAt=time.GetUtcNow();var end=DateTimeOffset.FromUnixTimeSeconds(availableAt.ToUnixTimeSeconds()/3600*3600);
                        plan=HourlyWarmupEngine.Plan(m.Instrument.Symbol,saved,end,availableAt);record=tracker.Begin(m.Instrument.Symbol,end,plan);phase="History";
                        if(plan.Required)
                        {
                            var response=await prices.GetCandlesAsync(m.Instrument.Symbol,60,plan.FetchLimit,end.AddMilliseconds(-1),ct);
                            var rows=HourlyWarmupEngine.Accept(m.Instrument.Symbol,response,end,time.GetUtcNow());
                            returned=response.Count;accepted=rows.Count;phase="PersistHistory";
                            if(rows.Count>0)
                            {
                                var ack=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                                await queue.Channel.Writer.WriteAsync(new SaveSnapshotsCommand(rows.Select(c=>(m.Instrument.AssetId,c,(decimal?)null,(decimal?)null)).ToArray(),health.GetAll(),ack),ct);
                                await ack.Task.WaitAsync(ct);
                            }
                            saved=await history.ReadHourlyHistoryAsync(m.Instrument.AssetId,800,ct);
                            plan=HourlyWarmupEngine.Plan(m.Instrument.Symbol,saved,end,time.GetUtcNow());
                        }
                        phase="OpenInterest";record=tracker.Update(record,"Running",phase,plan,returned,accepted,plan.Reason,time.GetUtcNow().AddMinutes(10));
                        var oi=runtime.Get(m.Instrument.Symbol).Series.GetValueOrDefault(DerivativeKind.OpenInterest5m)??[];
                        var baselineMissing=oi.Count==0||oi.Min(s=>s.SourceTime)>end.AddMinutes(-5).AddHours(-24);
                        if(baselineMissing||oi.Max(s=>s.SourceTime)<end.AddMinutes(-15))
                        {
                            var points=await derivatives.GetSeriesAsync(m.Instrument.Symbol,DerivativeKind.OpenInterest5m,baselineMissing?310:35,ct);
                            await bus.PublishAsync(new DerivativeEvent(m.Instrument.Symbol,points,time.GetUtcNow()),ct);
                        }
                        // Economy mode's full ticker has no mark price for every symbol.
                        // Prime both quotes before analysis; later pending/held positions join the fast quote loop.
                        phase="Quotes";record=tracker.Update(record,"Running",phase,plan,returned,accepted,plan.Reason,time.GetUtcNow().AddMinutes(10));
                        foreach(var e in await quotes.GetPriorityQuotesAsync(m.Instrument.Symbol,ct))await bus.PublishAsync(e,ct);
                        if(plan.Required)
                        {
                            // A newly listed contract cannot supply pre-listing history.
                            // Genuine acquisition gaps retry sooner without a busy loop.
                            var retry=m.Instrument.OnboardTime<=end.AddHours(-TenfoldEngine.RequiredHours)?time.GetUtcNow().AddMinutes(10):end.AddHours(1);
                            tracker.Update(record,"Missing","Recheck",plan,returned,accepted,plan.Reason,retry);
                            health.Failure("Paper warm-up",$"{m.Instrument.Symbol}仍缺{plan.MissingHours}个合格闭合小时，等待后续补数");
                        }
                        else{tracker.Update(record,"Ready","Complete",plan,returned,accepted,plan.Reason,end.AddHours(1));health.Success("Paper warm-up",freshnessSeconds:7200);}
                    }
                    catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
                    catch(Exception ex)
                    {
                        var at=time.GetUtcNow();record??=tracker.Begin(m.Instrument.Symbol,DateTimeOffset.FromUnixTimeSeconds(at.ToUnixTimeSeconds()/3600*3600),plan);
                        tracker.Update(record,"Failed",phase,plan,returned,accepted,"本阶段失败："+ex.GetType().Name,at.AddMinutes(10));
                        health.Failure("Paper warm-up","本标的基线未齐，继续等待，不能强制开仓");logger.LogWarning("Paper warm-up {Symbol}/{Phase}: {Type}",m.Instrument.Symbol,phase,ex.GetType().Name);
                    }
                }
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
            catch(Exception ex){logger.LogWarning("Paper warm-up loop: {Type}",ex.GetType().Name);}
            await Task.Delay(TimeSpan.FromSeconds(30),time,ct);
        }
    }
}
