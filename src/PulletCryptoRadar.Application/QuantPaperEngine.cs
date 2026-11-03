using PulletCryptoRadar.Core;
namespace PulletCryptoRadar.Application;

public static class QuantPaperEngine
{
    public const string Version="Quant-Paper-Experiment-1";
    public const int FundingLagMinutes=10;
    public static decimal Risk(PaperPosition p,PaperRules legacy)=>p.Quant is {} q?q.Plan.EstimatedRisk*p.Quantity/p.InitialQuantity:StructuralPaperEngine.RemainingRisk(p,legacy);
    public static decimal MarkValue(PaperPosition p,decimal mark)=>p.Quant is not null?p.RemainingMargin+StructuralPaperEngine.Gross(p,mark):Math.Max(0,p.RemainingMargin+StructuralPaperEngine.Gross(p,mark));
    public static QuantLiveControl Control(DateTimeOffset now,QuantParameters p,ExecutionCostModel costs)=>new(Version,now,QuantRules.Hash(p,costs),p,costs);
    private static Guid Id(object value)=>new(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(QuantRules.HashValue(value))).AsSpan(0,16));
    public static bool SignalMatches(QuantHypothesis d,QuantLiveControl control,IReadOnlyDictionary<string,QuantInputBundle> archived)
    {
        if(d.Mode!="Forward"||!d.InputArchived||d.Status!="PriceCandidate"||d.CapturedAt<control.ActivatedAt||d.RulesHash!=control.RulesHash||!archived.TryGetValue(d.InputHash,out var bundle)||bundle.Symbol!=d.Symbol||bundle.Hour!=d.Hour||QuantRules.HashValue(bundle)!=d.InputHash)return false;
        var frame=QuantSignalEngine.Frame(d.Symbol,bundle.Candles,d.CapturedAt,control.Parameters);var oi=QuantSignalEngine.Oi(d.Symbol,bundle.Oi,d.Hour,d.CapturedAt,control.Parameters);
        var reproduced=QuantSignalEngine.Analyze(d.Market,frame,oi,d.CapturedAt,"Forward",control.RulesHash,d.InputHash,control.Parameters).Single(r=>r.Direction==d.Direction&&r.Strategy==d.Strategy) with{InputArchived=true};
        return QuantRules.HashValue(d)==QuantRules.HashValue(reproduced);
    }
    public static PaperBatch Tick(PaperState source,IReadOnlyList<PaperTrade> history,IReadOnlyList<QuantFundingEvent> booked,QuantPaperInput input,Func<DateTimeOffset>? localClock=null)
    {
        var control=source.QuantControl??throw new InvalidDataException("新执行未启用");var now=input.At;
        if(control.Version!=Version||control.RulesHash!=QuantRules.Hash(control.Parameters,control.Costs)||now<source.UpdatedAt||source.Pending.Any(p=>p.QuantIntent is null))throw new InvalidDataException("新执行版本、时间或遗留意向不兼容");
        var markets=input.Markets.ToDictionary(m=>m.Instrument.AssetId);var bySymbol=input.Markets.ToDictionary(m=>m.Instrument.Symbol,StringComparer.Ordinal);
        var legacy=StructuralPaperEngine.Tick(source with{Enabled=false,Positions=source.Positions.Where(p=>p.Quant is null).ToArray(),Pending=[]},[],markets,now);
        var state=legacy.State with{Enabled=source.Enabled,Positions=legacy.State.Positions.Concat(source.Positions.Where(p=>p.Quant is not null)).ToArray(),Pending=source.Pending,PeakEquity=source.PeakEquity,Curve=source.Curve,ReviewThresholdReached=source.ReviewThresholdReached};
        var positions=state.Positions.ToList();var trades=legacy.Trades.ToList();var fundingEvents=new List<QuantFundingEvent>();var entryReviews=new List<QuantEntryReview>();var coverages=(source.QuantFunding??[]).ToDictionary(c=>c.PositionId);var decisions=new List<PaperDecision>();var cash=state.Cash;
        void Note(string symbol,string action,string reason,string? key=null)
        {if(!source.Decisions.TakeLast(12).Any(d=>d.Symbol==symbol&&d.Action==action&&d.Reason==reason&&now-d.Time<TimeSpan.FromMinutes(1)))decisions.Add(new(now,symbol,action,reason,key));}
        foreach(var issue in input.Issues)Note("执行资料","读取不足",issue);
        var quantIds=source.Positions.Where(p=>p.Quant is not null).Select(p=>p.Id).Concat(history.Where(t=>t.Quant is not null).Select(t=>t.PositionId!.Value)).ToHashSet();
        var cutoff=DateTimeOffset.FromUnixTimeSeconds((now.ToUnixTimeSeconds()/60-FundingLagMinutes)*60);
        foreach(var exposure in ExecutionEvidenceService.Exposures(source,history,now).Where(e=>quantIds.Contains(e.PositionId)))
        {
            var through=exposure.ClosedAt??cutoff;var window=input.Funding.GetValueOrDefault(exposure.Symbol);
            var audit=ExecutionFundingEngine.Evaluate(exposure,window,now,0,through);var status=coverages.GetValueOrDefault(exposure.PositionId)?.Status=="Conflict"?"Conflict":audit.Status;
            foreach(var payment in audit.Payments)
            {
                var key=$"{payment.PositionId:N}:{payment.FundingAt.UtcTicks}:{payment.RateType}";var prior=booked.SingleOrDefault(e=>e.Key==key);
                if(prior is not null)
                {
                    if(prior.Evidence.Quantity!=payment.Quantity||prior.Evidence.Rate!=payment.Rate||prior.Evidence.MarkPrice!=payment.MarkPrice||prior.Evidence.SignedCost!=payment.SignedCost){status="Conflict";Note(payment.Symbol,"资金费冲突","已入账来源冲突，保留原记录并阻止新仓，风险退出继续");}
                    continue;
                }
                var index=positions.FindIndex(p=>p.Id==payment.PositionId);var openQuantity=index>=0?positions[index].Quantity:0;
                var open=payment.SignedCost*openQuantity/payment.Quantity;var closed=payment.SignedCost-open;cash-=closed;
                if(index>=0){var p=positions[index];positions[index]=p with{RemainingMargin=p.RemainingMargin-open,FundingCost=p.FundingCost+open};}
                fundingEvents.Add(new(key,now,open,closed,payment));
            }
            coverages[exposure.PositionId]=new(exposure.PositionId,exposure.Symbol,status,audit.KnownThrough,now,audit.Reason);
        }
        foreach(var original in positions.Where(p=>p.Quant is not null).ToArray())
        {
            var p=original;var q=p.Quant!;var market=markets.GetValueOrDefault(p.AssetId);var evidence=input.Execution.GetValueOrDefault(p.Symbol);
            if(StructuralPaperEngine.Fresh(market,now))
            {
                var side=StructuralPaperEngine.Side(p.Direction);var price=market!.Price!.Value;var mark=market.MarkPrice!.Value;
                var freshPrice=market.PriceSourceTime>p.LastQuoteTime&&market.PriceSourceTime>p.OpenedAt;
                var freshMark=market.FundingSourceTime>p.LastMarkSourceTime&&market.FundingSourceTime>p.OpenedAt;
                string? reason=null;DateTimeOffset? after=null;
                var liq=side>0?(p.Quantity*p.EntryPrice-p.RemainingMargin)/(p.Quantity*(1-q.Parameters.AssumedMaintenanceFraction)):(p.Quantity*p.EntryPrice+p.RemainingMargin)/(p.Quantity*(1+q.Parameters.AssumedMaintenanceFraction));
                if(freshMark&&side*(mark-liq)<=0){reason="LiquidationRisk";after=market.FundingSourceTime;}
                else if(freshPrice&&market.PriceSourceTime>q.StopEffectiveAfter&&side*(price-p.StopPrice)<=0){reason="Stop";after=market.PriceSourceTime;}
                else if(now-p.OpenedAt>=TimeSpan.FromHours(q.Plan.MaximumHoldHours)){reason="TimeLimit";after=market.PriceSourceTime;}
                else if(q.PendingExit is null&&freshPrice)
                {
                    if(!p.FirstTaken&&!q.SkipFirst&&side*(price-p.TakePrice)>=0){reason="Target1";after=market.PriceSourceTime;}
                    else if((p.FirstTaken||q.SkipFirst)&&!p.SecondTaken&&!q.SkipSecond&&side*(price-p.Target2)>=0){reason="Target2";after=market.PriceSourceTime;}
                }
                if(reason is not null&&(q.PendingExit is null||reason is "Stop" or "LiquidationRisk"&&q.PendingExit is not ("Stop" or "LiquidationRisk")))q=q with{PendingExit=reason,ExitAfter=after,
                    ExitObservation=new("Quant-Exit-Timing-1",reason,after,reason=="LiquidationRisk"?market.MarkReceivedTime:market.PriceReceivedTime,localClock?.Invoke()??now,reason=="LiquidationRisk"?mark:price,p.StopPrice,p.LastQuoteTime)};
                p=p with{LastMark=mark,LastQuoteTime=market.PriceSourceTime!.Value,LastMarkSourceTime=market.FundingSourceTime!.Value,QuoteGapObserved=p.QuoteGapObserved||now-p.LastQuoteTime>TimeSpan.FromSeconds(60),Quant=q};
            }
            if(q.PendingExit is {} exit)
            {
                var after=new[]{p.OpenedAt,q.ExitAfter??p.OpenedAt,q.LastFillAt??p.OpenedAt}.Max();var book=evidence?.Book;
                if(book is not null&&book.TransactionAt>after&&book.ReceivedAt>after)
                {
                    decimal step=0;try{if(evidence!.Rules is {} r)step=ExecutionCostEngine.CommonStep(r.StepSize,r.MarketStepSize);}catch(Exception ex)when(ex is ArgumentException or OverflowException){}
                    var target=exit is "Target1" or "Target2";
                    var wanted=target&&step>0?Math.Max(0,ExecutionCostEngine.Floor(p.InitialQuantity*.3m,step)-(exit=="Target1"?q.FirstReduced:q.SecondReduced)):p.Quantity;
                    var minimum=evidence?.Rules is {} rules?Math.Max(rules.MinQuantity,rules.MarketMinQuantity):0;
                    if(target&&step>0&&(wanted<minimum||wanted==0))
                    {q=q with{PendingExit=null,ExitAfter=null,ExitObservation=null,SkipFirst=q.SkipFirst||exit=="Target1",SkipSecond=q.SkipSecond||exit=="Target2"};p=p with{Quant=q};Note(p.Symbol,"分批未执行","30%数量低于市价步长/最小数量，未伪造减仓；余仓继续止损与到期管理",p.SignalKey);}
                    else
                    {
                        var available=(p.Direction=="Long"?book.Bids:book.Asks).Sum(l=>l.Quantity);var amount=Math.Min(p.Quantity,Math.Min(wanted,available));
                        var quote=ExecutionCostEngine.Quote(new(p.Symbol,p.Direction=="Long"?"Sell":"Buy",amount,ReduceOnly:true),evidence?.Rules,book,now,q.Costs with{MaximumSpreadBps=decimal.MaxValue,MaximumImpactBps=decimal.MaxValue});
                        if(quote.Status=="Estimated"&&quote.Quantity is {} quantity&&quantity>0)
                        {
                            var fraction=quantity/p.Quantity;var collateral=p.RemainingMargin*fraction;var entryFee=p.OpenFee*fraction;var funding=p.FundingCost*fraction;var margin=StructuralPaperEngine.RemainingOriginalMargin(p)*fraction;
                            var fill=quote.EstimatedFill!.Value;var gross=StructuralPaperEngine.Side(p.Direction)*quantity*(fill-p.EntryPrice);var fee=quote.Fee!.Value;cash+=collateral+gross-fee;
                            var final=quantity==p.Quantity;var first=q.FirstReduced+(exit=="Target1"?quantity:0);var second=q.SecondReduced+(exit=="Target2"?quantity:0);var targetDone=target?quantity>=wanted:final;
                            trades.Add(new(Id(new{p.Id,exit,Book=book.TransactionAt,quantity}),p.SignalKey,p.AssetId,p.Symbol,p.OpenedAt,now,p.EntryPrice,fill,quantity,margin,q.Plan.Leverage,gross,entryFee+fee,funding,gross-entryFee-fee-funding,
                                exit+"；后续盘口模拟退出",p.QuoteGapObserved,Version,p.Intent){PositionId=p.Id,FinalExit=final,Direction=p.Direction,Bucket=p.Bucket,Quant=q with{ExitComputedAt=localClock?.Invoke()??now},ExitQuote=quote});
                            Note(p.Symbol,final?"新模型平仓":"新模型减仓",exit+"；按后续盘口与实际网格结算，非目标价回填",p.SignalKey);
                            if(final){positions.RemoveAll(x=>x.Id==p.Id);coverages[p.Id]=new(p.Id,p.Symbol,coverages.GetValueOrDefault(p.Id)?.Status=="Conflict"?"Conflict":"Pending",coverages.GetValueOrDefault(p.Id)?.KnownThrough,now,"最终退出后待补齐至退出时刻的资金费；来源冲突仍保留，历史成交不回写");continue;}
                            q=q with{LastFillAt=now,FirstReduced=first,SecondReduced=second,PendingExit=targetDone?null:exit,ExitAfter=targetDone?null:q.ExitAfter,ExitObservation=targetDone?null:q.ExitObservation};
                            p=p with{Quantity=p.Quantity-quantity,RemainingMargin=p.RemainingMargin-collateral,OpenFee=p.OpenFee-entryFee,FundingCost=p.FundingCost-funding,FirstTaken=p.FirstTaken||exit=="Target1"&&targetDone,SecondTaken=p.SecondTaken||exit=="Target2"&&targetDone,Quant=q};
                        }
                        else Note(p.Symbol,"退出等待盘口",quote.Reason,p.SignalKey);
                    }
                }
                else Note(p.Symbol,"退出等待盘口","触发已锁定，等待严格晚于触发/上次成交的新盘口；不回填旧价",p.SignalKey);
            }
            if((p.SecondTaken||q.SkipSecond)&&input.Frames.TryGetValue(p.Symbol,out var frame)&&frame.Issue is null)
            {
                var rows=frame.Bars.Where(c=>c.Symbol==p.Symbol&&c.IntervalMinutes==60&&c.IsValid&&c.EndTime<=now&&c.ReceivedTime<=now&&c.OpenTime>=p.OpenedAt&&c.EndTime>q.LastTrailHour).ToArray();
                if(rows.Length>0)
                {
                    var peak=p.Direction=="Long"?Math.Max(q.TrailPeak,rows.Max(c=>c.High)):Math.Min(q.TrailPeak,rows.Min(c=>c.Low));
                    var stop=p.Direction=="Long"?Math.Max(p.StopPrice,peak-3*p.InitialAtr):Math.Min(p.StopPrice,peak+3*p.InitialAtr);
                    q=q with{TrailPeak=peak,LastTrailHour=rows.Max(c=>c.EndTime),StopEffectiveAfter=stop!=p.StopPrice?now:q.StopEffectiveAfter};p=p with{StopPrice=stop,PeakPrice=peak,Quant=q};
                }
            }
            positions[positions.FindIndex(x=>x.Id==p.Id)]=p;
        }
        var pending=new List<PaperIntent>();var unprocessed=source.Pending.ToList();var used=(source.QuantUsedSignals??[]).ToHashSet(StringComparer.Ordinal);
        decimal? Equity()
        {
            if(!positions.All(p=>StructuralPaperEngine.Fresh(markets.GetValueOrDefault(p.AssetId),now)))return null;
            return cash+positions.Sum(p=>MarkValue(p,markets[p.AssetId].MarkPrice!.Value));
        }
        QuantPortfolio Portfolio()
        {return new(source.Revision,now,Equity(),cash,state.PeakEquity,positions.Select(p=>new QuantReservation(p.Symbol,p.Direction,Risk(p,state.Rules),StructuralPaperEngine.RemainingOriginalMargin(p),false)).Concat(pending.Concat(unprocessed).Select(p=>new QuantReservation(p.Symbol,p.QuantIntent!.Decision.Direction,p.QuantIntent.Assessment.Plan!.EstimatedRisk,p.QuantIntent.Assessment.Plan.Margin,true))).ToArray());}
        decimal Reserved()=>pending.Concat(unprocessed).Sum(i=>i.QuantIntent!.Assessment.Plan!.Margin+i.QuantIntent.Assessment.Plan.Fee);
        QuantEntryEvidence EntryEvidence(PaperIntent intent,bool verified,QuantPortfolio? portfolio=null)=>QuantEntryEngine.Evidence(intent,control,input,portfolio??Portfolio(),Reserved(),coverages.Values.ToArray(),verified);
        void Record(PaperIntent intent,string phase,string outcome,string reason,bool verified=false)
        {entryReviews.Add(QuantEntryEngine.Capture(intent,control,EntryEvidence(intent,verified),now,source.Revision+1,phase,outcome,reason));}
        var costsReady=coverages.Values.All(c=>c.Status=="ObservedWindow");
        foreach(var intent in source.Pending)
        {
            unprocessed.Remove(intent);
            var evidence=intent.QuantIntent!;var d=evidence.Decision;
            var verified=SignalMatches(d,control,input.Archived);
            if(!source.Enabled||now>d.ExpiresAt||!verified){var why=!source.Enabled?"新开仓已暂停":now>d.ExpiresAt?"冻结信号已过期，不补单":"原始信号档案未通过复算";Record(intent,"Signal","Cancelled",why,verified);Note(intent.Symbol,"取消新意向",why,intent.Key);continue;}
            if(!costsReady){Record(intent,"Funding","Waiting","已有新仓资金费覆盖未齐或来源冲突，风险退出继续",verified);pending.Add(intent);continue;}
            if(!bySymbol.TryGetValue(d.Symbol,out var market)||market.Instrument.AssetId!=d.Market.AssetId||!input.Execution.TryGetValue(d.Symbol,out var inputs)){Record(intent,"Identity","Waiting","缺少同一资产身份的行情或执行资料",verified);pending.Add(intent);continue;}
            if(market.PriceSourceTime<=intent.CreatedAt||market.PriceReceivedTime<=intent.CreatedAt||market.FundingSourceTime<=intent.CreatedAt||market.MarkReceivedTime<=intent.CreatedAt||inputs.Book is null||inputs.Book.TransactionAt<=intent.CreatedAt||inputs.Book.ReceivedAt<=intent.CreatedAt){Record(intent,"SubsequentQuotes","Waiting","报价、标记价和盘口须严格晚于意向，尚未齐备",verified);pending.Add(intent);continue;}
            var context=input.Snapshot?.Context??evidence.Assessment.Context;
            if(context is null){Record(intent,"Context","Waiting","缺少当前环境证据",verified);pending.Add(intent);continue;}
            var review=QuantEntryEngine.Evaluate(intent,control,EntryEvidence(intent,verified),now,source.Revision+1);
            if(review.Outcome!="Ready"||review.Selected?.Plan is not {} plan)
            {entryReviews.Add(review);if(review.Outcome=="Waiting")pending.Add(intent);Note(intent.Symbol,review.Outcome=="Waiting"?"新仓准入拦截":"取消新意向",review.Reason,intent.Key);continue;}
            var assessed=review.Selected;
            if(positions.Any(p=>p.Symbol==d.Symbol)||plan.Margin+plan.Fee>cash-Reserved()){entryReviews.Add(review with{Outcome="Cancelled",Reason="同币已占用或其他意向现金预留不足，未成交"});Note(intent.Symbol,"取消新意向","同币已占用或其他意向现金预留不足，未成交",intent.Key);continue;}
            var bucket=Enumerable.Range(1,10).First(b=>positions.All(p=>p.Bucket!=b));var id=Id(new{control.Version,control.ActivatedAt,d.Id});
            var metadata=new QuantLivePosition(control.Parameters,control.Costs,plan,assessed.Quote!,now,plan.Entry){StopEffectiveAfter=now,EntryImplementationVersion=QuantEntryEngine.Version,EntryReviewId=review.Id};
            var p=new PaperPosition(id,intent.Key,d.Market.AssetId,d.Symbol,now,plan.Entry,plan.Quantity,plan.Margin,plan.Margin,plan.Fee,0,now,market.MarkPrice!.Value,market.PriceSourceTime!.Value,plan.Stop,plan.Target1,intent)
                {InitialQuantity=plan.Quantity,InitialStop=plan.Stop,InitialAtr=d.Facts!.Atr,Target2=plan.Target2,PeakPrice=plan.Entry,PlannedRisk=plan.EstimatedRisk,Direction=d.Direction,LastMarkSourceTime=market.FundingSourceTime!.Value,Bucket=bucket,Quant=metadata};
            cash-=plan.Margin+plan.Fee;positions.Add(p);coverages[id]=new(id,d.Symbol,"Pending",null,now,"新仓资金费窗口待补证，最近10分钟暂记，不填完整零成本");
            entryReviews.Add(review with{Outcome="Filled",Reason=QuantEntryEngine.Summary(review).Capped?"按原批准上限缩量复核通过，使用后续盘口模拟成交":"原批准上限内复核通过，使用后续盘口模拟成交"});
            Note(d.Symbol,"新模型开仓",$"{d.Strategy}/{d.Direction}；{plan.Leverage}倍、计划风险{plan.EstimatedRisk:G6}U；后续盘口成交",intent.Key);
        }
        if(source.Enabled&&coverages.Values.All(c=>c.Status=="ObservedWindow")&&input.Snapshot is {} snapshot&&snapshot.RulesHash==control.RulesHash)
        foreach(var assessment in snapshot.Assessments.Where(a=>a.Mode=="Forward"&&a.Status=="ShadowReady"&&a.Plan is not null).OrderBy(a=>a.AsOf).ThenBy(a=>a.DecisionId,StringComparer.Ordinal))
        {
            var d=snapshot.Decisions.SingleOrDefault(d=>d.Id==assessment.DecisionId);if(d is null||used.Contains(d.Id)||now>d.ExpiresAt||!SignalMatches(d,control,input.Archived))continue;
            if(positions.Any(p=>p.Symbol==d.Symbol)||pending.Any(p=>p.Symbol==d.Symbol)||positions.Count+pending.Count>=control.Parameters.MaximumPositions||history.Concat(trades).Any(t=>t.Symbol==d.Symbol&&t.ClosedAt>=d.CapturedAt)){used.Add(d.Id);Note(d.Symbol,"新仓准入拦截","持仓/意向/关闭时间占用，不重复或补做旧信号",d.Id);continue;}
            if(!bySymbol.TryGetValue(d.Symbol,out var market)||market.Instrument.AssetId!=d.Market.AssetId||!input.Execution.TryGetValue(d.Symbol,out var inputs))continue;
            var working=Portfolio();var checkedPlan=QuantPortfolioEngine.Assess(d,QuantRules.Input(market),snapshot.Context,working,input.Frames,inputs,now,control.Parameters,control.Costs);
            if(checkedPlan.Status!="ShadowReady"||checkedPlan.Plan is null){if(checkedPlan.Status!="AwaitData")used.Add(d.Id);Note(d.Symbol,"新仓准入拦截",checkedPlan.Reason,d.Id);continue;}
            used.Add(d.Id);
            var intent=new PaperIntent(Version+":"+d.Id,Guid.Empty,d.Market.AssetId,d.Symbol,now,null){QuantIntent=new(d,checkedPlan)};
            entryReviews.Add(QuantEntryEngine.Capture(intent,control,EntryEvidence(intent,true,working),now,source.Revision+1,"Intent","Created","保存原批准计划，等待严格晚于意向的下一报价与盘口",selected:checkedPlan));
            pending.Add(intent);Note(d.Symbol,"新模型等待后续盘口","意向已保存；须下一报价、标记价及盘口再核验，不在本轮成交",d.Id);
        }
        var finalEquity=Equity();var complete=finalEquity is not null&&coverages.Values.All(c=>c.Status=="ObservedWindow");var accountPeak=complete?Math.Max(source.PeakEquity,finalEquity!.Value):source.PeakEquity;var curve=source.Curve.ToList();
        if(curve.Count==0||now-curve[^1].Time>=TimeSpan.FromMinutes(1))curve.Add(new(now,finalEquity??cash+positions.Sum(p=>MarkValue(p,p.LastMark)),complete));
        var finalState=state with{Cash=cash,Positions=positions,Pending=pending,QuantUsedSignals=used.Order(StringComparer.Ordinal).ToArray(),QuantFunding=coverages.Values.OrderBy(c=>c.PositionId).ToArray(),PeakEquity=accountPeak,ReviewThresholdReached=finalEquity<=state.Rules.InitialCash*(1-state.Rules.ReviewLossFraction),Curve=curve.TakeLast(20160).ToArray(),Decisions=state.Decisions.Concat(decisions).TakeLast(100).ToArray(),RecentTrades=source.RecentTrades.Concat(trades).TakeLast(200).ToArray(),EntryBlockReason=coverages.Values.Any(c=>c.Status!="ObservedWindow")?"新仓资金费覆盖待补证；风险退出继续":cash<=0?"可用现金非正；保留亏损缺口，不补本金":null};
        var beforeTotal=source.Cash+source.Positions.Sum(p=>p.RemainingMargin);var afterTotal=finalState.Cash+finalState.Positions.Sum(p=>p.RemainingMargin);
        var newEntryFees=finalState.Positions.Where(p=>source.Positions.All(o=>o.Id!=p.Id)).Sum(p=>p.OpenFee);
        // Legacy isolated settlement adjustments remain in their original model; new fills have no artificial floor.
        var exitFees=trades.Sum(t=>t.ExitQuote?.Fee??t.Quantity*t.ExitPrice*source.Rules.FeeRate);
        var legacyFundingDelta=source.Positions.Where(p=>p.Quant is null).Sum(p=>p.FundingCost)-legacy.State.Positions.Sum(p=>p.FundingCost)-legacy.Trades.Sum(t=>t.FundingCost);
        var expectedDelta=trades.Sum(t=>t.GrossPnl+t.SettlementAdjustment)-exitFees-newEntryFees-fundingEvents.Sum(e=>e.Evidence.SignedCost)+legacyFundingDelta;
        if(Math.Abs(afterTotal-beforeTotal-expectedDelta)>.00000001m)throw new InvalidDataException("混合账本现金及保证金不守恒");
        return new(finalState,source.Revision,trades){FundingEvents=fundingEvents,EntryReviews=entryReviews};
    }
}
