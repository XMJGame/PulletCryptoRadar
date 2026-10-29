using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Application;

public sealed class ForwardService(IForwardRepository repository,IMarketRuntimeStore market,IAssetRepository candles,
    IDerivativeRuntimeStore derivatives,TimeProvider time)
{
    private readonly SemaphoreSlim gate=new(1,1);
    private ForwardCohort Ensure()
    {
        var existing=repository.Cohort();if(existing is not null)return existing;
        var now=time.GetUtcNow();var rules=new ForwardRules();
        repository.Initialize(new(Guid.NewGuid(),now,now.AddDays(rules.Days),rules,ForwardEngine.Hash(rules)));
        return repository.Cohort()??throw new InvalidOperationException("Cohort initialization failed");
    }
    public async Task TickAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);try
        {
            var cohort=Ensure();var now=time.GetUtcNow();var markets=market.GetAll();
            var bucket=ForwardEngine.Bucket(now);
            if(now<cohort.EndsAt.AddHours(72)&&!repository.HasSnapshot(bucket)&&markets.Count>0)
            {
                var snapshot=ForwardEngine.Capture(markets,now,cohort.Rules);var decisions=new List<ForwardDecision>();
                // No network calls: evaluate all registered active assets; uncached histories stay insufficient.
                // Every hour's first observation is immutable, even when missing. Later backfill cannot rewrite it.
                var existing=repository.Decisions().Select(d=>d.Id).ToHashSet(StringComparer.Ordinal);
                if(now<cohort.EndsAt&&(now.Minute<cohort.Rules.MaximumDecisionDelayMinutes||existing.Count==0))
                foreach(var row in snapshot.Rows)
                {
                    var hour=DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds()/3600*3600);
                    if(existing.Contains($"{hour.ToUnixTimeSeconds()}:{row.Symbol}:A"))continue;
                    var view=markets.First(m=>m.Instrument.AssetId==row.AssetId);
                    var saved=await candles.ReadHourlyHistoryAsync(row.AssetId,400,ct);
                    var hourly=ForwardEngine.Hourly(saved,market.GetCandles(row.AssetId,5),now);
                    decisions.AddRange(ForwardEngine.Decide(cohort,view,hourly,row,derivatives.Get(row.Symbol),now));
                }
                repository.Save(snapshot,decisions);
            }
            var known=repository.Outcomes().Select(o=>o.Id).ToHashSet(StringComparer.Ordinal);var outcomes=new List<ForwardOutcome>();
            foreach(var d in repository.Decisions().Where(d=>d.Plan is not null))
            foreach(var horizon in new[]{24,72})
            {
                if(now<d.CapturedAt.AddHours(horizon)||known.Contains($"{d.Id}:{horizon}"))continue;
                var frames=repository.Snapshots(d.CapturedAt,d.CapturedAt.AddHours(horizon));
                outcomes.Add(ForwardEngine.Evaluate(d,horizon,frames,now,cohort.Rules));
            }
            if(outcomes.Count>0)repository.SaveOutcomes(outcomes);
        }finally{gate.Release();}
    }
    public async Task<object> OverviewAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);try
        {
            var cohort=Ensure();var now=time.GetUtcNow();var decisions=repository.Decisions();var outcomes=repository.Outcomes();
            var snapshots=repository.SnapshotSummaries();
            var last=repository.LatestSnapshot();
            var collectionEnd=cohort.EndsAt.AddHours(72);
            var dates=Enumerable.Range(0,cohort.Rules.Days+3).Select(i=>cohort.StartedAt.AddDays(i).ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd"))
                .Append(collectionEnd.AddTicks(-1).ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd")).Distinct().Where(day=>string.CompareOrdinal(day,now.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd"))<=0);
            var days=dates.Select(day=>
            {
                var ds=decisions.Where(d=>Day(d.CapturedAt)==day).ToArray();var ss=snapshots.Where(s=>Day(s.CapturedAt)==day).ToArray();
                var dayStart=new DateTimeOffset(DateOnly.ParseExact(day,"yyyy-MM-dd").ToDateTime(TimeOnly.MinValue),TimeSpan.FromHours(8));
                var from=dayStart>cohort.StartedAt?dayStart:cohort.StartedAt;
                var to=new[]{dayStart.AddDays(1),collectionEnd,now}.Min();
                var upper=to==now?to:to.AddTicks(-1);
                var expected=Math.Max(0,(int)((ForwardEngine.Bucket(upper)-ForwardEngine.Bucket(from)).TotalMinutes/cohort.Rules.SnapshotMinutes)+1);
                return new{Date=day,Snapshots=ss.Length,ExpectedSlots=expected,MissingSlots=Math.Max(0,expected-ss.Length),
                    Registered=ss.LastOrDefault()?.Registered??0,Fresh=ss.LastOrDefault()?.Fresh??0,
                    A=ds.Count(d=>d.Strategy=="A"&&d.Status=="Candidate"),B=ds.Count(d=>d.Strategy=="B"&&d.Status=="Candidate"),
                    Skipped=ds.Count(d=>d.Status=="Skipped"),Insufficient=ds.Count(d=>d.Status=="Insufficient"),NoSignal=ds.Count(d=>d.Status=="NoSignal"),
                    Outcomes=outcomes.Count(o=>Day(o.EvaluatedAt)==day),IncompleteOutcomes=outcomes.Count(o=>Day(o.EvaluatedAt)==day&&!o.CoverageComplete),
                    Review="按观察时保存的版本与缺失记录复盘；5分钟采样不能证明真实成交，亏损/未启动样本不删除，不按单日结果调参。"};
            }).Reverse().ToArray();
            var plans=decisions.Where(d=>d.Plan is not null||d.Status=="Skipped").Take(100).ToArray();
            return new{Cohort=cohort,Now=now,Status=now>=collectionEnd?"本轮采样结束，保留结果与缺口；不会自动续期或开启交易":now>=cohort.EndsAt?"观察期已结束，等待末尾72h标签完成；不会自动续期或开启交易":"前瞻观察中，数据不足则等待预热",
                ObservationFresh=last is not null&&now-last.CapturedAt<=TimeSpan.FromMinutes(10),LastSnapshot=last?.CapturedAt,
                SnapshotCount=snapshots.Count,DecisionCount=decisions.Count,PlanCount=decisions.Count(d=>d.Plan is not null),
                Days=days,LatestRanking=last,Decisions=plans,RecentDiagnostics=decisions.Take(30).ToArray(),Outcomes=outcomes.TakeLast(100).ToArray(),
                AutomaticTrading=false,NetworkRequestsAdded=0,AccountLinked=false,
                Notice="本观察使用全部已注册活跃合约的当时快照；省流模式只补齐重点标的历史，缺失/失鲜不算通过。不声称覆盖交易所已退市或未注册历史全集。程序关闭时不记录，重启不补造缺失快照。运行到14天只结束入组，末尾72h采样继续保留至完成；这是观察标签，不是模拟策略净收益。"};
        }finally{gate.Release();}
    }
    private static string Day(DateTimeOffset time)=>time.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd");
}
