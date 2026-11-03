using PulletCryptoRadar.Core;
namespace PulletCryptoRadar.Application;

public sealed class QuantDailyService(PaperService paper,IPaperRepository account,IQuantShadowRepository shadow,IQuantDailyRepository archive,TimeProvider time,PaperPerformanceService? performance=null)
{
    private readonly SemaphoreSlim gate=new(1,1);
    private readonly Dictionary<DateOnly,QuantDailyReport> cache=[];
    public async Task<QuantDailyReport?> GetAsync(DateOnly? requested,CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var now=time.GetUtcNow();var today=DateOnly.FromDateTime(now.ToOffset(TimeSpan.FromHours(8)).DateTime);var date=requested??today;
            if(date>today||date<today.AddDays(-60))throw new ArgumentException("请选择最近60天内且不晚于今天的上海日期");
            if(cache.TryGetValue(date,out var existing)&&now-existing.GeneratedAt<TimeSpan.FromMinutes(1))return existing;
            var context=await paper.ReviewContextAsync(ct);
            // Capture the report clock after awaiting the ledger lock. Its funding check
            // may have completed while this request was waiting for the account snapshot.
            now=time.GetUtcNow();today=DateOnly.FromDateTime(now.ToOffset(TimeSpan.FromHours(8)).DateTime);date=requested??today;
            if(date>today||date<today.AddDays(-60))throw new ArgumentException("请选择最近60天内且不晚于今天的上海日期");
            var control=context.State.QuantControl;if(control is null)return null;
            if(QuantDailyEngine.Start(date).AddDays(1)<=control.ActivatedAt)throw new ArgumentException("所选日期早于新执行启用日期");
            var from=QuantDailyEngine.Start(date);var through=now<from.AddDays(1)?now:from.AddDays(1);
            var universes=new List<QuantUniverse>();var batches=new List<QuantDecisionBatch>();
            for(var hour=from;hour<through;hour=hour.AddHours(1))
            {
                ct.ThrowIfCancellationRequested();if(shadow.Universe(control.RulesHash,hour) is {} universe)universes.Add(universe);
                batches.AddRange(shadow.Decisions(control.RulesHash,hour));
            }
            var assessments=shadow.ReadAssessments(control.RulesHash,from,through);
            var audit=account.AnalysisAudit(from,through);
            // Select exactly the displayed paths before reading their bounded local history.
            var selected=QuantDailyEngine.Build(date,now,context.State,context.Trades,context.Funding,universes,batches,assessments,audit,new Dictionary<Guid,IReadOnlyList<PaperHoldingSnapshot>>());
            var ids=selected.Paths.Select(p=>p.PositionId).ToArray();
            var histories=ids.ToDictionary(id=>id,id=>account.HoldingHistory(id,1000,null));
            var result=QuantDailyEngine.Build(date,now,context.State,context.Trades,context.Funding,universes,batches,assessments,audit,histories) with{EntryReviews=account.EntryReviews(from,through),EntryDiagnosticsStartedAt=account.FirstEntryReviewAt(),EntryImplementationVersion=QuantEntryEngine.Version};
            result=result with{Review=QuantReviewEngine.Build(date,now,context.State,context.Trades,context.Funding,result),AccountPerformance=performance?.ForContext(date,now,context.State,context.Trades,context.Funding)};
            cache[date]=result;foreach(var old in cache.Keys.Where(d=>d<today.AddDays(-60)).ToArray())cache.Remove(old);return result;
        }
        finally{gate.Release();}
    }
    public async Task CaptureAsync(CancellationToken ct)
    {
        var today=DateOnly.FromDateTime(time.GetUtcNow().ToOffset(TimeSpan.FromHours(8)).DateTime);
        foreach(var date in new[]{today,today.AddDays(-1)})
        {
            QuantDailyReport? report;try{report=await GetAsync(date,ct);}catch(ArgumentException){continue;}
            if(report is not null)archive.Save(report);
        }
    }
}
