using System.Text.Json;
using PulletCryptoRadar.Core;
namespace PulletCryptoRadar.Application;

public static class QuantPortfolioValidationEngine
{
    public const string Version="Quant-Portfolio-Validation-1";
    private static bool Same(object? a,object? b)=>QuantRules.HashValue(a)==QuantRules.HashValue(b);
    private static bool Within(decimal value,decimal limit)=>value<=limit+.00000001m;
    public static QuantPortfolioMarketCapture Freeze(QuantPaperInput x)=>new(x.At,x.Markets,x.Snapshot,x.Frames.ToDictionary(p=>p.Key,p=>new QuantPortfolioFrame(p.Value.Symbol,p.Value.Hour,p.Value.Issue,p.Value.Bars),StringComparer.Ordinal),x.Execution,x.Funding,x.Archived,x.Issues);
    private static QuantPaperInput Restore(QuantPortfolioMarketCapture x)=>new(x.At,x.Markets,x.Snapshot,x.Frames.ToDictionary(p=>p.Key,p=>new QuantFrame(p.Value.Symbol,p.Value.Hour,p.Value.Issue,null,p.Value.Bars,[],[]),StringComparer.Ordinal),x.Execution,x.Funding,x.Archived){Issues=x.Issues};
    public static QuantPortfolioValidationRun Run(QuantPortfolioFrozenInput input,Func<string,QuantPortfolioSource> readSource)
    {
        if(input.Version!=Version||input.State.QuantControl is not {} control||input.CapturedAt!=input.Market.At||input.State.UpdatedAt>input.CapturedAt
            ||control.RulesHash!=QuantRules.Hash(control.Parameters,control.Costs)||input.Sources.Select(s=>s.Id).Distinct().Count()!=input.Sources.Count)
            throw new InvalidDataException("组合验证来源版本、时间或复核身份无效");
        var sourceHash=QuantRules.HashValue(new{input.State,input.Trades,input.Funding,input.Market});
        var rows=new List<QuantPortfolioAuditRow>();var sources=new List<QuantPortfolioSource>();
        foreach(var reference in input.Sources)
        {
            var source=readSource(reference.Hash);
            if(source.Review.Id!=reference.Id||QuantRules.HashValue(source)!=reference.Hash||source.Review.Revision>input.State.Revision||source.Review.At>input.State.UpdatedAt
                ||PaperEngine.Day(source.Review.At).ToString("yyyy-MM-dd")!=input.Date||!Same(source.Review.Control,control))throw new InvalidDataException("组合复核原始来源摘要或范围不一致");
            sources.Add(source);rows.Add(Audit(source));
        }
        var batch=QuantPaperEngine.Tick(input.State,input.Trades,input.Funding,Restore(input.Market));
        var repeated=QuantPaperEngine.Tick(input.State,input.Trades,input.Funding,Restore(input.Market));
        var restored=JsonSerializer.Deserialize<QuantPortfolioFrozenInput>(JsonSerializer.Serialize(input))!;
        var restarted=QuantPaperEngine.Tick(restored.State,restored.Trades,restored.Funding,Restore(restored.Market));
        var repeatable=Same(batch,repeated)&&Same(batch,restarted);var preserved=sourceHash==QuantRules.HashValue(new{input.State,input.Trades,input.Funding,input.Market});
        var seed=sources.Where(s=>s.Review.Phase=="Admission"&&s.Review.Outcome=="Filled").OrderByDescending(s=>s.Review.At).ThenBy(s=>s.Review.Id,StringComparer.Ordinal).FirstOrDefault();
        var scenarios=seed is not null&&Audit(seed).Status=="Pass"?Scenarios(seed.Review):new[]{new QuantPortfolioScenario("NoSeed","当日最新成交复核种子","完整且可重算","资料不足","Unknown","没有可靠最新种子，未选其他更有利时点",null,Array.Empty<QuantPortfolioGate>())};
        var before=Resources(input.State,input.Trades,input.Funding,input.CapturedAt);
        var after=Resources(batch.State,input.Trades.Concat(batch.Trades).ToArray(),input.Funding.Concat(batch.FundingEvents).ToArray(),input.CapturedAt);
        var entries=input.State.Positions.Where(p=>p.Quant is not null).Select(p=>(p.Id,p.OpenedAt,p.Quant!,p.Intent))
            .Concat(input.Trades.Where(t=>t.Quant is not null&&t.PositionId is not null).Select(t=>(t.PositionId!.Value,t.OpenedAt,t.Quant!,t.Evidence))).DistinctBy(p=>p.Item1)
            .Where(p=>PaperEngine.Day(p.OpenedAt).ToString("yyyy-MM-dd")==input.Date&&p.OpenedAt>=control.ActivatedAt).ToArray();
        var linked=entries.Count(p=>p.Item3.EntryReviewId is {} id&&sources.Any(s=>s.Review.Id==id&&s.Review.Outcome=="Filled"&&s.Review.Intent.Key==p.Item4.Key&&Same(s.Review.Selected?.Plan,p.Item3.Plan)));
        var mismatches=rows.Count(r=>r.Status=="Fail");var failures=rows.Count(r=>r.Checks.Any(c=>c.Status=="Fail"));
        var copyAdmissions=batch.EntryReviews.Where(r=>r.Outcome is "Filled" or "Created").SelectMany(Constraints).ToArray();
        var gates=new QuantStudyGate[]{
            MarketCoverage(input,batch),
            new("AdmissionReplay",mismatches>0?"Fail":rows.Any(r=>r.Status=="Unknown")?"Unknown":rows.Any(r=>r.Status=="Pass")?"Pass":"Unknown","只重算Intent/Admission，其他阶段不混成通过样本；原始信号独立复算，缺资料保持未知"),
            new("JointLimits",failures>0||copyAdmissions.Any(c=>c.Status=="Fail")?"Fail":rows.Any(r=>r.Outcome=="Filled"&&r.Status=="Pass")?"Pass":"Unknown","持仓和其他意向共同占用数量、原保证金、计划风险及保证金/费用现金；按当时权益核对，不用之后权益重判过去准入"),
            new("FrozenCopy",repeatable&&preserved?"Pass":"Fail","原引擎单轮内存副本、重复和反序列化后相同；没有IPaperPersistence或真实委托"),
            new("EngineeringCases",scenarios.Any(s=>s.Status=="Fail")?"Fail":scenarios.Any(s=>s.Status=="Unknown")?"Unknown":"Pass","冻结真实准入资料作为种子，修改组合/缺失条件的合成门禁案例，不作为现场持仓或盈利证据"),
            new("EntryCoverage",entries.Length>0&&linked==entries.Length?"Pass":"Unknown",$"当日新模型实际入场{entries.Length}笔，可关联逐次成交复核{linked}笔；旧记录不补造"),
            new("FiveActualPositions",before.Positions==5&&before.Equity is not null?"Pass":"Unknown",$"当前原账户实持{before.Positions}笔、意向{before.Pending}笔；仅核对该截面，不据占用5份或合成测试宣称五笔实持现场样本"),
            new("HistoricalMarketReplay","Unknown","单日真实准入及一个冻结轮次，不是完整历史连续五仓收益回测；历史OI、盘口、成分及保证金档位仍有缺口")};
        var report=new QuantPortfolioValidationReport(input.Id,Version,input.Date,input.CapturedAt,QuantRules.HashValue(input),gates.Any(g=>g.Status=="Fail")?"NeedsInvestigation":"EngineeringCheckedAwaitEvidence",
            rows.Count,rows.Count(r=>r.Status=="Pass"),rows.Count(r=>r.Status=="NotApplicable"),rows.Count(r=>r.Status=="Unknown"),mismatches,failures,rows.Count(r=>r.Outcome=="Filled"),
            rows.Count>0?rows.Max(r=>r.AfterSlots):0,entries.Length,linked,entries.Length-linked,before,after,QuantRules.HashValue(batch),repeatable,preserved,batch.Trades.Count,batch.FundingEvents.Count,
            batch.State.Positions.Count(p=>input.State.Positions.All(prior=>prior.Id!=p.Id)),seed?.Review.Id,scenarios.Count(s=>s.Status=="Pass"),scenarios.Count(s=>s.Status=="Unknown"),scenarios.Count(s=>s.Status=="Fail"),scenarios,
            rows.OrderByDescending(r=>r.At).ThenBy(r=>r.Id,StringComparer.Ordinal).Take(100).ToArray(),rows.Count>100,gates,
            ["这是工程与证据覆盖核验，不是策略盈利证明。原账户自动运行，本报告的内存副本不会保存为第二个模拟账户或提交成交。",
             "全部所选日可读取的复核参与计数，明细最多100条；创建/成交后占用包含其他意向，不等于当时有相同数量的实际持仓。",
             "合成案例只验证门禁，不能当成可交易组合绩效。已有仓位可能因价格/资金费变化超出当前预算，资源快照如实提示，不把它等同于入场时违反当时约束。",
             "复制原账户的一轮使用冻结的本机市场、交易规则、盘口和资金费缓存；数据缺失沿用原引擎阻止新仓及风险退出逻辑，不用之后补齐资料替代当时输入。",
             "旧仓旧费用/强平模型保留；盘口成交、手续费与强平档位仍含假设，未来退出费用与最近资金费暂记，不承诺不会强平或穿仓。",
             "缺早期逐次复核、完整连续市场历史或自然五笔实持资料仍为Unknown；五仓上限不是必须开满，不能强行交易凑样本。"]);
        return new(input,report);
    }
    private static QuantStudyGate MarketCoverage(QuantPortfolioFrozenInput input,PaperBatch batch)
    {
        var targets=input.State.Positions.Select(p=>(p.AssetId,p.Symbol,p.Direction,p.Quantity,Reduce:true,NeedsBook:p.Quant is not null))
            .Concat(input.State.Pending.Select(p=>(p.AssetId,p.Symbol,p.QuantIntent!.Decision.Direction,p.QuantIntent.Assessment.Plan!.Quantity,Reduce:false,NeedsBook:true))).ToArray();
        var fresh=0;var executable=0;
        foreach(var target in targets)
        {
            var market=input.Market.Markets.FirstOrDefault(m=>m.Instrument.AssetId==target.AssetId&&m.Instrument.Symbol==target.Symbol);
            if(StructuralPaperEngine.Fresh(market,input.CapturedAt))fresh++;
            if(!target.NeedsBook)continue;
            var evidence=input.Market.Execution.GetValueOrDefault(target.Symbol);
            var side=target.Reduce?(target.Direction=="Long"?"Sell":"Buy"):(target.Direction=="Long"?"Buy":"Sell");
            var quote=ExecutionCostEngine.Quote(new(target.Symbol,side,target.Quantity,ReduceOnly:target.Reduce),evidence?.Rules,evidence?.Book,input.CapturedAt,
                input.State.QuantControl!.Costs with{MaximumSpreadBps=decimal.MaxValue,MaximumImpactBps=decimal.MaxValue});
            if(quote.Status=="Estimated"&&quote.Quantity==target.Quantity)executable++;
        }
        var funds=(batch.State.QuantFunding??[]).All(c=>c.Status=="ObservedWindow");
        var bookTargets=targets.Count(t=>t.NeedsBook);
        return new("CapturedInputs",targets.Length>0&&fresh==targets.Length&&executable==bookTargets&&funds?"Pass":"Unknown",
            $"冻结持仓/意向{targets.Length}个，行情时间通过{fresh}；新模型{bookTargets}个标的规则/盘口完整数量通过{executable}；本轮资金费范围{(funds?"已观察":"待补证")}。未覆盖不填零；旧仓继续旧执行模型。");
    }
    public static QuantPortfolioAuditRow Audit(QuantPortfolioSource source)
    {
        var r=source.Review;var e=r.Evidence;var d=r.Intent.QuantIntent!.Decision;
        var applicable=r.Phase is "Intent" or "Admission";var signal=source.Signal is null?"Missing":QuantPaperEngine.SignalMatches(d,r.Control,new Dictionary<string,QuantInputBundle>{{d.InputHash,source.Signal}})?"Pass":"Fail";
        var status="NotApplicable";var reason="此阶段没有完整准入重算，保留原结果，不计为通过";
        if(applicable)
        {
            if(signal=="Missing"){status="Unknown";reason="缺原始信号档案，未仅凭历史通过标签确认";}
            else if(signal=="Fail"||!e.SignalVerified){status="Fail";reason="原始信号复算或保存核验标记不一致";}
            else if(e.Market is null||e.Context is null||e.Execution is null){status="Unknown";reason="缺保存的完整准入输入";}
            else
            {
                var frames=Frames(e);
                if(r.Phase=="Intent")
                {
                    var plan=QuantPortfolioEngine.Assess(d,e.Market,e.Context,e.Portfolio,frames,e.Execution,r.At,r.Control.Parameters,r.Control.Costs);
                    status=Same(plan,r.Selected)&&r.Outcome=="Created"?"Pass":"Fail";
                }
                else
                {
                    var replay=QuantEntryEngine.Evaluate(r.Intent,r.Control,e,r.At,r.Revision);
                    var expected=r.Outcome=="Filled"?"Ready":r.Outcome;
                    status=replay.Outcome==expected&&Same(replay.Proposed,r.Proposed)&&Same(replay.Selected,r.Selected)&&Same(replay.Values,r.Values)?"Pass":"Fail";
                }
                reason=status=="Pass"?"保存的市场/盘口/规则/相关序列/费用状态重算与原选择一致":"重新计算与原计划、费用结果、条件或结果不一致";
            }
        }
        var active=r.Outcome is "Filled" or "Created";var planUsed=active?r.Selected?.Plan:null;var checks=active?Constraints(r):Array.Empty<QuantPortfolioGate>();
        var unknown=e.Portfolio.Reservations.Count(p=>QuantPortfolioEngine.Correlation(Frames(e).GetValueOrDefault(d.Symbol),Frames(e).GetValueOrDefault(p.Symbol),r.Control.Parameters.CorrelationHours) is null);
        return new(r.Id,r.At,r.Revision,d.Symbol,r.Phase,r.Outcome,status,reason,signal,e.Portfolio.Reservations.Count,e.Portfolio.Reservations.Count+(planUsed is null?0:1),e.Portfolio.Reservations.Count(p=>p.Pending),
            e.Portfolio.Cash,e.CashReserved,planUsed is null?null:e.Portfolio.Cash-e.CashReserved-planUsed.Margin-planUsed.Fee,
            e.Portfolio.Reservations.Sum(p=>p.PlannedRisk),planUsed is null?null:e.Portfolio.Reservations.Sum(p=>p.PlannedRisk)+planUsed.EstimatedRisk,
            e.Portfolio.Reservations.Sum(p=>p.Margin),planUsed is null?null:e.Portfolio.Reservations.Sum(p=>p.Margin)+planUsed.Margin,unknown,checks);
    }
    private static Dictionary<string,QuantFrame> Frames(QuantEntryEvidence e)=>e.Frames.ToDictionary(f=>f.Symbol,f=>new QuantFrame(f.Symbol,f.Hour,f.Issue,null,f.Bars,[],[]),StringComparer.Ordinal);
    public static QuantPortfolioGate[] Constraints(QuantEntryReview r)
    {
        var e=r.Evidence;var d=r.Intent.QuantIntent!.Decision;var p=r.Control.Parameters;var q=r.Selected?.Plan;var old=r.Intent.QuantIntent.Assessment.Plan;
        if(q is null||old is null)return[new("Plan","保存准入计划","Unknown",null,null,null,"缺原计划或实际选择")];
        var reservations=e.Portfolio.Reservations;var eq=e.Portfolio.Equity;var frames=Frames(e);var checks=new List<QuantPortfolioGate>();
        void Limit(string key,string name,decimal? cap,decimal before,decimal after,string why)=>checks.Add(new(key,name,cap is null?"Unknown":Within(after,cap.Value)?"Pass":"Fail",cap,before,after,why));
        Limit("Capacity","持仓＋意向",p.MaximumPositions,reservations.Count,reservations.Count+1,"原仓与其他意向都占用份额");
        Limit("DirectionCount","同向持仓＋意向",p.MaximumSameDirection,reservations.Count(x=>x.Direction==d.Direction),reservations.Count(x=>x.Direction==d.Direction)+1,"多空分别计算，不把反向当作自动对冲");
        Limit("Cash","本笔保证金及手续费",e.Portfolio.Cash-e.CashReserved,0,q.Margin+q.Fee,"其他意向预留保证金和手续费后仍须足额支付");
        Limit("Margin","原保证金总占用",p.MaximumMargin*p.MaximumPositions,reservations.Sum(x=>x.Margin),reservations.Sum(x=>x.Margin)+q.Margin,"不是扣资金费后的抵押额");
        Limit("SingleMargin","本笔保证金",p.MaximumMargin,0,q.Margin,"单份最多100U，数量由冻结风险/结构决定");
        decimal? single=null;
        if(eq is >0&&e.Context is not null&&e.Market?.FundingRate is {} rate)
        {
            var draw=e.Portfolio.PeakEquity>0?Math.Max(0,1-eq.Value/e.Portfolio.PeakEquity):0;
            var drawScale=draw>=.15m?.25m:draw>=.10m?.5m:draw>=.05m?.75m:1;
            var scale=p.PilotRiskScale*QuantPortfolioEngine.EnvironmentScale(e.Context,d.Direction)*d.Oi.RiskScale*drawScale*(Math.Abs(rate)>.001m?.5m:1);
            single=Math.Min(p.AbsoluteRiskCap,eq.Value*p.RiskFraction)*scale;
        }
        Limit("SingleRisk","本笔含成本风险",single,0,q.EstimatedRisk,"按当时权益、环境、OI、回撤与费用倍率独立核对");
        Limit("TotalRisk","总计划风险",eq is {} value?Math.Min(p.AbsoluteTotalRiskCap,value*p.TotalRiskFraction):null,reservations.Sum(x=>x.PlannedRisk),reservations.Sum(x=>x.PlannedRisk)+q.EstimatedRisk,"使用准入前当时权益，含旧仓和意向");
        var same=reservations.Where(x=>x.Direction==d.Direction).Sum(x=>x.PlannedRisk);
        Limit("DirectionRisk","同向计划风险",eq is {} value2?Math.Min(p.AbsoluteDirectionRiskCap,value2*p.DirectionRiskFraction):null,same,same+q.EstimatedRisk,"使用准入前当时权益");
        var correlated=reservations.Where(x=>QuantPortfolioEngine.Correlation(frames.GetValueOrDefault(d.Symbol),frames.GetValueOrDefault(x.Symbol),p.CorrelationHours) is not {} corr||Math.Abs(corr)>=p.CorrelationThreshold).Sum(x=>x.PlannedRisk);
        Limit("CorrelationRisk","相关/未知相关计划风险",eq is {} value3?Math.Min(p.AbsoluteCorrelationRiskCap,value3*p.CorrelationRiskFraction):null,correlated,correlated+q.EstimatedRisk,"按绝对相关性；缺序列按相关占用，不用方向相反抵掉风险");
        Limit("ApprovedQuantity","原批准数量",old.Quantity,old.Quantity,q.Quantity,"执行不得扩量");Limit("ApprovedRisk","原批准风险",old.EstimatedRisk,old.EstimatedRisk,q.EstimatedRisk,"执行不得扩大原批准风险");Limit("Leverage","原批准及版本杠杆上限",Math.Min(old.Leverage,p.MaximumLeverage),old.Leverage,q.Leverage,"只核对当前新仓，不改旧仓杠杆");
        checks.Add(new("Stop","原批准结构止损",q.Stop==old.Stop?"Pass":"Fail",old.Stop,old.Stop,q.Stop,"不为成交移动止损"));
        checks.Add(new("Identity","同币不重复",reservations.All(x=>x.Symbol!=d.Symbol)?"Pass":"Fail",null,null,null,"原仓和其他意向均检查"));
        return checks.ToArray();
    }
    private static QuantPortfolioResources Resources(PaperState s,IReadOnlyList<PaperTrade> trades,IReadOnlyList<QuantFundingEvent> funding,DateTimeOffset now)
    {
        var p=s.QuantControl!.Parameters;var reserved=s.Pending.Sum(x=>x.QuantIntent!.Assessment.Plan!.Margin+x.QuantIntent.Assessment.Plan.Fee);
        var margin=s.Positions.Sum(StructuralPaperEngine.RemainingOriginalMargin)+s.Pending.Sum(x=>x.QuantIntent!.Assessment.Plan!.Margin);
        var risk=s.Positions.Sum(x=>QuantPaperEngine.Risk(x,s.Rules))+s.Pending.Sum(x=>x.QuantIntent!.Assessment.Plan!.EstimatedRisk);
        var equity=PaperEquityRecorder.Capture(s,trades,funding,now).Equity;
        var checks=new[]{new QuantPortfolioGate("Capacity","持仓＋意向",s.Positions.Count+s.Pending.Count<=p.MaximumPositions?"Pass":"Fail",p.MaximumPositions,null,s.Positions.Count+s.Pending.Count,"当前资源快照，不冒充过去入场条件"),
            new QuantPortfolioGate("Margin","原保证金占用",Within(margin,p.MaximumMargin*p.MaximumPositions)?"Pass":"Fail",p.MaximumMargin*p.MaximumPositions,null,margin,"已持仓原保证金＋待成交计划保证金"),
            new QuantPortfolioGate("Cash","扣意向预留后现金",s.Cash>=reserved?"Pass":"Warning",s.Cash,null,reserved,"费用/行情可改变现有资源，负数不补本金或截成零"),
            new QuantPortfolioGate("Risk","当前总风险",equity is null?"Unknown":Within(risk,Math.Min(p.AbsoluteTotalRiskCap,equity.Value*p.TotalRiskFraction))?"Pass":"Warning",equity is null?null:Math.Min(p.AbsoluteTotalRiskCap,equity.Value*p.TotalRiskFraction),null,risk,"现有风险随权益变化，超当前额度不自动等于过去违规")};
        return new(s.Revision,s.UpdatedAt,s.Positions.Count,s.Pending.Count,s.Cash,reserved,s.Cash-reserved,margin,risk,equity,checks);
    }
    private static QuantPortfolioScenario[] Scenarios(QuantEntryReview seed)
    {
        var e=seed.Evidence;var p=seed.Control.Parameters;var direction=seed.Intent.QuantIntent!.Decision.Direction;var other=direction=="Long"?"Short":"Long";var eq=e.Portfolio.Equity!.Value;
        QuantReservation R(int i,string side,decimal risk=.01m,decimal margin=1,bool pending=false)=>new("SYNTHETIC_"+i,side,risk,margin,pending);
        var four=new[]{R(0,direction),R(1,direction),R(2,other),R(3,other)};
        var cases=new List<(string Key,string Name,string Expected,QuantEntryEvidence Evidence)>{
            ("Original","原保存准入","Ready",e),
            ("Fifth","四占用后的第五笔","Ready",e with{Portfolio=e.Portfolio with{Reservations=four}}),
            ("Sixth","五占用拦第六笔","Cancelled",e with{Portfolio=e.Portfolio with{Reservations=four.Append(R(4,other)).ToArray()}}),
            ("PendingSlot","其他意向占一份","Cancelled",e with{Portfolio=e.Portfolio with{Reservations=four.Append(R(4,other,pending:true)).ToArray()}}),
            ("Direction","同向三笔拦第四笔","Cancelled",e with{Portfolio=e.Portfolio with{Reservations=Enumerable.Range(0,3).Select(i=>R(i,direction)).ToArray()}}),
            ("SameSymbol","同币已有占用","Cancelled",e with{Portfolio=e.Portfolio with{Reservations=[new(seed.Intent.Symbol,other,.01m,1,false)]}}),
            ("TotalRisk","总风险额度用尽","Cancelled",e with{Portfolio=e.Portfolio with{Reservations=[R(0,other,Math.Min(p.AbsoluteTotalRiskCap,eq*p.TotalRiskFraction))]}}),
            ("UnknownCorrelation","相关未知额度用尽","Cancelled",e with{Frames=[],Portfolio=e.Portfolio with{Reservations=[R(0,other,Math.Min(p.AbsoluteCorrelationRiskCap,eq*p.CorrelationRiskFraction))]}}),
            ("Margin","四份大保证金的第五笔","Ready",e with{Portfolio=e.Portfolio with{Reservations=four.Select(r=>r with{Margin=p.MaximumMargin,PlannedRisk=1.5m}).ToArray()}}),
            ("Cash","现金不足最小成交","Cancelled",e with{Portfolio=e.Portfolio with{Cash=.00000001m,Reservations=[]},CashReserved=0}),
            ("ReservedCash","其他意向预留保证金及费","Cancelled",e with{Portfolio=e.Portfolio with{Cash=100,Reservations=[R(0,other,2,99.5m,true)]},CashReserved=99.5m*(1+p.MaximumLeverage*seed.Control.Costs.FeeRate)}),
            ("MissingEquity","缺参考权益","Waiting",e with{Portfolio=e.Portfolio with{Equity=null}}),
            ("MissingMark","缺标记价","Waiting",e with{Market=e.Market! with{MarkPrice=null}}),
            ("MissingBook","缺保存盘口","Waiting",e with{Execution=e.Execution! with{Book=null}}),
            ("MissingFunding","已有仓费用观察未知","Waiting",e with{Funding=e.Funding.Append(new(Guid.Empty,"SYNTHETIC","Pending",null,seed.At,"合成缺失案例")).ToArray()}),
            ("Pressure","压力环境","Cancelled",e with{Context=e.Context! with{State="Pressure"}}),
            ("Drawdown","回撤后不增计划风险","NoRiskIncrease",e with{Portfolio=e.Portfolio with{Equity=eq*.8m,PeakEquity=Math.Max(eq,e.Portfolio.PeakEquity)}})};
        return cases.Select(c=>
        {
            var result=QuantEntryEngine.Evaluate(seed.Intent,seed.Control,c.Evidence,seed.At,seed.Revision);
            var risk=result.Selected?.Plan?.EstimatedRisk;
            var success=c.Expected=="NoRiskIncrease"?result.Outcome!="Ready"||risk<=seed.Selected!.Plan!.EstimatedRisk:result.Outcome==c.Expected;
            var constraints=result.Outcome=="Ready"?Constraints(result):Array.Empty<QuantPortfolioGate>();
            success&=constraints.All(check=>check.Status=="Pass");
            return new QuantPortfolioScenario(c.Key,c.Name,c.Expected,result.Outcome,success?"Pass":"Fail",result.Reason,risk,constraints);
        }).ToArray();
    }
}
