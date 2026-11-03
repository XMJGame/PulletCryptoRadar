using PulletCryptoRadar.Core;
namespace PulletCryptoRadar.Application;

public sealed class HourlyWarmupTracker(IHourlyWarmupRepository repository,TimeProvider time)
{
    public const string Version="Hourly-Warmup-Audit-1";
    private readonly object gate=new();private Dictionary<string,HourlyWarmupRecord>? records;
    private Dictionary<string,HourlyWarmupRecord> Records=>records??=repository.Read().Where(r=>r.Version==Version).ToDictionary(r=>r.Symbol,StringComparer.Ordinal);
    public bool Due(string symbol,DateTimeOffset now){lock(gate)return !Records.TryGetValue(symbol,out var value)||now>=value.NextAttemptAt;}
    public IReadOnlyList<string> RetrySymbols(DateTimeOffset now)
    {lock(gate)return Records.Values.Where(r=>r.Status is "Missing" or "Failed" or "Running"&&now>=r.NextAttemptAt).OrderBy(r=>r.NextAttemptAt).Select(r=>r.Symbol).Take(20).ToArray();}
    public HourlyWarmupRecord Begin(string symbol,DateTimeOffset hour,HourlyWarmupPlan plan)
    {
        var now=time.GetUtcNow();var record=new HourlyWarmupRecord(Version,symbol,hour,now,now,now.AddMinutes(10),"Running","History",plan.MissingHours,plan.RejectedRows,plan.FetchLimit,0,0,plan.Reason);
        Put(record);return record;
    }
    public HourlyWarmupRecord Update(HourlyWarmupRecord record,string status,string phase,HourlyWarmupPlan plan,int returned,int accepted,string reason,DateTimeOffset next)
    {
        var result=record with{UpdatedAt=time.GetUtcNow(),NextAttemptAt=next,Status=status,Phase=phase,MissingHours=plan.MissingHours,RejectedRows=plan.RejectedRows,ReturnedRows=returned,AcceptedRows=accepted,Reason=reason};
        Put(result);return result;
    }
    private void Put(HourlyWarmupRecord record)
    {
        lock(gate)
        {
            if(Records.TryGetValue(record.Symbol,out var current)&&current.StartedAt>record.StartedAt)throw new InvalidOperationException("旧补数不能覆盖新尝试");
            repository.Save(record);Records[record.Symbol]=record;
        }
    }
    public HourlyWarmupStatus Status()
    {
        lock(gate){var now=time.GetUtcNow();var hour=DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds()/3600*3600);
            return new(now,hour,Records.Values.OrderBy(r=>r.Status=="Ready"?1:0).ThenByDescending(r=>r.UpdatedAt).Take(200).ToArray(),
                "各币最后一次补数资料，跨小时需重新核对；Ready只表示当次小时缓存通过，不等于交易许可。记录跨重启保留，失败重试标的暂离轮换池也继续补数；不补造历史可见性。");}
    }
}
