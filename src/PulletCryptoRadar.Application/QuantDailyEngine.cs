using PulletCryptoRadar.Core;
namespace PulletCryptoRadar.Application;

public static class QuantDailyEngine
{
    public const string Version="Quant-Daily-1";
    public static DateTimeOffset Start(DateOnly date)=>new(date.ToDateTime(TimeOnly.MinValue),TimeSpan.FromHours(8));
    public static string Key(string value)=>value.StartsWith(QuantPaperEngine.Version+":",StringComparison.Ordinal)?value[(QuantPaperEngine.Version.Length+1)..]:value;
    public static QuantDailyReport Build(DateOnly date,DateTimeOffset now,PaperState state,IReadOnlyList<PaperTrade> allTrades,IReadOnlyList<QuantFundingEvent> allFunding,
        IReadOnlyList<QuantUniverse> universes,IReadOnlyList<QuantDecisionBatch> batches,IReadOnlyList<QuantAssessment> assessments,PaperAuditWindow audit,
        IReadOnlyDictionary<Guid,IReadOnlyList<PaperHoldingSnapshot>> histories)
    {
        var control=state.QuantControl??throw new InvalidOperationException("新执行尚未启用");var start=Start(date);var end=start.AddDays(1);var through=now<end?now:end;
        bool Day(DateTimeOffset t)=>t>=start&&t<end&&t<=now;
        bool Eligible(QuantHypothesis d)=>d.Mode=="Forward"&&d.InputArchived&&d.RulesHash==control.RulesHash&&d.CapturedAt>=control.ActivatedAt&&Day(d.CapturedAt);
        var rows=batches.Where(b=>b.Mode=="Forward"&&b.RulesHash==control.RulesHash).SelectMany(b=>b.Decisions).Where(Eligible).DistinctBy(d=>d.Id).ToArray();
        var candidates=rows.Where(d=>d.Status=="PriceCandidate").ToArray();
        var plans=candidates.Select(d=>
        {
            var samples=assessments.Where(a=>a.DecisionId==d.Id&&a.Mode=="Forward"&&a.Symbol==d.Symbol&&a.Strategy==d.Strategy&&a.Direction==d.Direction&&a.AsOf>=d.CapturedAt&&a.AsOf<=d.ExpiresAt&&a.AsOf<=now).DistinctBy(a=>a.AsOf).OrderBy(a=>a.AsOf).ToArray();
            var ready=samples.FirstOrDefault(a=>a.Status=="ShadowReady"&&a.Plan is not null);var last=samples.LastOrDefault();
            return new QuantDailyCandidate(d.Id,d.Symbol,d.Strategy,d.Direction,d.CapturedAt,ready is not null?"EverReady":last?.Status=="AwaitData"?"AwaitData":last is not null?"Rejected":"Unassessed",
                ready is not null?"有效期内至少一次计划通过，后续是否成交以账本为准":last?.Reason??"没有有效期内的已存评估；不能当作明确拒绝",samples.Length,ready?.AsOf);
        }).OrderBy(p=>p.CapturedAt).ThenBy(p=>p.Id,StringComparer.Ordinal).ToArray();
        var events=audit.Records.Where(r=>r.Time<=now).SelectMany(r=>r.Decisions).Where(d=>Day(d.Time)&&d.Time>=control.ActivatedAt).DistinctBy(d=>(d.Time,d.Symbol,d.Action,d.SignalKey,d.Reason)).ToArray();
        var intents=events.Where(d=>d.Action=="新模型等待后续盘口"&&d.SignalKey is not null).Select(d=>Key(d.SignalKey!)).ToHashSet(StringComparer.Ordinal);
        // A filled intent is stronger evidence than a possibly truncated audit window.
        var trades=allTrades.Where(t=>t.Quant is not null&&t.Version==control.Version&&t.OpenedAt>=control.ActivatedAt&&t.ClosedAt<=now).DistinctBy(t=>t.Id).ToArray();
        var positions=state.Positions.Where(p=>p.Quant is not null&&p.OpenedAt>=control.ActivatedAt&&p.OpenedAt<=now).ToArray();
        var identities=positions.Select(p=>(p.Id,p.OpenedAt,p.Intent)).Concat(trades.Select(t=>(t.PositionId!.Value,t.OpenedAt,t.Evidence))).DistinctBy(p=>p.Item1).ToArray();
        foreach(var p in identities.Where(p=>Day(p.Item3.CreatedAt)))intents.Add(Key(p.Item3.Key));
        foreach(var p in state.Pending.Where(p=>p.QuantIntent is not null&&Day(p.CreatedAt)))intents.Add(Key(p.Key));
        var ids=identities.Select(p=>p.Item1).ToHashSet();
        var dayTrades=trades.Where(t=>Day(t.ClosedAt)).ToArray();var funding=allFunding.Where(f=>f.BookedAt<=now&&ids.Contains(f.Evidence.PositionId)).DistinctBy(f=>f.Key).ToArray();var dayFunding=funding.Where(f=>Day(f.BookedAt)).ToArray();
        var hours=Enumerable.Range(0,24).Select(n=>start.AddHours(n)).Where(h=>h<through&&h.AddMinutes(control.Parameters.DecisionWindowMinutes)>control.ActivatedAt).Select(hour=>
        {
            var universe=universes.FirstOrDefault(u=>u.Hour==hour);var frozen=batches.Where(b=>b.Hour==hour&&b.Mode=="Forward"&&b.RulesHash==control.RulesHash&&b.CapturedAt<=now).DistinctBy(b=>b.Symbol).ToArray();var eligible=frozen.SelectMany(b=>b.Decisions).Where(Eligible).ToArray();
            return new QuantDailyHour(hour,universe?.Symbols.Count??0,frozen.Length,frozen.Sum(b=>b.Decisions.Count),eligible.Length,eligible.Count(d=>d.Status=="PriceCandidate"),eligible.Count(d=>d.Status=="Insufficient"),
                now<hour.AddMinutes(control.Parameters.DecisionWindowMinutes)?"Collecting":universe is null?"MissingUniverse":frozen.Length<universe.Symbols.Count?"Incomplete":"Frozen");
        }).ToArray();
        var relevant=identities.Where(p=>Day(p.Item2)||positions.Any(o=>o.Id==p.Item1)&&start<=now&&end>p.Item2||dayTrades.Any(t=>t.PositionId==p.Item1)||dayFunding.Any(f=>f.Evidence.PositionId==p.Item1)).OrderByDescending(p=>p.Item2).ToArray();
        var paths=relevant.Take(30).Select(item=>
        {
            var id=item.Item1;var p=positions.FirstOrDefault(p=>p.Id==id);var legs=trades.Where(t=>t.PositionId==id).OrderBy(t=>t.ClosedAt).ToArray();var intent=item.Item3;var d=intent.QuantIntent!.Decision;
            var meta=p?.Quant??legs[0].Quant!;var entry=p?.EntryPrice??legs[0].EntryPrice;var initial=p?.InitialQuantity??legs.Sum(t=>t.Quantity);var history=histories.GetValueOrDefault(id)??[];
            var valid=history.Where(h=>h.PositionId==id&&h.DataFresh&&h.CapturedAt>=item.Item2&&h.CapturedAt<=now&&h.Market?.Price is >0&&h.Market.PriceSourceTime<=h.CapturedAt&&h.Market.PriceReceivedTime<=h.CapturedAt).ToArray();
            var side=d.Direction=="Long"?1:-1;var returns=valid.Select(h=>side*(h.Market!.Price!.Value/entry-1)*100).ToArray();
            var closedAdjustment=funding.Where(f=>f.Evidence.PositionId==id).Sum(f=>f.ClosedAllocated);
            return new QuantDailyPath(id,d.Symbol,d.Strategy,d.Direction,item.Item2,intent.CreatedAt,d.CapturedAt,d.Id,entry,meta.Plan.Leverage,initial,p?.Quantity??0,p?.InitialMargin??legs.Sum(t=>t.Margin),meta.Plan.EstimatedRisk,p?.InitialStop??meta.Plan.Stop,
                meta.Plan.Target1,meta.Plan.Target2,d.StopBasis??"未保存",meta.Plan.Target1Basis,meta.Plan.Target2Basis,intent.QuantIntent.Assessment.Context?.State??"Unknown",d.Facts?.VolumeRatio,d.Oi.Change1hPercent,d.Oi.Change24hPercent,
                meta.EntryQuote.BookAt,meta.EntryQuote.Book?.ReceivedAt,meta.EntryQuote.BookAt>intent.CreatedAt&&meta.EntryQuote.Book?.ReceivedAt>intent.CreatedAt,d.InputArchived,p is not null,legs.Length,legs.Sum(t=>t.NetPnl)-closedAdjustment,
                valid.Length,returns.Length>0?Math.Max(0,returns.Max()):null,returns.Length>0?Math.Min(0,returns.Min()):null,history.Count>=1000,(state.QuantFunding??[]).FirstOrDefault(f=>f.PositionId==id)?.Status??"Unknown",
                p is not null&&p.LastMarkSourceTime<=now&&p.LastQuoteTime<=now&&now-p.LastMarkSourceTime<=TimeSpan.FromSeconds(45)&&now-p.LastQuoteTime<=TimeSpan.FromSeconds(90)?StructuralPaperEngine.Gross(p,p.LastMark):null,
                p?.OpenFee??0,p?.FundingCost??0,legs.Select(t=>$"{t.ClosedAt.ToOffset(TimeSpan.FromHours(8)):MM-dd HH:mm:ss} {t.Reason}；数量{t.Quantity:G8}；净额{t.NetPnl:G6} U").ToArray()){EntryImplementationVersion=meta.EntryImplementationVersion,EntryReviewId=meta.EntryReviewId,
                    Lifecycle=QuantLifecycleEngine.Build(id,p,allTrades,allFunding,(state.QuantFunding??[]).FirstOrDefault(f=>f.PositionId==id),now)};
        }).ToArray();
        var executionEvents=events.Where(d=>d.Action is "新模型等待后续盘口" or "新模型开仓" or "新仓准入拦截" or "取消新意向" or "新模型减仓" or "新模型平仓" or "退出等待盘口" or "分批未执行" or "资金费冲突" or "读取不足").OrderByDescending(d=>d.Time).ToArray();
        var reasons=plans.Where(p=>p.Outcome!="EverReady").Select(p=>(Key:p.Id,p.Reason))
            .Concat(executionEvents.Where(d=>d.Action is "取消新意向" or "新仓准入拦截" or "退出等待盘口" or "资金费冲突" or "读取不足").Select(d=>(Key:Key(d.SignalKey??d.Symbol),d.Reason)))
            .Distinct().GroupBy(p=>p.Reason).Select(g=>new ActivityCount(g.Key,g.Count())).OrderByDescending(c=>c.Count).ToArray();
        var failed=rows.SelectMany(d=>d.Conditions.Where(c=>!c.Passed).Select(c=>(d.Id,c.Key,c.Name))).DistinctBy(c=>(c.Id,c.Key)).GroupBy(c=>c.Name).Select(g=>new ActivityCount(g.Key,g.Count())).OrderByDescending(c=>c.Count).Take(15).ToArray();
        var dayOpenings=identities.Count(p=>Day(p.Item2));var dayEntryFees=positions.Where(p=>Day(p.OpenedAt)).Sum(p=>p.OpenFee)+trades.Where(t=>Day(t.OpenedAt)).Sum(t=>t.Quantity*t.EntryPrice*t.Quant!.Costs.FeeRate);
        var late=dayFunding.Sum(f=>f.ClosedAllocated);var net=dayTrades.Sum(t=>t.NetPnl)-late;
        return new(Version,date.ToString("yyyy-MM-dd"),now,start,through,now>=end,control,state.Revision,state.UpdatedAt,now-state.UpdatedAt>TimeSpan.FromSeconds(30),
            new(rows.Length,candidates.Length,plans.Count(p=>p.Outcome=="EverReady"),plans.Count(p=>p.Outcome=="AwaitData"),plans.Count(p=>p.Outcome=="Rejected"),plans.Count(p=>p.Outcome=="Unassessed"),intents.Count,dayOpenings,events.Count(d=>d.Action=="取消新意向"),plans.Count(p=>p.Outcome=="EverReady"&&!intents.Contains(p.Id))),
            new(dayTrades.Length,dayTrades.Count(t=>t.FinalExit),dayTrades.Sum(t=>t.GrossPnl),dayTrades.Sum(t=>t.Fees),dayTrades.Sum(t=>t.FundingCost),late,net,dayEntryFees,dayFunding.Sum(f=>f.Evidence.SignedCost)),hours,reasons,failed,plans,paths,executionEvents.Take(100).ToArray(),audit.Truncated,relevant.Length>30,
            ["仅统计启用后冻结的Forward判断；当前预览、启用前判断和旧仓不混入漏斗。价格候选=曾通过计划+仅待数据+仅拒绝+缺评估，按信号ID去重。",
             "意向含当日审计、仍在等待及已有仓位/成交反证；审计截断时是可观测下界，未观察到不等于没有。风控通过不保证随后成交。",
             "结算净额按退出日期，迟到的已平数量资金费按入账日期调整。入场手续费另示为现金支出，已结算手续费已含其分摊，不可再次扣除。未平浮盈不是已实现盈利。",
             "持仓路径为所选日开仓/结算/资金费入账及跨日未平仓的累计状态，截至本次生成；最多30笔，每笔最近1000条本地采样。价格极值是非杠杆采样变化，不能替代逐笔MFE/MAE或真实成交。",
             "冻结小时覆盖不足、未形成universe的小时和停机不能当作无机会；日内数据会继续变化，归档允许后续补证。本报告不计算缺少日初完整净值的每日收益率，不证明策略盈利。"])
        {InputIssues=rows.Where(d=>d.Status=="Insufficient").GroupBy(d=>(d.Hour,d.Symbol,d.Reason)).Select(g=>new QuantDailyInputIssue(g.Key.Hour,g.Key.Symbol,InputCategory(g.First(),control.Parameters),g.Key.Reason,g.Count())).OrderByDescending(r=>r.Hour).ThenBy(r=>r.Symbol,StringComparer.Ordinal).ToArray()};
    }
    private static string InputCategory(QuantHypothesis d,QuantParameters p)=>d.Facts is null?"Baseline":!d.Market.Active||QuantRules.Stable(d.Market.BaseAsset)?"Eligibility":
        !QuantRules.FreshPrice(d.Market,d.CapturedAt)?"Quote":d.Market.QuoteVolume24h is null?"MissingFields":d.Market.QuoteVolume24h<p.MinimumVolume24h?"Liquidity":
        d.Market.OnboardAt>d.Hour.AddHours(-p.MinimumHours)?"Listing":"Other";
}
