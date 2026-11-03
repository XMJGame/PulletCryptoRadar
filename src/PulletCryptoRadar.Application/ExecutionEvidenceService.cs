using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Application;
public sealed class ExecutionEvidenceService(IExecutionEvidenceProvider provider,IExecutionEvidenceRepository repository,TimeProvider time,NetworkOptions network)
{
    private readonly SemaphoreSlim refreshGate=new(1,1);private readonly object gate=new();
    private ContractRulesSnapshot? rules;private FundingIntervalSnapshot? intervals;private bool initialized;
    private readonly Dictionary<string,ExecutionOrderBook> books=new(StringComparer.Ordinal);
    private readonly Dictionary<string,FundingRateWindow> funding=new(StringComparer.Ordinal);
    private readonly Dictionary<string,(DateTimeOffset At,string? Error)> attempts=new(StringComparer.Ordinal);
    private readonly Dictionary<string,DateTimeOffset> due=new(StringComparer.Ordinal);
    private IReadOnlyList<FundingAuditResult> audits=[];private int? payments;private string? globalError;private int rotation;
    public ExecutionCostModel Model {get;}=new();
    public ExecutionPlanningInputs PlanningInputs(string symbol)
    {
        lock(gate)
        {
            Initialize();var now=time.GetUtcNow();var interval=intervals is {} i&&i.ObservedAt<=now&&now-i.ObservedAt<=TimeSpan.FromHours(2)?i.Adjustments.FirstOrDefault(x=>x.Symbol==symbol)?.IntervalHours:null;
            return new(rules?.Rules.FirstOrDefault(r=>r.Symbol==symbol),books.GetValueOrDefault(symbol),interval);
        }
    }
    public bool CollectionHealthy
    {get{lock(gate)return rules is {} r&&r.ObservedAt<=time.GetUtcNow()&&time.GetUtcNow()-r.ObservedAt<=TimeSpan.FromHours(12)&&payments is not null&&globalError is null&&attempts.Values.All(a=>a.Error is null);}}
    private void Initialize()
    {
        if(initialized)return;initialized=true;
        try{rules=repository.Rules();intervals=repository.Intervals();payments=repository.FundingPaymentCount();}
        catch(Exception ex)when(ex is IOException or System.Text.Json.JsonException or InvalidDataException){globalError="成本缓存读取失败："+ex.GetType().Name;}
    }
    public async Task RefreshAsync(PaperState state,IReadOnlyList<PaperTrade> trades,IReadOnlyList<MarketView> markets,CancellationToken ct,IReadOnlyList<string>? shadowSymbols=null)
    {
        await refreshGate.WaitAsync(ct);try
        {
            lock(gate)Initialize();var now=time.GetUtcNow();
            async Task Attempt(string key,TimeSpan successDelay,Func<CancellationToken,Task> action)
            {
                if(due.TryGetValue(key,out var next)&&now<next)return;
                due[key]=now.AddSeconds(45);
                using var bounded=CancellationTokenSource.CreateLinkedTokenSource(ct);bounded.CancelAfter(TimeSpan.FromSeconds(12));
                try{await action(bounded.Token);due[key]=time.GetUtcNow().Add(successDelay);}
                catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
                catch(Exception ex)when(ex is HttpRequestException or OperationCanceledException or IOException or System.Text.Json.JsonException or InvalidDataException or FormatException or KeyNotFoundException)
                {lock(gate){attempts[key]=(time.GetUtcNow(),"本项采集失败："+(ex is InvalidDataException?ex.Message:ex.GetType().Name));}}
            }
            await Attempt("Rules",TimeSpan.FromHours(6),async token=>
            {
                lock(gate){if(rules is {} r&&r.ObservedAt<=now&&now-r.ObservedAt<TimeSpan.FromHours(6))return;}
                var value=await provider.GetTradingRulesAsync(token);repository.SaveRules(value);lock(gate){rules=value;attempts["Rules"]=(time.GetUtcNow(),null);}
            });
            await Attempt("Intervals",TimeSpan.FromHours(1),async token=>
            {
                lock(gate){if(intervals is {} i&&i.ObservedAt<=now&&now-i.ObservedAt<TimeSpan.FromHours(1))return;}
                var value=await provider.GetFundingIntervalsAsync(token);repository.SaveIntervals(value);lock(gate){intervals=value;attempts["Intervals"]=(time.GetUtcNow(),null);}
            });
            var hot=state.Positions.Select(p=>p.Symbol).Concat(state.Pending.Select(p=>p.Symbol)).Concat((shadowSymbols??[]).Where(s=>markets.Any(m=>m.Instrument.Symbol==s&&m.Instrument.Active)).Distinct(StringComparer.Ordinal).Take(3)).Concat(state.SignalObservations.Where(d=>d.Status=="Candidate"&&d.CapturedAt<=now&&now-d.CapturedAt<=TimeSpan.FromMinutes(5)).Select(d=>d.Symbol))
                .Distinct(StringComparer.Ordinal).Take(8).ToArray();
            var probes=network.PrioritySymbols.Except(hot,StringComparer.Ordinal).ToArray();var probe=probes.Length>0?probes[rotation++%probes.Length]:null;
            foreach(var symbol in hot.Concat(probe is null?[]:new[]{probe}))
                await Attempt("Book:"+symbol,hot.Contains(symbol)?TimeSpan.FromSeconds(10):TimeSpan.FromMinutes(2),async token=>
                {var book=await provider.GetOrderBookAsync(symbol,token);lock(gate){books[symbol]=book;attempts["Book:"+symbol]=(time.GetUtcNow(),null);}});
            var exposures=Exposures(state,trades,now);
            var fundSymbols=hot.Concat(exposures.Select(e=>e.Symbol)).Concat(probe is null?[]:new[]{probe}).Distinct(StringComparer.Ordinal).Take(16).ToArray();
            foreach(var symbol in fundSymbols)
            {
                lock(gate)
                {
                    if(!funding.ContainsKey(symbol))try{if(repository.Funding(symbol) is {} value)funding[symbol]=value;}
                    catch(Exception ex)when(ex is IOException or System.Text.Json.JsonException or InvalidDataException){attempts["Funding:"+symbol]=(now,"资金费缓存无效："+ex.GetType().Name);}
                }
                await Attempt("Funding:"+symbol,TimeSpan.FromMinutes(5),async token=>
                {
                    var from=exposures.Where(e=>e.Symbol==symbol).Select(e=>e.OpenedAt).DefaultIfEmpty(now.AddHours(-24)).Min();
                    from=from<now.AddDays(-8)?now.AddDays(-8):from;var through=time.GetUtcNow().AddMinutes(-2);if(from>through)return;
                    var value=await provider.GetFundingHistoryAsync(symbol,from,through,token);repository.SaveFunding(value);
                    lock(gate){funding[symbol]=value;attempts["Funding:"+symbol]=(time.GetUtcNow(),null);}
                });
            }
            var reports=exposures.Select(e=>
            {
                FundingRateWindow? window;lock(gate)funding.TryGetValue(e.Symbol,out window);
                var legacy=state.Positions.Where(p=>p.Id==e.PositionId).Sum(p=>p.FundingCost)+trades.Where(t=>t.PositionId==e.PositionId).Sum(t=>t.FundingCost);
                return ExecutionFundingEngine.Evaluate(e,window,time.GetUtcNow(),legacy);
            }).ToArray();
            try{repository.AddFundingPayments(reports.SelectMany(r=>r.Payments).ToArray());lock(gate){payments=repository.FundingPaymentCount();globalError=null;}}
            catch(Exception ex)when(ex is IOException or System.Text.Json.JsonException or InvalidDataException){lock(gate){payments=null;globalError="资金费核验日志未提交："+ex.GetType().Name;}}
            lock(gate)audits=reports;
            foreach(var p in state.Positions)
            {
                var quote=Preview(new(p.Symbol,p.Direction=="Short"?"Buy":"Sell",p.Quantity,ReduceOnly:true));
                repository.SaveQuote(quote);
            }
        }
        finally{refreshGate.Release();}
    }
    public static IReadOnlyList<FundingExposure> Exposures(PaperState state,IReadOnlyList<PaperTrade> trades,DateTimeOffset now)
    {
        var result=new List<FundingExposure>();
        foreach(var p in state.Positions)
        {
            var reductions=trades.Where(t=>t.PositionId==p.Id).OrderBy(t=>t.ClosedAt).Select(t=>new ExposureReduction(t.ClosedAt,t.Quantity)).ToArray();
            var initial=p.InitialQuantity>0?p.InitialQuantity:p.Quantity+reductions.Sum(r=>r.Quantity);
            // A discrepancy is left invalid so the audit cannot publish a fictitious zero.
            if(initial-p.Quantity!=reductions.Sum(r=>r.Quantity))initial=0;
            result.Add(new(p.Id,p.Symbol,p.Direction,p.OpenedAt,initial,null,reductions));
        }
        foreach(var group in trades.Where(t=>t.PositionId is not null).GroupBy(t=>t.PositionId!.Value).Where(g=>!state.Positions.Any(p=>p.Id==g.Key)&&g.Any(t=>t.FinalExit)))
        {
            var final=group.Where(t=>t.FinalExit).Max(t=>t.ClosedAt);if(final<now.AddDays(-7))continue;
            var first=group.First();result.Add(new(group.Key,first.Symbol,first.Direction,first.OpenedAt,group.Sum(t=>t.Quantity),final,
                group.OrderBy(t=>t.ClosedAt).Select(t=>new ExposureReduction(t.ClosedAt,t.Quantity)).ToArray()));
        }
        return result.Take(20).ToArray();
    }
    public ExecutionQuote Preview(ExecutionQuoteRequest request)
    {lock(gate){Initialize();return ExecutionCostEngine.Quote(request,rules?.Rules.FirstOrDefault(r=>r.Symbol==request.Symbol),books.GetValueOrDefault(request.Symbol),time.GetUtcNow(),Model);}}
    public ExecutionFoundationStatus Status(PaperState state,IReadOnlyList<MarketView> markets)
    {
        lock(gate)
        {
            Initialize();var now=time.GetUtcNow();var symbols=state.Positions.Select(p=>p.Symbol).Concat(state.Pending.Select(p=>p.Symbol)).Concat(books.Keys).Concat(funding.Keys).Distinct(StringComparer.Ordinal).Order().Take(25).ToArray();
            var rows=symbols.Select(symbol=>
            {
                var r=rules?.Rules.FirstOrDefault(r=>r.Symbol==symbol);var b=books.GetValueOrDefault(symbol);var f=funding.GetValueOrDefault(symbol);var mark=markets.FirstOrDefault(m=>m.Instrument.Symbol==symbol);
                var price=b?.Asks.FirstOrDefault()?.Price??mark?.Price;var quantity=price>0?100/price.Value:0;
                var last=attempts.Where(p=>p.Key.EndsWith(":"+symbol,StringComparison.Ordinal)).OrderByDescending(p=>p.Value.At).FirstOrDefault();
                var errors=attempts.Where(p=>p.Key.EndsWith(":"+symbol,StringComparison.Ordinal)&&p.Value.Error is not null).Select(p=>p.Value.Error).ToArray();
                var currentInterval=intervals is {} interval&&interval.ObservedAt<=now&&now-interval.ObservedAt<=TimeSpan.FromHours(2)?interval.Adjustments.FirstOrDefault(a=>a.Symbol==symbol)?.IntervalHours:null;
                var observed=f?.Records.Where(x=>x.RateType=="Regular").OrderBy(x=>x.FundingAt).TakeLast(2).ToArray();
                return new ExecutionSymbolStatus(symbol,r,quantity>0?ExecutionCostEngine.Quote(new(symbol,"Buy",quantity),r,b,now,Model):null,
                    quantity>0?ExecutionCostEngine.Quote(new(symbol,"Sell",quantity),r,b,now,Model):null,f?.From,f?.Through,f?.Records.Count,currentInterval,
                    observed?.Length==2?(decimal)(observed[1].FundingAt-observed[0].FundingAt).TotalHours:null,
                    mark?.FundingSourceTime is {} mt&&mt<=now&&now-mt<=TimeSpan.FromSeconds(45)?mark.NextFundingTime:null,last.Value.At==default?null:last.Value.At,
                    errors.Length>0?string.Join("；",errors):null);
            }).ToArray();
            return new(now,"ShadowAudit",Model,rules?.ObservedAt,rules?.Rules.Count??0,rows,audits,payments,
                state.Positions.Select(p=>Preview(new(p.Symbol,p.Direction=="Short"?"Buy":"Sell",p.Quantity,ReduceOnly:true))).ToArray(),
                new[]{"执行基础为影子核验，原模拟账本继续使用开仓时的旧成本规则；本页不下单、不修改现金/仓位，也不以核验成本改写旧交易。",
                    "资金费用实际结算时点、结算标记价和当时模拟数量计算；正数是付费、负数是收取。最近两分钟暂不纳入查询；历史窗口不齐时完整成本Unknown。",
                    "区间字段仅是fundingInfo列出的当前调整；未列出不推定固定8h。相邻两次实际记录的间隔只描述历史，不能保证未来周期。",
                    "100 USDT仅用于当前盘口成本演示，数量向下取整，可能低于交易所最低名义金额；不是推荐订单。手续费每边0.05%和盘口外残余滑点0.05%仍是配置假设。",
                    "交易所与本机存在毫秒时钟差；最多保留2秒内的原始盘口领先，只有本地时间已到达原始源时间才参与估算，不改写接收时间。",
                    "VWAP含已观察的点差与深度冲击，未覆盖RPI、排队、网络延迟、未来撤单、完整市价限幅和保证金阶梯，不是可成交保证或入场许可。"}
                    .Concat(attempts.Where(p=>p.Key is "Rules" or "Intervals"&&p.Value.Error is not null).Select(p=>p.Key+"："+p.Value.Error))
                    .Concat(globalError is null?[]:new[]{globalError}).ToArray());
        }
    }
}
