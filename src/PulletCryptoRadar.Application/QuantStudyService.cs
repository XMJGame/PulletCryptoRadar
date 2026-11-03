using System.Threading.Channels;
using PulletCryptoRadar.Core;
namespace PulletCryptoRadar.Application;
public sealed class QuantStudyService(IMarketRuntimeStore market,IAssetRepository history,IMarketRestProvider prices,IQuantStudyRepository repository,
    IQuantShadowRepository forwardRepository,QuantShadowService shadow,NetworkOptions network,TimeProvider time)
{
    private readonly object gate=new();private QuantStudyProgress? current;private QuantStudyReport? report;private string? error;private bool initialized,running;
    private readonly Channel<QuantStudyRequest> jobs=Channel.CreateBounded<QuantStudyRequest>(new BoundedChannelOptions(1){SingleReader=true});
    private void Initialize(){if(initialized)return;current=repository.LatestProgress();report=repository.LatestReport();initialized=true;}
    public QuantStudyStatus Status(){lock(gate){Initialize();return new(current,report,running,error);}}
    public QuantStudyProgress Start()
    {
        lock(gate)
        {
            Initialize();if(running||current?.Status is "Queued" or "Collecting" or "Processing" or "Interrupted")return current!;
            var live=shadow.Status();if(live.Cohort is null||live.Latest is null||live.Stale||live.Latest.Universe.Count<Math.Min(20,network.HistorySymbols))throw new InvalidOperationException("影子核心候选池尚未就绪，不能冻结研究名单");
            var all=market.GetAll();var names=live.Latest.Universe.Concat(network.PrioritySymbols).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
            var universe=all.Where(m=>m.Instrument.Active&&names.Contains(m.Instrument.Symbol)&&!QuantRules.Stable(m.Instrument.BaseAsset)).Select(m=>m.Instrument).OrderBy(i=>i.Symbol,StringComparer.Ordinal).Take(67).ToArray();
            if(universe.Length==0)throw new InvalidOperationException("没有可冻结的合约");var request=QuantStudyEngine.Request(universe,live.Cohort.Parameters,time.GetUtcNow());
            current=new(request,"Queued",time.GetUtcNow(),0,0,"已固定名单、时间段与研究参数，等待后台处理",[]);repository.SaveProgress(current);
            if(!jobs.Writer.TryWrite(request))throw new InvalidOperationException("Study queue unavailable");running=true;error=null;return current;
        }
    }
    public void ResumeIncomplete()
    {
        lock(gate)
        {
            Initialize();if(running||current?.Status is not ("Queued" or "Collecting" or "Processing" or "Interrupted"))return;
            if(jobs.Writer.TryWrite(current.Request)){running=true;error=null;}
        }
    }
    public IAsyncEnumerable<QuantStudyRequest> ReadJobs(CancellationToken ct)=>jobs.Reader.ReadAllAsync(ct);
    private void Progress(QuantStudyProgress value){repository.SaveProgress(value);lock(gate)current=value;}
    public async Task ProcessAsync(QuantStudyRequest request,CancellationToken ct)
    {
        QuantStudyProgress state;lock(gate){Initialize();state=current?.Request.Id==request.Id?current:new(request,"Queued",time.GetUtcNow(),0,0,"",[]);}
        try
        {
            var sources=state.Sources.ToDictionary(s=>s.Symbol,StringComparer.Ordinal);var input=new List<(QuantStudySource Source,QuantStudySeries Series)>();
            foreach(var instrument in request.Universe)
            {
                ct.ThrowIfCancellationRequested();QuantStudySeries series;QuantStudySource source;
                if(sources.TryGetValue(instrument.Symbol,out var saved))
                {source=saved;series=repository.Series(saved.Hash)??throw new InvalidDataException("Frozen source is missing");}
                else
                {
                    Progress(state with{Status="Collecting",UpdatedAt=time.GetUtcNow(),Collected=sources.Count,Message="读取/补齐 "+instrument.Symbol,Sources=sources.Values.ToArray()});
                    series=await Collect(instrument,request,ct);var hash=repository.SaveSeries(series);
                    source=new(instrument.Symbol,hash,series.Candles.Count,series.Candles.Count>0?series.Candles.Min(c=>c.OpenTime):null,series.Candles.Count>0?series.Candles.Max(c=>c.EndTime):null,series.Source,series.Error);sources.Add(instrument.Symbol,source);
                    state=state with{Status="Collecting",UpdatedAt=time.GetUtcNow(),Collected=sources.Count,Sources=sources.Values.OrderBy(s=>s.Symbol,StringComparer.Ordinal).ToArray()};Progress(state);
                }
                input.Add((source,series));
            }
            var auditCohort=shadow.Status().Cohort;
            var audit=state.ForwardAudit??(auditCohort?.RulesHash==request.RulesHash?QuantStudyEngine.Audit(forwardRepository,auditCohort):new QuantForwardReproduction(0,0,0,0,0,["当前前瞻批次与研究版本不一致，未混用其他版本"]));
            state=state with{Status="Processing",UpdatedAt=time.GetUtcNow(),Message="按固定时间分段重建价格条件、退出与压力成本",Processed=0,ForwardAudit=audit};Progress(state);
            var availableAt=input.Select(i=>i.Series.CollectedAt).Append(request.RequestedAt).Max();
            var result=await Task.Run(()=>QuantStudyEngine.Run(request,input,audit,availableAt,n=>Progress(state with{Processed=n,UpdatedAt=time.GetUtcNow(),Message=$"已回放{n}/{request.Universe.Count}个合约"}),ct),ct);
            var finished=result.Report with{CompletedAt=time.GetUtcNow()};repository.SaveReport(finished,result.Trades);var savedReport=repository.LatestReport();Progress(state with{Status="Completed",UpdatedAt=time.GetUtcNow(),Processed=request.Universe.Count,Message="报告已存档，历史情景不等于完整策略盈利验证"});lock(gate){report=savedReport;error=null;}
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested)
        {Progress((current??state) with{Status="Interrupted",UpdatedAt=time.GetUtcNow(),Message="正常停止中断研究，下次从已冻结输入继续"});throw;}
        catch(Exception ex)
        {Progress((current??state) with{Status="Failed",UpdatedAt=time.GetUtcNow(),Message="研究未完成："+ex.GetType().Name});lock(gate)error="研究未完成："+ex.GetType().Name;}
        finally{lock(gate)running=false;}
    }
    private async Task<QuantStudySeries> Collect(Instrument instrument,QuantStudyRequest request,CancellationToken ct)
    {
        var from=request.From.AddHours(-request.Strategy.MinimumHours);var to=request.Through;var rows=new List<Candle>();var downloaded=false;string? error=null;
        try
        {
            rows.AddRange(await history.ReadHourlyHistoryAsync(instrument.AssetId,2000,ct));
            var expected=(int)(to-from).TotalHours;var saved=rows.Where(c=>c.Symbol==instrument.Symbol&&c.IntervalMinutes==60&&c.IsValid&&c.OpenTime>=from&&c.EndTime<=to).Select(c=>c.OpenTime).Distinct().Count();
            if(saved<expected)
            {
                var end=to.AddMilliseconds(-1);
                for(var page=0;page<2&&end>=from;page++)
                {
                    using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(25));
                    var fetched=await prices.GetCandlesAsync(instrument.Symbol,60,1000,end,timeout.Token);downloaded=true;rows.AddRange(fetched);
                    if(fetched.Count==0)break;var first=fetched.Min(c=>c.OpenTime);if(first<=from||fetched.Count<1000)break;end=first.AddMilliseconds(-1);
                    await Task.Delay(TimeSpan.FromMilliseconds(250),time,ct);
                }
            }
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch(Exception ex)when(ex is HttpRequestException or IOException or InvalidOperationException or OperationCanceledException or System.Text.Json.JsonException)
        {error="部分历史补采未完成："+ex.GetType().Name;}
        var visible=rows.Where(c=>c.Symbol==instrument.Symbol&&c.IntervalMinutes==60&&c.OpenTime>=from&&c.EndTime<=to&&c.ReceivedTime<=time.GetUtcNow()).ToArray();
        // Equal duplicates are harmless; conflicting values remain, so the engine can reject affected baselines/paths.
        var canonical=visible.GroupBy(c=>(c.OpenTime,c.Open,c.High,c.Low,c.Close,c.QuoteVolume,c.SourceTime)).Select(g=>g.MinBy(c=>c.ReceivedTime)!).OrderBy(c=>c.OpenTime).ToArray();
        return new(instrument,time.GetUtcNow(),downloaded?"Local + Binance USD-M /fapi/v1/klines":"Local hourly cache",canonical,error);
    }
}
