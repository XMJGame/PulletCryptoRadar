using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Application;

public sealed class PaperService(IPaperRepository repository,IPaperPersistence persistence,IMarketRuntimeStore market,IRadarSnapshotSource radar,TimeProvider time,IAssetRepository history,IDerivativeRuntimeStore derivatives,NetworkOptions? network=null,IQuantExecutionCoordinator? quant=null)
{
    private readonly SemaphoreSlim _gate=new(1,1);private PaperState? _state;private IReadOnlyList<PaperTrade> _trades=[];
    private PaperAuditWindow? activityAudit;private DateTimeOffset activityAt;
    private IReadOnlyList<QuantFundingEvent> _funding=[];
    private async Task Commit(PaperBatch batch,CancellationToken ct)
    {
        try{await persistence.SaveAsync(batch,ct);}catch{_state=null;throw;}
        _state=batch.State;if(batch.Trades.Count>0)_trades=batch.Trades.Concat(_trades).ToArray();if(batch.FundingEvents.Count>0)_funding=_funding.Concat(batch.FundingEvents).ToArray();
    }
    public async Task ActivateQuantAsync(QuantCohort cohort,ExecutionCostModel costs,CancellationToken ct)
    {
        await _gate.WaitAsync(ct);try
        {
            await Ensure(ct);var old=_state!;if(old.QuantControl is not null){if(old.QuantControl.RulesHash!=cohort.RulesHash)throw new InvalidOperationException("原执行版本已冻结，不能覆盖参数");return;}
            if(quant is null||!StructuralPaperEngine.ValidRules(old.Rules)||old.Pending.Count>0||cohort.RulesHash!=QuantRules.Hash(cohort.Parameters,costs))throw new InvalidOperationException("执行组件、旧规则或待成交意向尚未满足迁移条件");
            var now=time.GetUtcNow();var control=QuantPaperEngine.Control(now,cohort.Parameters,costs);
            var next=old with{Revision=old.Revision+1,UpdatedAt=now,QuantControl=control,Decisions=old.Decisions.Append(new(now,"账户","切换新仓实验","旧仓旧规则；新仓仅接生效后Forward，半额校准风险，真实资金费；研究未验证")).TakeLast(100).ToArray()};
            await Commit(new(next,old.Revision,[]),ct);
        }finally{_gate.Release();}
    }
    public async Task<QuantLiveStatus> QuantStatusAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);try{await Ensure(ct);var s=_state!;return new(s.QuantControl is null?"NotActivated":s.Enabled?"ExperimentalLiveSimulation":"PausedEntries",s.QuantControl,s.Positions.Count(p=>p.Quant is not null),s.Positions.Count(p=>p.Quant is null),s.Pending.Count,s.QuantFunding??[],_funding.Sum(f=>f.ClosedAllocated),
            ["仅虚拟资金，策略研究未验证；原仓继续原规则，新仓有独立执行版本。","新仓使用后续盘口及真实数量网格，手续费与残余滑点仍为假设，维持保证金未接真实档位。","实际资金费补证至最近10分钟前，最近10分钟暂记；缺窗/冲突阻止新仓，风险退出继续，迟到费用独立调整绩效。","暂停仅阻止新仓，已有仓位继续管理；20%回撤只提醒和降风险，不补本金或机械停在800。"]);}finally{_gate.Release();}
    }
    public async Task<(PaperState State,IReadOnlyList<PaperTrade> Trades)> ExecutionContextAsync(CancellationToken ct)
    {await _gate.WaitAsync(ct);try{await Ensure(ct);return (_state!,_trades);}finally{_gate.Release();}}
    public async Task<(PaperState State,IReadOnlyList<PaperTrade> Trades,IReadOnlyList<QuantFundingEvent> Funding)> ReviewContextAsync(CancellationToken ct)
    {await _gate.WaitAsync(ct);try{await Ensure(ct);return (_state!,_trades,_funding);}finally{_gate.Release();}}
    public async Task<PaperActivity> ActivityAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);try
        {
            await Ensure(ct);var now=time.GetUtcNow();
            if(activityAudit is null||now-activityAt>=TimeSpan.FromMinutes(1))
            {activityAudit=repository.AnalysisAudit(now.AddHours(-24),now);activityAt=now;}
            return PaperActivityEngine.Build(_state!,activityAudit,now);
        }
        finally{_gate.Release();}
    }
    private async Task Ensure(CancellationToken ct)
    {
        if(_state is not null)return;_trades=repository.Trades();_funding=repository.QuantFundingEvents();var restored=repository.Restore();if(restored is not null){_state=restored;return;}
        var now=time.GetUtcNow();var initial=new PaperState{Revision=1,StartedAt=now,UpdatedAt=now,Decisions=[new(now,"账户","初始化","1000 USDT；10份，每笔100保证金，10倍逐仓；仅模拟")],Curve=[new(now,1000,true)]};
        await persistence.SaveAsync(new(initial,0,[]),ct);_state=initial;
    }
    private async Task<(IReadOnlyList<ForwardDecision> Signals,PaperState Working)> AnalyzeAsync(PaperState s,IReadOnlyList<MarketView> markets,CancellationToken ct)
    {
        var now=time.GetUtcNow();var hour=DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds()/3600*3600);
        if(!s.Enabled||s.ExecutionStartedAt is null||s.SignalHour==hour||now-hour>TimeSpan.FromMinutes(10)||!markets.Any(m=>StructuralPaperEngine.Fresh(m,now)))return ([],s);
        if(s.LastAnalysisAttempt is {} last&&now-last<TimeSpan.FromSeconds(30))return ([],s);
        // Freeze this hour's universe, but let each symbol finish its metered warm-up independently.
        // A ready BTC must not permanently close the hour for nineteen unready symbols.
        if(s.AnalysisHour!=hour)s=s with{AnalysisHour=hour,UniverseVersion=OpportunityEngine.Version,AnalysisSymbols=OpportunityEngine.Select(markets,now,network?.HistorySymbols??60)
            .Where(r=>!r.Tracks.Contains("Early")||r.Tracks.Contains("Momentum")||r.Tracks.Contains("Weakness")).Select(m=>m.Market.Instrument.Symbol).ToArray()};
        var completed=s.SignalObservations.Where(d=>d.DecisionAt==hour).Select(d=>d.Symbol).ToHashSet(StringComparer.Ordinal);
        var candidates=markets.Where(m=>s.AnalysisSymbols.Contains(m.Instrument.Symbol)&&!completed.Contains(m.Instrument.Symbol)).ToArray();
        var signals=new List<ForwardDecision>();
        var ranks=ForwardEngine.Capture(markets,now,new()).Rows.ToDictionary(m=>m.Symbol,m=>m.Rank,StringComparer.Ordinal);
        foreach(var m in candidates)
        {
            var saved=await history.ReadHourlyHistoryAsync(m.Instrument.AssetId,400,ct);
            var rows=ForwardEngine.Hourly(saved,market.GetCandles(m.Instrument.AssetId,5),now);
            var result=PaperAnalysis.Analyze(s.ExecutionStartedAt.Value,m,rows,derivatives.Get(m.Instrument.Symbol),now).Select(d=>d with{Rank=ranks.GetValueOrDefault(m.Instrument.Symbol)}).ToArray();
            if(result.All(d=>d.Status=="Insufficient")&&now-hour<TimeSpan.FromMinutes(9))continue;
            signals.AddRange(result);completed.Add(m.Instrument.Symbol);
        }
        // Included in the same atomic account batch below; no independent writes or retroactive edits.
        var finished=s.AnalysisSymbols.All(completed.Contains)||now-hour>=TimeSpan.FromMinutes(9);
        return (signals,s with{SignalHour=finished?hour:s.SignalHour,LastAnalysisAttempt=now,SignalObservations=s.SignalObservations.Concat(signals).TakeLast(2000).ToArray()});
    }
    public async Task TickAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);try
        {
            await Ensure(ct);var markets=market.GetAll().ToDictionary(m=>m.Instrument.AssetId);var source=_state!;PaperBatch batch;
            if(source.QuantControl is not null){if(quant is null)throw new InvalidOperationException("新执行组件缺失");var input=await quant.CaptureAsync(source,_trades,ct);markets=input.Markets.ToDictionary(m=>m.Instrument.AssetId);batch=QuantPaperEngine.Tick(source,_trades,_funding,input,time.GetUtcNow);}
            else{var analysis=source.Rules.ExperimentalSimulation?await AnalyzeAsync(source,markets.Values.ToArray(),ct):(Signals:(IReadOnlyList<ForwardDecision>)[],Working:source);batch=source.Rules.ExperimentalSimulation?StructuralPaperEngine.Tick(analysis.Working,analysis.Signals,markets,time.GetUtcNow()):PaperEngine.Tick(source,radar.GetSnapshot(),markets,time.GetUtcNow());}
            batch=PaperHoldingRecorder.Attach(source,batch,markets);
            batch=PaperEquityRecorder.Attach(source,batch,_trades,_funding);await Commit(batch,ct);
        }finally{_gate.Release();}
    }
    public async Task SetEnabledAsync(bool enabled,CancellationToken ct)
    {
        await _gate.WaitAsync(ct);try{await Ensure(ct);var old=_state!;var now=time.GetUtcNow();
        if(old.Enabled==enabled&&old.Rules.ExperimentalSimulation)return;
        if(old.QuantControl is not null)
        {
            var at=time.GetUtcNow();var reason=enabled?"恢复时清理遗留未成交意向，不补单":"用户暂停新开仓，取消未成交意向；已有仓位继续管理";
            var reviews=old.Pending.Where(i=>i.QuantIntent is not null).Select(intent=>
            {
                var reservations=old.Positions.Select(p=>new QuantReservation(p.Symbol,p.Direction,QuantPaperEngine.Risk(p,old.Rules),StructuralPaperEngine.RemainingOriginalMargin(p),false))
                    .Concat(old.Pending.Select(i=>new QuantReservation(i.Symbol,i.QuantIntent!.Decision.Direction,i.QuantIntent.Assessment.Plan!.EstimatedRisk,i.QuantIntent.Assessment.Plan.Margin,true))).ToArray();
                var portfolio=new QuantPortfolio(old.Revision,at,null,old.Cash,old.PeakEquity,reservations);var reserved=old.Pending.Where(i=>i.Key!=intent.Key).Sum(i=>i.QuantIntent!.Assessment.Plan!.Margin+i.QuantIntent.Assessment.Plan.Fee);
                var evidence=new QuantEntryEvidence(null,intent.QuantIntent!.Assessment.Context,portfolio,reserved,null,[],old.QuantFunding??[],false);
                return QuantEntryEngine.Capture(intent,old.QuantControl,evidence,at,old.Revision+1,"UserControl","Cancelled",reason);
            }).ToArray();
            var notes=reviews.Select(r=>new PaperDecision(at,r.Intent.Symbol,"取消新意向",reason,r.Intent.Key));
            await Commit(new(old with{Enabled=enabled,Pending=[],Revision=old.Revision+1,UpdatedAt=at,Decisions=old.Decisions.Concat(notes).Append(new(at,"账户",enabled?"恢复新模型":"暂停新仓","保留原生效时间和全部仓位，退出管理继续")).TakeLast(100).ToArray()},old.Revision,[]){EntryReviews=reviews},ct);return;
        }
        if(enabled&&!old.Rules.ExperimentalSimulation&&(old.Positions.Count>0||old.Pending.Count>0||_trades.Count>0))throw new ArgumentException("旧原型存在仓位/成交，不能直接混入结构实验版本");
        var rules=enabled?StructuralPaperEngine.Rules():old.Rules;
        var next=old with{Rules=rules,ExecutionStartedAt=enabled?now:old.ExecutionStartedAt,Enabled=enabled,Revision=old.Revision+1,UpdatedAt=now,Pending=enabled?old.Pending:[],Decisions=old.Decisions.Append(new(now,"账户",enabled?"恢复模拟":"暂停新仓",enabled?"用户授权双向实验：5笔上限、单笔最多权益0.6%且最多6、总风险最多3%且最多30；20%仅提醒复盘，继续实验，研究未验证":"仅暂停虚拟新仓；已有结构仓继续风险退出")).TakeLast(40).ToArray()};await Commit(new(next,old.Revision,[]),ct);}finally{_gate.Release();}
    }
    public async Task<object> HoldingHistoryAsync(Guid? positionId,int limit,long? before,CancellationToken ct)
    {
        await _gate.WaitAsync(ct);try
        {
            await Ensure(ct);if(before is not null&&positionId is null)throw new ArgumentException("翻页需要先选择一笔持仓");
            var rows=repository.HoldingHistory(positionId,limit,before);
            return new{Total=repository.HoldingSnapshotCount(),Records=rows,NextBefore=positionId is not null&&rows.Count>0?rows[^1].CapturedAt.ToUnixTimeMilliseconds():(long?)null,
                Meaning="约每分钟的持仓上下文及开仓/减仓/平仓事件；不是逐笔盘口或每5秒行情归档。数据源时间与新鲜状态保留，失鲜盈亏为Unknown。"};
        }finally{_gate.Release();}
    }
    public async Task<StrategyReviewSnapshot> StrategyReviewAsync(OpportunitySnapshot? opportunities,CancellationToken ct)
    {
        await _gate.WaitAsync(ct);try{await Ensure(ct);return StrategyReview.Build(_state!,_trades,market.GetAll().ToDictionary(m=>m.Instrument.AssetId),opportunities,time.GetUtcNow(),_funding);}finally{_gate.Release();}
    }
    public async Task<PaperOverview> OverviewAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);try
        {
            await Ensure(ct);var s=_state!;var now=time.GetUtcNow();var cash=s.Cash;var equity=cash;var complete=true;
            foreach(var p in s.Positions){var quote=market.Get(p.AssetId);var fresh=s.Rules.ExperimentalSimulation?StructuralPaperEngine.Fresh(quote,now):PaperEngine.Fresh(quote,now);complete&=fresh;var mark=fresh?quote!.MarkPrice!.Value:p.LastMark;equity+=p.Quant is not null?QuantPaperEngine.MarkValue(p,mark):Math.Max(0,p.RemainingMargin+(s.Rules.ExperimentalSimulation?StructuralPaperEngine.Gross(p,mark):p.Quantity*(mark-p.EntryPrice)));}
            complete&=(s.QuantFunding??[]).All(c=>c.Status=="ObservedWindow");
            var margin=s.Positions.Sum(StructuralPaperEngine.RemainingOriginalMargin);var realized=_trades.Sum(t=>t.NetPnl)-_funding.Sum(f=>f.ClosedAllocated);var closed=_trades.GroupBy(t=>t.PositionId??t.Id).Where(g=>g.Any(t=>t.FinalExit)).ToArray();var wins=closed.Count(g=>g.Sum(t=>t.NetPnl)-_funding.Where(f=>f.Evidence.PositionId==g.Key).Sum(f=>f.ClosedAllocated)>0);
            var days=_trades.Select(t=>PaperEngine.Day(t.ClosedAt)).Concat(_funding.Where(f=>f.ClosedAllocated!=0).Select(f=>PaperEngine.Day(f.BookedAt))).Distinct().Select(day=>
            {var rows=_trades.Where(t=>PaperEngine.Day(t.ClosedAt)==day).ToArray();var late=_funding.Where(f=>PaperEngine.Day(f.BookedAt)==day).Sum(f=>f.ClosedAllocated);return new PaperDaily(day.ToString("yyyy-MM-dd"),rows.Length,rows.Count(t=>t.NetPnl>0),rows.Sum(t=>t.NetPnl)-late,rows.Sum(t=>t.Fees),rows.Sum(t=>t.FundingCost)+late,"已观察成本暂记；迟到资金费按入账日独立调整，未改写历史成交。样本不足不据单日调参");}).OrderByDescending(d=>d.Date).Take(30).ToList();
            if(!days.Any(d=>d.Date==PaperEngine.Day(now).ToString("yyyy-MM-dd")))days.Insert(0,new(PaperEngine.Day(now).ToString("yyyy-MM-dd"),0,0,0,0,0,"今天尚无已平仓交易；等待符合规则的实时信号，不补做历史成交"));
            return new(s,equity,complete?equity:null,realized,complete?equity-cash-s.Positions.Sum(p=>p.RemainingMargin):null,margin,_trades.Sum(t=>t.Fees)+s.Positions.Sum(p=>p.OpenFee),closed.Length,wins,closed.Length>0?100m*wins/closed.Length:null,
                s.QuantControl is null?"实验虚拟 USDT 双向 / 逐仓估算；行情来自 Binance 永续；研究未验证，资金费为保守假设，非真实委托或成交":"Quant-Paper-Experiment-1已启用；旧仓旧规则，新仓盘口/实际资金费；最近10分钟费用暂记，策略未验证，非真实委托",days){SettlementLegs=_trades.Count,OpenPlannedRisk=s.Positions.Sum(p=>QuantPaperEngine.Risk(p,s.Rules)),NextAnalysisAt=!s.Enabled?null:s.QuantControl is not null?DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds()/3600*3600).AddHours(1):s.SignalHour==DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds()/3600*3600)||now.Minute>=10?DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds()/3600*3600).AddHours(1):now};
        }finally{_gate.Release();}
    }
}
