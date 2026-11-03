using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Application;

public static class StructuralPaperEngine
{
    public const string Version="Paper-LongShort-Experiment-1";
    public static decimal Side(string direction)=>direction=="Short"?-1:1;
    public static decimal Gross(PaperPosition p,decimal price)=>Side(p.Direction)*p.Quantity*(price-p.EntryPrice);
    public static PaperRules Rules()=>new(){Version=Version,ResearchValidated=false,ExperimentalSimulation=true,MaximumPositions=5,
        StopPercent=0,TakePercent=0,MaximumHoldHours=168,RiskPerTrade=6,TotalPlannedRiskLimit=30};
    public static bool Fresh(MarketView? m,DateTimeOffset now)=>m is {Price:>0,MarkPrice:>0}
        &&m.PriceSourceTime is {} t&&m.PriceReceivedTime is {} received&&m.FundingSourceTime is {} mark
        &&t<=now&&received>=t&&received<=now&&mark<=now&&now-t<=TimeSpan.FromSeconds(45)
        &&now-received<=TimeSpan.FromSeconds(45)&&now-mark<=TimeSpan.FromSeconds(45);
    public static decimal RemainingOriginalMargin(PaperPosition p)=>p.InitialQuantity>0?p.InitialMargin*p.Quantity/p.InitialQuantity:p.InitialMargin;
    public static decimal RemainingRisk(PaperPosition p,PaperRules rules)=>Math.Max(0,Side(p.Direction)*p.Quantity*(p.EntryPrice-p.StopPrice))
        +p.Quantity*p.EntryPrice*(rules.FeeRate+rules.SlippageRate+.001m)+p.OpenFee;
    public static bool ValidRules(PaperRules r)=>r.Version==Version&&r.ExperimentalSimulation&&!r.ResearchValidated
        &&r.InitialCash==1000&&r.Leverage==10&&r.MaximumPositions==5&&r.MarginPerTrade==100&&r.RiskPerTrade==6&&r.TotalPlannedRiskLimit==30
        &&r.FeeRate==.0005m&&r.SlippageRate==.0005m&&r.MaintenanceRate==.005m&&r.FundingCost8h==.0001m
        &&r.DailyLossLimit==30&&r.ReviewLossFraction==.20m&&r.MaximumHoldHours==168;
    public static PaperBatch Tick(PaperState state,IReadOnlyList<ForwardDecision> candidates,IReadOnlyDictionary<int,MarketView> markets,DateTimeOffset now)
    {
        if(now<state.UpdatedAt)throw new ArgumentException("模拟时钟不能倒退");
        if(!ValidRules(state.Rules))throw new InvalidOperationException("实验规则版本或参数不匹配，停止写入");
        var r=state.Rules;var cash=state.Cash;var positions=new List<PaperPosition>();var pending=new List<PaperIntent>();
        var trades=new List<PaperTrade>();var decisions=new List<PaperDecision>();var used=state.UsedSignals.ToHashSet(StringComparer.Ordinal);
        if(candidates.Count>0)decisions.Add(new(now,"账户","双向分析快照",$"保存{candidates.Count}条做多/做空分析，含不足、未触发、风险跳过，不为出单降低门槛"));
        (PaperPosition Position,decimal Returned) Exit(PaperPosition p,decimal amount,decimal quote,string reason,bool final)
        {
            amount=Math.Min(amount,p.Quantity);var fraction=amount/p.Quantity;
            var margin=p.RemainingMargin*fraction;var original=p.InitialMargin*amount/p.InitialQuantity;
            var entryFee=p.OpenFee*fraction;var funding=p.FundingCost*fraction;var fill=quote*(1-Side(p.Direction)*r.SlippageRate);
            var gross=Side(p.Direction)*amount*(fill-p.EntryPrice);var exitFee=amount*fill*r.FeeRate;
            var liquidated=reason=="实验模拟强平（估算）";var returned=liquidated?0:Math.Max(0,margin+gross-exitFee);
            var adjustment=returned-(margin+gross-exitFee);
            trades.Add(new(Guid.NewGuid(),p.SignalKey,p.AssetId,p.Symbol,p.OpenedAt,now,p.EntryPrice,fill,amount,original,r.Leverage,
                gross,entryFee+exitFee,funding,returned-original-entryFee,reason,p.QuoteGapObserved,r.Version,p.Intent)
                {Bucket=p.Bucket,SettlementAdjustment=adjustment,PositionId=p.Id,FinalExit=final,Direction=p.Direction});
            decisions.Add(new(now,p.Symbol,final?"实验模拟平仓":"实验模拟分批减仓",reason+"；按本次可用报价含滑点结算，不回填目标价",p.SignalKey));
            return(p with{Quantity=p.Quantity-amount,RemainingMargin=p.RemainingMargin-margin,OpenFee=p.OpenFee-entryFee,FundingCost=p.FundingCost-funding},returned);
        }
        foreach(var old in state.Positions)
        {
            var m=markets.GetValueOrDefault(old.AssetId);
            if(!Fresh(m,now)||m!.PriceSourceTime<=old.LastQuoteTime&&m.FundingSourceTime<=old.LastMarkSourceTime){positions.Add(old);continue;}
            var p=old;var periods=Math.Max(0,now.ToUnixTimeSeconds()/28800-old.LastFundingAt.ToUnixTimeSeconds()/28800);
            var funding=Math.Min(p.RemainingMargin,p.Quantity*p.EntryPrice*r.FundingCost8h*periods);
            p=p with{RemainingMargin=p.RemainingMargin-funding,FundingCost=p.FundingCost+funding,LastFundingAt=now,
                LastMark=m.MarkPrice!.Value,LastQuoteTime=m.PriceSourceTime!.Value,LastMarkSourceTime=m.FundingSourceTime!.Value,
                QuoteGapObserved=p.QuoteGapObserved||now-old.LastQuoteTime>TimeSpan.FromSeconds(60)};
            var liquidation=PaperEngine.EstimatedLiquidation(p,r);
            var side=Side(p.Direction);
            var reason=side*(p.LastMark-liquidation)<=0?"实验模拟强平（估算）":side*(m.Price!.Value-p.StopPrice)<=0?"结构/尾仓跟踪止损":now-p.OpenedAt>=TimeSpan.FromHours(r.MaximumHoldHours)?"持有168小时退出":null;
            if(reason is not null){var settled=Exit(p,p.Quantity,m.Price!.Value,reason,true);cash+=settled.Returned;continue;}
            if(!p.FirstTaken&&side*(m.Price!.Value-p.TakePrice)>=0)
            {
                var settled=Exit(p,p.InitialQuantity*.3m,m.Price!.Value,"第一目标减原始数量30%",false);cash+=settled.Returned;p=settled.Position with{FirstTaken=true};
            }
            if(!p.SecondTaken&&side*(m.Price!.Value-p.Target2)>=0)
            {
                var settled=Exit(p,p.InitialQuantity*.3m,m.Price!.Value,"第二目标减原始数量30%",false);cash+=settled.Returned;p=settled.Position with{SecondTaken=true};
            }
            // A new trail is only tested on a subsequent source quote, never against its own high.
            var peak=side>0?Math.Max(p.PeakPrice,m.Price!.Value):Math.Min(p.PeakPrice,m.Price!.Value);
            var stop=!p.FirstTaken?p.StopPrice:side>0?Math.Max(p.StopPrice,peak-3*p.InitialAtr):Math.Min(p.StopPrice,peak+3*p.InitialAtr);
            positions.Add(p with{PeakPrice=peak,StopPrice=stop});
        }
        var complete=positions.All(p=>Fresh(markets.GetValueOrDefault(p.AssetId),now));
        var equity=cash+positions.Sum(p=>Math.Max(0,p.RemainingMargin+Gross(p,p.LastMark)));
        var peakEquity=complete?Math.Max(state.PeakEquity,equity):state.PeakEquity;
        var review=state.ReviewThresholdReached||equity<=r.InitialCash*(1-r.ReviewLossFraction);
        if(review&&!state.ReviewThresholdReached)decisions.Add(new(now,"账户","20%亏损复盘提醒","根据用户要求继续实验，不自动停止或补本金；后续仓位按剩余权益缩小"));
        var block=!complete?"已有持仓数据失鲜，暂停新仓":equity<=0?"模拟权益不足，不能继续开仓":null;
        foreach(var intent in state.Pending)
        {
            var d=intent.StructuralEvidence;var m=markets.GetValueOrDefault(intent.AssetId);
            if(!state.Enabled||block is not null||positions.Count>=r.MaximumPositions||now-intent.CreatedAt>TimeSpan.FromMinutes(2)||d?.Plan is null)
            {decisions.Add(new(now,intent.Symbol,"取消模拟意向",block??"已暂停、意向超时、容量或计划无效",intent.Key));continue;}
            if(!Fresh(m,now)||m!.PriceSourceTime<=intent.CreatedAt||m.PriceReceivedTime<=intent.CreatedAt||m.FundingSourceTime<=intent.CreatedAt){pending.Add(intent);continue;}
            if(d.Oi24hPercent is not (>=0 and <=30)||d.OiReceivedTime is null||d.OiReceivedTime>d.CapturedAt||d.RulesHash!=PaperAnalysis.RulesHash)
            {decisions.Add(new(now,intent.Symbol,"取消模拟意向","保存的OI或冻结规则证据无效",intent.Key));continue;}
            if(!m.Instrument.Active||m.Volume24h<5_000_000||m.Volume24h is null||positions.Any(p=>p.AssetId==intent.AssetId))
            {decisions.Add(new(now,intent.Symbol,"跳过","已下架、成交额不足500万或已有同标的持仓",intent.Key));continue;}
            var plan=d.Plan;var side=Side(d.Direction);var entry=m.Price!.Value*(1+side*r.SlippageRate);var distance=side*(entry-plan.Stop);
            var liq=side>0?entry*(1-1m/r.Leverage)/(1-r.MaintenanceRate):entry*(1+1m/r.Leverage)/(1+r.MaintenanceRate);
            if(d.Direction is not ("Long" or "Short")||plan.ReferenceEntry<=0||plan.Stop<=0||plan.Target1<=0||plan.Target2<=0||plan.Atr<=0
                ||side*(m.Price.Value/plan.ReferenceEntry-1)>.01m||distance<=0||side*(plan.Stop-liq)<=.5m*plan.Atr||side*(plan.Target1-entry)<=0||side*(plan.Target2-plan.Target1)<=0)
            {decisions.Add(new(now,intent.Symbol,"跳过","追高、结构/强平安全空间或目标失效，不能按旧价成交",intent.Key));continue;}
            var allocatedRisk=positions.Sum(p=>RemainingRisk(p,r));
            var riskRoom=Math.Min(Math.Min(r.RiskPerTrade,equity*.006m),Math.Min(r.TotalPlannedRiskLimit,equity*.03m)-allocatedRisk);
            var unitRisk=distance+entry*(2*r.FeeRate+r.SlippageRate+.001m);
            var qty=riskRoom>0?Math.Min(riskRoom/unitRisk,Math.Min(r.MarginPerTrade*r.Leverage/entry,cash/(entry/r.Leverage+entry*r.FeeRate))):0;
            var margin=qty*entry/r.Leverage;var fee=qty*entry*r.FeeRate;
            if(qty*entry<5||cash<margin+fee){decisions.Add(new(now,intent.Symbol,"跳过","总计划风险/可用现金不足或数量过小",intent.Key));continue;}
            cash-=margin+fee;
            positions.Add(new(Guid.NewGuid(),intent.Key,intent.AssetId,intent.Symbol,now,entry,qty,margin,margin,fee,0,now,m.MarkPrice!.Value,
                m.PriceSourceTime!.Value,plan.Stop,plan.Target1,intent)
                {Bucket=Enumerable.Range(1,10).First(b=>!positions.Any(p=>p.Bucket==b)),InitialQuantity=qty,InitialStop=plan.Stop,InitialAtr=plan.Atr,
                    Target2=plan.Target2,PeakPrice=m.Price.Value,PlannedRisk=qty*unitRisk,Direction=d.Direction,LastMarkSourceTime=m.FundingSourceTime!.Value});
            decisions.Add(new(now,intent.Symbol,"实验模拟开仓",$"{d.Direction}/{d.Strategy}结构计划；风险{qty*unitRisk:F2}、保证金{margin:F2} USDT；OI：{d.OiConfirmation}",intent.Key));
        }
        if(state.Enabled&&block is null&&state.ExecutionStartedAt is {} start)
        foreach(var d in candidates.Where(d=>d.Status=="Candidate"&&d.Plan is not null&&d.CapturedAt>=start&&d.DecisionAt>=start
                &&d.CapturedAt<=now&&now-d.CapturedAt<=TimeSpan.FromMinutes(5))
            .OrderByDescending(d=>d.QuoteVolumeRatio??0).ThenByDescending(d=>Math.Abs(d.Return24h??0)).ThenBy(d=>d.Rank??int.MaxValue)
            .ThenByDescending(d=>d.Strategy,StringComparer.Ordinal).ThenBy(d=>d.Symbol,StringComparer.Ordinal))
        {
            var m=markets.Values.FirstOrDefault(m=>m.Instrument.Symbol==d.Symbol);var key=$"Structure:{d.Symbol}:{d.DecisionAt.ToUnixTimeSeconds()}";
            if(used.Contains(key))continue;
            if(m is null){decisions.Add(new(now,d.Symbol,"跳过","没有当前行情，不创建意向",key));continue;}
            if(positions.Any(p=>p.AssetId==m.Instrument.AssetId)||pending.Any(p=>p.AssetId==m.Instrument.AssetId))
            {decisions.Add(new(now,d.Symbol,"跳过","已有同币持仓或意向，不重复开仓或双向对冲",key));continue;}
            if(positions.Count+pending.Count>=r.MaximumPositions){decisions.Add(new(now,d.Symbol,"跳过","持仓加意向已达到同时持仓上限",key));break;}
            if(state.RecentTrades.Concat(trades).Any(t=>t.Symbol==d.Symbol&&t.FinalExit&&now-t.ClosedAt<TimeSpan.FromHours(24)))
            {decisions.Add(new(now,d.Symbol,"跳过","同币最终平仓后24小时冷却未结束",key));continue;}
            if(!Fresh(m,now)||m.Volume24h is null||m.Volume24h<5_000_000)
            {decisions.Add(new(now,d.Symbol,"跳过","价格/标记价失鲜或成交额不足500万",key));continue;}
            if(d.RulesHash!=PaperAnalysis.RulesHash||d.Oi24hPercent is not (>=0 and <=30)||d.OiSourceTime is null||d.OiReceivedTime is null||d.OiReceivedTime>d.CapturedAt)
            {used.Add(key);decisions.Add(new(now,d.Symbol,"跳过","OI数量24h证据缺失、未来输入或构成反证，不开未经多维确认的仓位",key));continue;}
            used.Add(key);pending.Add(new(key,Guid.Empty,m.Instrument.AssetId,d.Symbol,now,null){StructuralEvidence=d});
            decisions.Add(new(now,d.Symbol,"等待下一报价",$"{d.Strategy}新结构计划，等待之后的价格与标记价；尚未成交",key));
        }
        complete=positions.All(p=>Fresh(markets.GetValueOrDefault(p.AssetId),now));
        equity=cash+positions.Sum(p=>Math.Max(0,p.RemainingMargin+Gross(p,p.LastMark)));
        var curve=state.Curve.ToList();if(curve.Count==0||now-curve[^1].Time>=TimeSpan.FromMinutes(1))curve.Add(new(now,equity,complete));
        return new(state with{Revision=state.Revision+1,UpdatedAt=now,Cash=cash,Positions=positions,Pending=pending,PeakEquity=peakEquity,ReviewThresholdReached=review,
            EntryBlockReason=block,UsedSignals=used.Order().ToArray(),RecentTrades=state.RecentTrades.Concat(trades).TakeLast(200).ToArray(),
            Decisions=state.Decisions.Concat(decisions).TakeLast(80).ToArray(),Curve=curve.TakeLast(20160).ToArray()},state.Revision,trades);
    }
}
