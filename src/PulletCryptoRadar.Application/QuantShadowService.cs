using PulletCryptoRadar.Core;
namespace PulletCryptoRadar.Application;
// A separate, read-only consumer of the old paper account. No IPaperPersistence or order interface is available here.
public sealed class QuantShadowService(IMarketRuntimeStore market,IDerivativeRuntimeStore derivatives,IAssetRepository history,
    IQuantShadowRepository repository,PaperService paper,ExecutionEvidenceService execution,NetworkOptions network,TimeProvider time,IQuantExecutionFeed? feed=null):IQuantShadowTargets
{
    private readonly object gate=new();private readonly SemaphoreSlim refresh=new(1,1);private readonly QuantParameters parameters=new();
    private QuantCohort? cohort;private QuantSnapshot? latest;private IReadOnlyList<QuantHistoryHour> hours=[];private string? error;
    private bool executionActive;
    private string[] targets=[],warmups=[],previewUniverse=[];private DateTimeOffset cachedHour,nextRefresh;private readonly Dictionary<string,QuantFrame> frames=[];
    private readonly Dictionary<string,QuantDecisionBatch> frozenBatches=new(StringComparer.Ordinal);
    public IReadOnlyList<string> CostSymbols {get{lock(gate)return targets.ToArray();}}
    public IReadOnlyList<string> WarmupSymbols {get{lock(gate)return warmups.ToArray();}}
    public QuantShadowStatus Status()
    {
        lock(gate)return new(time.GetUtcNow(),cohort,latest,latest is null||time.GetUtcNow()-latest.AsOf>TimeSpan.FromSeconds(90),hours,targets.ToArray(),
            [executionActive?"条件模块发布计划，新版模拟执行负责后续报价再核验与原子记账；成交及退出以账户账本为准。":"影子分析与原模拟盘隔离，不创建、平掉或重置任何仓位。", "参数为预先固定的研究假设；当前预览不计入前瞻样本。", "强平采用1%维持保证金率假设，未接入账户费率/维持保证金档位；不构成实盘成交许可。", "新风险先按半额校准，权益回撤时继续降低额度；20%回撤不自动停盘或补充资金。", executionActive?"分批与尾仓由独立新执行版本管理；历史价格研究未通过预设检验，净收益仍需持续前瞻验证。":"止盈计划30%/30%/40%尾仓仅为待回放方案；实际退出及净收益验证尚未完成。"],error);
    }
    public bool Healthy {get{lock(gate)return error is null&&latest is not null&&time.GetUtcNow()-latest.AsOf<=TimeSpan.FromSeconds(90);}}
    public void Failure(Exception ex){lock(gate){targets=[];error="影子分析未完成："+ex.GetType().Name+"；不影响原模拟账户";}}
    public async Task RefreshAsync(CancellationToken ct)
    {
        await refresh.WaitAsync(ct);
        try
        {
            var now=time.GetUtcNow();if(now<nextRefresh)return;nextRefresh=now.AddSeconds(30);var hash=QuantRules.Hash(parameters);
            if(cohort is null)
            {
                var opened=repository.OpenCohort(hash,parameters,now);var restored=repository.Latest(hash);var archived=repository.History(hash,48);lock(gate){cohort=opened;latest=restored;hours=archived;}
            }
            var all=market.GetAll();var hour=DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds()/3600*3600);var forward=hour>=cohort!.StartedAt&&now<hour.AddMinutes(parameters.DecisionWindowMinutes);
            if(hour!=cachedHour){cachedHour=hour;frames.Clear();frozenBatches.Clear();foreach(var b in repository.Decisions(hash,hour))frozenBatches.Add(b.Symbol,b);previewUniverse=PaperUniverse.Select(all,now,network.HistorySymbols).Where(m=>!QuantRules.Stable(m.Instrument.BaseAsset)).Select(m=>m.Instrument.Symbol).ToArray();}
            if(previewUniverse.Length==0)previewUniverse=PaperUniverse.Select(all,now,network.HistorySymbols).Where(m=>!QuantRules.Stable(m.Instrument.BaseAsset)).Select(m=>m.Instrument.Symbol).ToArray();
            var universe=repository.Universe(hash,hour);
            if(forward&&universe is null&&previewUniverse.Length>=Math.Min(20,network.HistorySymbols))universe=repository.FreezeUniverse(hash,new(cohort.Id,hour,now,previewUniverse));
            var selected=forward&&universe is not null?universe.Symbols:previewUniverse;var bySymbol=all.ToDictionary(m=>m.Instrument.Symbol,StringComparer.Ordinal);
            var overview=await paper.OverviewAsync(ct);var needed=selected.Concat(new[]{"BTCUSDT","ETHUSDT"}).Concat(overview.State.Positions.Select(p=>p.Symbol)).Concat(overview.State.Pending.Select(p=>p.Symbol)).Distinct(StringComparer.Ordinal).ToArray();
            lock(gate)warmups=needed;
            foreach(var symbol in needed)
            {
                if(!bySymbol.TryGetValue(symbol,out var m))continue;if(frames.TryGetValue(symbol,out var cached)&&cached.Issue is null)continue;
                var saved=await history.ReadHourlyHistoryAsync(m.Instrument.AssetId,parameters.MinimumHours+4,ct);
                frames[symbol]=QuantSignalEngine.Frame(symbol,QuantSignalEngine.MergeHourly(symbol,saved,market.GetCandles(m.Instrument.AssetId,5),now),now,parameters);
            }
            // All source times are bounded by the time after local baseline reads; no request is triggered by a GET.
            now=time.GetUtcNow();if(DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds()/3600*3600)!=hour){nextRefresh=now;return;}
            forward=forward&&now<hour.AddMinutes(parameters.DecisionWindowMinutes);var context=QuantPortfolioEngine.Context(frames,all,now,parameters);
            overview=await paper.OverviewAsync(ct);lock(gate)executionActive=overview.State.QuantControl is not null;var portfolio=QuantPortfolioEngine.Portfolio(overview,now);
            var decisions=new List<QuantHypothesis>();var universeHash=QuantRules.HashValue(selected.Order(StringComparer.Ordinal).ToArray());
            foreach(var symbol in selected)
            {
                if(!bySymbol.TryGetValue(symbol,out var m)||!frames.TryGetValue(symbol,out var frame))continue;
                if(forward&&frozenBatches.TryGetValue(symbol,out var frozen)){decisions.AddRange(frozen.Decisions);continue;}
                var raw=(derivatives.Get(symbol).Series.GetValueOrDefault(DerivativeKind.OpenInterest5m)??[]).Where(r=>r.SourceTime>=hour.AddHours(-27)&&r.SourceTime<=hour.AddMinutes(-5)&&r.ReceivedTime<=now).ToArray();
                var oi=QuantSignalEngine.Oi(symbol,raw,hour,now,parameters);var bundle=new QuantInputBundle(symbol,hour,frame.Evidence,raw);var inputHash=QuantRules.HashValue(bundle);
                var mode=forward?(universe is null?"AwaitBaseline":"Forward"):"CurrentPreview";var rows=QuantSignalEngine.Analyze(QuantRules.Input(m),frame,oi,now,mode,hash,inputHash,parameters);
                var wait=forward&&now<hour.AddMinutes(parameters.DecisionWindowMinutes-1)&&(rows.Any(d=>d.Status=="Insufficient")||rows.Any(d=>d.Status=="PriceCandidate")&&(oi.Status=="Unknown"||context.State=="Unknown"));
                if(wait)rows=rows.Select(d=>d with{Mode="AwaitBaseline"}).ToArray();
                else if(forward&&universe is not null)
                {
                    repository.SaveInput(hash,bundle);rows=rows.Select(d=>d with{InputArchived=true}).ToArray();
                    var batch=new QuantDecisionBatch(cohort.Id,hash,"Forward",hour,now,symbol,inputHash,universeHash,context,rows);
                    if(!repository.SaveDecision(batch))batch=repository.Decisions(hash,hour).First(b=>b.Symbol==symbol);
                    frozenBatches[symbol]=batch;rows=batch.Decisions;
                }
                else if(rows.Any(d=>d.Status=="PriceCandidate"))
                {repository.SaveInput(hash,bundle);rows=rows.Select(d=>d with{InputArchived=true}).ToArray();}
                decisions.AddRange(rows);
            }
            var candidates=decisions.Where(d=>d.Status=="PriceCandidate").OrderBy(d=>d.Facts?.AtrPercent).ThenBy(d=>d.Symbol,StringComparer.Ordinal).ThenBy(d=>d.Strategy,StringComparer.Ordinal).ToArray();
            lock(gate)targets=candidates.Where(d=>now<=d.ExpiresAt&&d.Oi.RiskScale>0&&QuantPortfolioEngine.EnvironmentScale(context,d.Direction)>0&&!portfolio.Reservations.Any(r=>r.Symbol==d.Symbol)).Select(d=>d.Symbol).Distinct(StringComparer.Ordinal).Take(3).ToArray();
            var assessments=new List<QuantAssessment>();var reservations=portfolio.Reservations.ToList();var remainingCash=portfolio.Cash;var accepted=new HashSet<string>(StringComparer.Ordinal);
            foreach(var d in candidates)
            {
                if(!bySymbol.TryGetValue(d.Symbol,out var current))continue;var working=portfolio with{Reservations=reservations.ToArray(),Cash=remainingCash};
                var assessment=QuantPortfolioEngine.Assess(d,QuantRules.Input(current),context,working,frames,execution.PlanningInputs(d.Symbol),now,parameters,execution.Model);
                assessments.Add(assessment);if(d.Mode=="Forward"&&now<=d.ExpiresAt.AddSeconds(30))repository.SaveAssessment(hash,assessment);
                if(assessment.Status=="ShadowReady"&&assessment.Plan is {} plan&&accepted.Add(d.Symbol))
                {reservations.Add(new(d.Symbol,d.Direction,plan.EstimatedRisk,plan.Margin,false));remainingCash-=plan.Margin+plan.Fee;}
            }
            var snapshot=new QuantSnapshot(now,hour,forward?"ForwardWindow":"CurrentPreview",hash,selected.ToArray(),context,portfolio,decisions,assessments);
            repository.SaveLatest(snapshot);lock(gate)
            {
                latest=snapshot;if(frozenBatches.Count>0)hours=hours.Where(h=>h.Hour!=hour).Append(new(hour,frozenBatches.Count,frozenBatches.Values.Sum(b=>b.Decisions.Count),frozenBatches.Values.Sum(b=>b.Decisions.Count(d=>d.Status=="PriceCandidate")),frozenBatches.Values.Sum(b=>b.Decisions.Count(d=>d.Status=="Insufficient")))).OrderByDescending(h=>h.Hour).Take(48).ToArray();error=null;
            }
            feed?.Publish(snapshot);
        }
        finally{refresh.Release();}
    }
}
