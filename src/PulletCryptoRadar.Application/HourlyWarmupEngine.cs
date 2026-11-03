using PulletCryptoRadar.Core;
namespace PulletCryptoRadar.Application;

public sealed record HourlyWarmupPlan(bool Required,int FetchLimit,int MissingHours,int RejectedRows,string Reason);
public static class HourlyWarmupEngine
{
    public const string Version="Hourly-Warmup-2";
    public static IReadOnlyList<Candle> Accept(string symbol,IReadOnlyList<Candle> rows,DateTimeOffset end,DateTimeOffset availableAt)=>rows
        .Where(c=>c.Symbol==symbol&&c.IntervalMinutes==60&&c.IsValid&&c.OpenTime.UtcTicks%TimeSpan.TicksPerHour==0
            &&c.EndTime<=end&&c.SourceTime>=c.EndTime.AddSeconds(-2)&&c.SourceTime<c.EndTime&&c.ReceivedTime>=c.SourceTime&&c.ReceivedTime<=availableAt)
        .GroupBy(c=>c.OpenTime).Where(g=>g.Select(c=>(c.Open,c.High,c.Low,c.Close,c.QuoteVolume)).Distinct().Count()==1)
        .Select(g=>g.MinBy(c=>c.ReceivedTime)!).OrderBy(c=>c.OpenTime).ToArray();
    public static HourlyWarmupPlan Plan(string symbol,IReadOnlyList<Candle> saved,DateTimeOffset end,DateTimeOffset availableAt,int requiredHours=TenfoldEngine.RequiredHours)
    {
        if(requiredHours is <1 or >799||end>availableAt||end.UtcTicks%TimeSpan.TicksPerHour!=0)throw new ArgumentException("小时补数边界或长度无效");
        var from=end.AddHours(-requiredHours);var relevant=saved.Where(c=>c.OpenTime>=from&&c.OpenTime<end).ToArray();
        var accepted=Accept(symbol,relevant,end,availableAt);var known=accepted.Select(c=>c.OpenTime).ToHashSet();
        var missing=Enumerable.Range(0,requiredHours).Select(n=>from.AddHours(n)).Where(t=>!known.Contains(t)).ToArray();
        var rejected=relevant.Length-accepted.Count;
        if(missing.Length==0)return new(false,0,0,rejected,"闭合小时、接收顺序与连续范围已齐");
        // A full baseline is needed when rows are physically absent. For rejected old
        // rows, request only far enough back to include the earliest unusable hour.
        var present=relevant.Where(c=>c.Symbol==symbol&&c.IntervalMinutes==60).Select(c=>c.OpenTime).ToHashSet();
        var physicalGap=accepted.Count==0||missing.Any(t=>!present.Contains(t)&&t<=accepted[^1].OpenTime);
        var limit=physicalGap?800:Math.Clamp((int)(end-missing[0]).TotalHours+1,2,800);
        return new(true,limit,missing.Length,rejected,rejected>0?"存在不合格小时，需重新获取来源资料，不修改原接收时间":"小时范围缺失，需补齐闭合来源资料");
    }
}
