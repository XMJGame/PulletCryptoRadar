using System.Globalization;
using System.Numerics;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Application;
public static class DailyResearchEngine
{
    public static DailyMonitorReview Monitor(TokenMonitorTarget target,IReadOnlyList<TokenMonitorSnapshot> input,TokenMonitorView? current,DateOnly date,DateTimeOffset now)
    {
        var start=new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue),TimeSpan.FromHours(8));var end=start.AddDays(1);var cutoff=now<end?now:end;
        var samples=input.Where(s=>s.TargetId==target.Id&&s.ContractAddress==target.ContractAddress&&s.SourceTime>=start&&s.SourceTime<end
            &&s.SourceTime<=s.ReceivedTime&&s.ReceivedTime<=now).OrderBy(s=>s.SourceTime).ToArray();
        var completed=(int)Math.Clamp((cutoff-start).TotalMinutes/30,0,48);
        var slots=samples.Select(s=>(int)(s.SourceTime-start).TotalMinutes/30).Where(n=>n<completed).Distinct().Count();
        var first=samples.FirstOrDefault();var last=samples.LastOrDefault();
        var prior=last is not null?input.FirstOrDefault(s=>s.TargetId==target.Id&&s.ContractAddress==target.ContractAddress&&s.Decimals==last.Decimals&&s.Window.AddressSetHash==last.Window.AddressSetHash
            &&s.SourceTime<=s.ReceivedTime&&s.ReceivedTime<=now&&Math.Abs((last.SourceTime-s.SourceTime).TotalHours-24)<=1):null;
        string? released=null;
        if(first?.Vesting is {} f&&last?.Vesting is {} l&&first.Id!=last.Id&&f.RuntimeSha256==l.RuntimeSha256&&first.Decimals==last.Decimals
            &&f.Allocations.Count==l.Allocations.Count)
        {
            var deltas=l.Allocations.Select(a=>
            {var old=f.Allocations.FirstOrDefault(b=>b.Index==a.Index);return old is not null?BigInteger.Parse(a.RawReleased,CultureInfo.InvariantCulture)-BigInteger.Parse(old.RawReleased,CultureInfo.InvariantCulture):(BigInteger?)null;}).ToArray();
            if(deltas.All(d=>d>=0))released=TokenMonitorEngine.Amount(deltas.Aggregate(BigInteger.Zero,(n,d)=>n+d!.Value),last.Decimals);
        }
        var events=samples.SelectMany(s=>s.Transfers).Where(t=>t.Chain=="bsc"&&t.ContractAddress==target.ContractAddress&&t.BlockTime>=start&&t.BlockTime<end&&t.BlockTime<=t.ReceivedTime&&t.ReceivedTime<=now)
            .DistinctBy(t=>(t.TransactionHash,t.LogIndex)).ToArray();
        var gaps=new List<string>();
        if(samples.Length==0)gaps.Add("当天没有有效确认区块快照；不能判断没有变化或没有释放");
        if(slots<completed)gaps.Add($"截至所选日时点，{completed-slots}/{completed}个已结束半小时槽没有快照（可能未启动、停机、身份/网络/预算限制）；不是全日在线率");
        if(prior is null)gaps.Add("缺少同观察集合的23～25小时真实基线，暂不能判断24h余额变化");
        if(released is null)gaps.Add("没有同日两次可比锁仓参数，实际记录释放增量未知；普通可调用上界不能替代它");
        gaps.Add("固定地址集合与过滤转账只覆盖部分链上活动；无事件不证明没有交易所流入或多跳分发");
        var sameDay=date==DateOnly.FromDateTime(now.ToOffset(TimeSpan.FromHours(8)).DateTime);
        return new(target.Id,target.Symbol,target.ContractAddress,samples.Length,slots,completed,first?.SourceTime,last?.SourceTime,prior is not null,
            last?.Finding??"只有原始观测或尚未采集，尚无冻结的研究判断；不事后补写原结论",released,
            released is null?null:$"{first!.SourceTime:O} → {last!.SourceTime:O}；合约记账增量，不等于二级市场出售",
            sameDay?current?.EarliestPermitted7Days:null,last is null?null:events.Length,
            sameDay?current?.ConfirmedExchangeInflows:null,sameDay?current?.CandidateExchangeInflows:null,gaps);
    }
    public static IReadOnlyList<DailyOutcomeGroup> Outcomes(IReadOnlyList<TokenCheckReport> reports,IReadOnlyList<TokenFollowup> followups,DateTimeOffset now)
    {
        return reports.SelectMany(r=>new[]{24,72,168}.Select(h=>
        {
            var p=followups.FirstOrDefault(f=>f.ReportId==r.Id&&f.EvaluatedAt<=now)?.Points.FirstOrDefault(p=>p.Hours==h);
            var status=p?.Status=="无可比基准"?"无可比基准":r.AsOf.AddHours(h)>now?"未到观察时间":p?.Status??"等待本地数据";
            var observed=status=="已观察"&&p?.PriceAt<=now&&p.PriceAt>=r.AsOf.AddHours(h)&&p.PriceAt<r.AsOf.AddHours(h+1)&&p.ReturnPercent is not null;
            return new{r.Verdict,Hours=h,Status=status,Observed=observed,Return=observed?p!.ReturnPercent:null,Below=observed&&p!.BelowInvalidation==true};
        })).GroupBy(x=>(x.Verdict,x.Hours)).Select(g=>new DailyOutcomeGroup(g.Key.Verdict,g.Key.Hours,g.Count(),g.Count(x=>x.Observed),g.Count(x=>x.Status=="未到观察时间"),
            g.Count(x=>!x.Observed&&x.Status is not("未到观察时间" or "无可比基准")),g.Count(x=>x.Status=="无可比基准"),g.Any(x=>x.Observed)?g.Where(x=>x.Observed).Average(x=>x.Return):null,g.Count(x=>x.Below))).ToArray();
    }
}
public sealed class DailyResearchService(ITokenMonitorRepository monitors,TokenMonitorService monitoring,ITokenCheckRepository reports,
    ITokenFollowupRepository followups,IDailyResearchRepository archive,TimeProvider time)
{
    public DailyResearchReview Build(DateOnly date)
    {
        var now=time.GetUtcNow();var today=DateOnly.FromDateTime(now.ToOffset(TimeSpan.FromHours(8)).DateTime);
        if(date>today||date<today.AddDays(-30))throw new ArgumentException("研究复盘日期仅支持上海今天及过去30天");
        var missing=new List<string>();var rows=new List<DailyMonitorReview>();IReadOnlyList<TokenMonitorView> views=[];
        try{views=monitoring.Overview();}catch(Exception ex)when(ex is IOException or System.Text.Json.JsonException){missing.Add("监测状态不可读，部分实时分类保持缺失："+ex.GetType().Name);}
        foreach(var target in monitors.Targets())
        {
            try{rows.Add(DailyResearchEngine.Monitor(target,monitors.History(target.Id,400),views.FirstOrDefault(v=>v.Target.Id==target.Id),date,now));}
            catch(Exception ex)when(ex is IOException or System.Text.Json.JsonException or InvalidDataException or FormatException)
            {missing.Add(target.Symbol+"历史记录不可读，不能当成零变化："+ex.GetType().Name);}
        }
        var dayReports=reports.ForFollowup(now.AddDays(-31)).Where(r=>DateOnly.FromDateTime(r.AsOf.ToOffset(TimeSpan.FromHours(8)).DateTime)==date&&r.AsOf<=now).ToArray();
        var observations=new List<TokenFollowup>();
        foreach(var r in dayReports)try{if(followups.Get(r.Id) is {} f)observations.Add(f);}catch(Exception ex)when(ex is IOException or System.Text.Json.JsonException){missing.Add($"报告{r.Id}观察记录暂不可读");}
        return new("Daily-Research-1",date,now,dayReports.Length,rows,DailyResearchEngine.Outcomes(dayReports,observations,now),missing,
            ["先看停机、身份和窗口缺口，再解释地址变化；不因样本少就放松开仓门槛",
            "收益表是所选日生成的快查报告之后的价格表现，按当时判断分组，不是策略回报或胜率；重复/相邻报告高度相关，不能视作独立样本",
            "失败/资料不足报告也保留；未到期、无基准和缺数据分别统计，不用当前价填补，不据此自动调整模拟规则"]);
    }
    public DailyResearchReview? Saved(DateOnly date)=>archive.Latest(date);
    public void CaptureToday()=>archive.Save(Build(DateOnly.FromDateTime(time.GetUtcNow().ToOffset(TimeSpan.FromHours(8)).DateTime)));
}
