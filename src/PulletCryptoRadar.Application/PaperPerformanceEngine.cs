using PulletCryptoRadar.Core;
namespace PulletCryptoRadar.Application;

public static class PaperPerformanceEngine
{
    public static PaperPerformance Build(DateOnly date,DateTimeOffset now,PaperEquitySample current,IReadOnlyList<PaperEquityPoint> history,
        IReadOnlyList<PaperEquitySample> boundaries,DateTimeOffset? recordingStartedAt)
    {
        var start=QuantDailyEngine.Start(date);var end=start.AddDays(1);var ended=now>=end;
        var through=ended?end:now;var window=now.AddDays(-90);
        var points=history.Where(p=>p.CapturedAt>=window&&p.CapturedAt<=current.CapturedAt&&p.Revision<=current.Revision)
            // A stopped account can now be stale at the same revision which was valid when saved.
            // Preserve that historical observation; only the live closing value becomes unknown.
            .Append(current.Point()).GroupBy(p=>p.Revision).Select(g=>g.First()).OrderBy(p=>p.CapturedAt).ThenBy(p=>p.Revision).ToArray();
        // First observation is deterministic; do not skip an invalid opening to select a favourable later balance.
        PaperEquityPoint? Boundary(DateTimeOffset at)=>boundaries.Where(p=>p.CapturedAt>=at&&p.CapturedAt<at.AddMinutes(1)&&p.CapturedAt<=current.CapturedAt&&p.Revision<=current.Revision)
            .OrderBy(p=>p.CapturedAt).ThenBy(p=>p.Revision).FirstOrDefault()?.Point();
        var opening=Boundary(start);var closing=ended?Boundary(end):current.CapturedAt>=start&&current.CapturedAt<=now?current.Point():null;
        var status=opening is null?"MissingOpening":opening.Equity is null?"InvalidOpening":closing is null?"MissingClosing":closing.Equity is null?"InvalidClosing":opening.Equity<=0?"InvalidBase":"Ready";
        decimal? net=status is "Ready" or "InvalidBase"?closing!.Equity-opening!.Equity:null;
        var dayPoints=points.Where(p=>p.CapturedAt>=start&&p.CapturedAt<=through).ToArray();
        var dayGaps=Gaps(dayPoints,start,through);
        var day=new PaperPerformanceDay(date.ToString("yyyy-MM-dd"),start,through,ended,status,opening,closing,net,
            status=="Ready"&&net is {} n?100*n/opening!.Equity:null,dayPoints.Length,dayPoints.Count(p=>p.Equity is null),dayGaps.Count,dayGaps.Longest);
        decimal? peak=null,maximum=null;DateTimeOffset? peakAt=null,maxPeakAt=null,maxTroughAt=null;
        foreach(var p in points.Where(p=>p.Equity is not null))
        {
            if(peak is null||p.Equity>peak){peak=p.Equity;peakAt=p.CapturedAt;}
            if(peak<=0)continue;
            var decline=100*(peak-p.Equity)/peak;
            if(maximum is null||decline>maximum){maximum=decline;maxPeakAt=peakAt;maxTroughAt=p.CapturedAt;}
        }
        var drawdownFrom=recordingStartedAt is {} first&&first>window?first:window;
        if(drawdownFrom>now)drawdownFrom=now;
        var gaps=Gaps(points,drawdownFrom,now);
        var drawdown=new PaperDrawdown(drawdownFrom,now,points.FirstOrDefault(p=>p.Equity is not null)?.CapturedAt,points.Count(p=>p.Equity is not null),
            points.Count(p=>p.Equity is null),gaps.Count,gaps.Longest,peak,peakAt,current.Equity is {} equity&&peak>0?100*(peak-equity)/peak:null,maximum,maxPeakAt,maxTroughAt);
        return new(PaperEquityRecorder.Version,now,recordingStartedAt,current,current.Equity-current.InitialCash,day,drawdown,
            ["净值是已提交模拟账本的现金＋剩余保证金＋标记毛浮盈亏＋旧模型估值调整；已付开仓费和已入账资金费不再重复扣除，未含未来退出费用。",
             "日初/日末采用上海零点后60秒内首个保存采样，按所示实际时间计算观测区间收益，不声称精确覆盖零点；中途启动、首样无效或缺失不算完整日收益。",
             "回撤仅覆盖最近90日内本版本保存的可信采样，不导入旧滚动曲线；断档、无效样本和采样间极值可能低估真实回撤，不把未知显示为0。",
             "当前/旧规则仅分摊已结算及未平仓贡献，共用同一账户；分组贡献不等于各自独立账户收益率。",
             "最近约10分钟实际资金费暂记，旧仓仍使用原费用与强平估算模型；观察窗口通过不证明供应方无遗漏。"]);
    }
    private static (int Count,decimal Longest) Gaps(IReadOnlyList<PaperEquityPoint> points,DateTimeOffset from,DateTimeOffset through)
    {
        var last=from;var count=0;decimal longest=0;
        foreach(var at in points.Select(p=>p.CapturedAt).Where(t=>t>=from&&t<=through).Append(through))
        {var seconds=(decimal)(at-last).TotalSeconds;if(seconds>125)count++;longest=Math.Max(longest,seconds);last=at;}
        return(count,longest);
    }
}
