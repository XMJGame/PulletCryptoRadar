using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Application;
public static class PaperActivityEngine
{
    public static PaperActivity Build(PaperState state,PaperAuditWindow audit,DateTimeOffset now)
    {
        var start=state.ExecutionStartedAt>now.AddHours(-24)?state.ExecutionStartedAt.Value:now.AddHours(-24);
        var signals=audit.Records.Where(r=>r.Time<=now).SelectMany(r=>r.Signals).Concat(state.SignalObservations)
            .Where(d=>d.DecisionAt>=start&&d.DecisionAt<=now&&d.CapturedAt>=d.DecisionAt&&d.CapturedAt<=now)
            .GroupBy(d=>d.Id).Select(g=>g.MinBy(d=>d.CapturedAt)!).OrderBy(d=>d.DecisionAt).ToArray();
        var events=audit.Records.Where(r=>r.Time<=now).SelectMany(r=>r.Decisions).Concat(state.Decisions)
            .Where(d=>d.Time>=start&&d.Time<=now).DistinctBy(d=>(d.Time,d.Symbol,d.Action,d.Reason,d.SignalKey)).OrderBy(d=>d.Time).ToArray();
        bool Oi(ForwardDecision d)=>d.Status=="Candidate"&&d.Plan is not null&&d.Oi24hPercent is >=0 and <=30&&d.OiSourceTime is not null&&d.OiReceivedTime is not null
            &&d.OiSourceTime<=d.OiReceivedTime&&d.OiReceivedTime<=d.CapturedAt;
        var hourly=signals.GroupBy(d=>d.DecisionAt).Select(g=>new PaperAnalysisHour(g.Key,g.Select(d=>d.Symbol).Distinct().Count(),g.Count(),g.Count(d=>d.Status=="Insufficient"),
            g.Count(d=>d.Status=="NoSignal"),g.Count(d=>d.Status=="Skipped"),g.Count(d=>d.Status=="Candidate"),g.Count(Oi))).OrderByDescending(h=>h.Hour).ToArray();
        var candidates=signals.Count(d=>d.Status=="Candidate");var ready=signals.Count(Oi);var openings=events.Count(d=>d.Action=="实验模拟开仓");
        var expectedHour=DateTimeOffset.FromUnixTimeSeconds(now.AddMinutes(-10).ToUnixTimeSeconds()/3600*3600);
        var currentHour=DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds()/3600*3600);
        var collecting=state.AnalysisHour==currentHour&&state.SignalHour!=currentHour&&now-currentHour<TimeSpan.FromMinutes(10);
        var overdue=(state.SignalHour is null||state.SignalHour<expectedHour)&&!collecting;
        var latest=hourly.FirstOrDefault();
        var health=now-state.UpdatedAt>TimeSpan.FromSeconds(30)?"账本停更":!state.Enabled?"新仓已暂停":state.EntryBlockReason is not null?"新仓受阻":
            overdue?"分析待完成":state.Pending.Count>0?"等待下一报价":collecting?"本小时分析进行中":state.Positions.Count>=state.Rules.MaximumPositions?"持仓已满":
            latest is null||latest.Candidates==0?"本轮没有结构候选":latest.OiEligible==0?"本轮候选OI未通过":"存在候选，检查执行条件";
        var summary=health switch{"账本停更"=>"账户超过30秒未更新，应先查模拟任务与采集。","新仓已暂停"=>"用户暂停了新仓，已有持仓仍按规则检查退出。","新仓受阻"=>state.EntryBlockReason!,
            "分析待完成"=>"最近应完成的小时分析尚未完成；请查预热与调度。刚启用仍可能在等待首次小时闭合。",
            "本小时分析进行中"=>$"本小时已保存{hourly.FirstOrDefault(h=>h.Hour==currentHour)?.Symbols??0}/{state.AnalysisSymbols.Count}个合约的判断，其余仍在前10分钟采集窗口内等待预热；当前统计尚未完成。",
            "等待下一报价"=>"已产生意向，等待晚于信号的新鲜价格及标记价。","持仓已满"=>"已达到同时持仓上限，不能继续分配份额。","本轮没有结构候选"=>"最新分析小时没有通过量价及结构计划检查的候选；有空闲份额也不会强行开仓。",
            "本轮候选OI未通过"=>"最新小时结构候选尚未通过OI证据门槛，未进入成交等待。",_=>"最新小时有候选；可能已持有同币、冷却、报价或后续执行条件不满足，按逐项事件核对。"};
        return new(now,start,state.UpdatedAt,health,summary,state.Enabled,state.Positions.Count,state.Rules.MaximumPositions,state.Pending.Count,state.AnalysisSymbols.Count,state.LastAnalysisAttempt,state.SignalHour,
            signals.Length,signals.Select(d=>d.Symbol).Distinct().Count(),hourly.Length,signals.Count(d=>d.Status=="Insufficient"),signals.Count(d=>d.Status=="NoSignal"),signals.Count(d=>d.Status=="Skipped"),candidates,ready,
            events.Count(d=>d.Action=="等待下一报价"),openings,events.Count(d=>d.Action=="取消模拟意向"),audit.Truncated,signals.FirstOrDefault()?.DecisionAt,
            signals.GroupBy(d=>d.Reason).Select(g=>new ActivityCount(g.Key,g.Count())).OrderByDescending(c=>c.Count).Take(12).ToArray(),
            signals.Where(d=>d.Status=="NoSignal").SelectMany(d=>d.Conditions.Where(c=>!c.Passed)).GroupBy(c=>c.Name).Select(g=>new ActivityCount(g.Key,g.Count())).OrderByDescending(c=>c.Count).ToArray(),
            hourly,signals.Where(d=>d.Status is "Skipped" or "Candidate").OrderByDescending(d=>d.CapturedAt).Take(20).ToArray(),events.Where(d=>d.Symbol!="账户").TakeLast(30).Reverse().ToArray(),
            ["统计来自原始小时审计及当前账本，最多读取1000个审计批次；截断或采集断档时不是完整24h覆盖。一次A/B×多空是4条分析，不是4次独立机会。",
            "结构候选和OI符合只是中间阶段；已有同币、24h冷却、风险额度、强平距离、价格时效等仍可能阻止成交。统计不证明策略有盈利能力。",
            "逐项量价条件从新版发布后的原判断同时保存；旧判断只有原原因，不事后套用当前行情补写。条件失败可同时发生，计数不能相加当作独立标的数。",
            "没有每日必须开仓的数量目标。当前风险规则与信号阈值未因本轮诊断放宽；策略是否过窄需要另建版本前瞻比较。"]);
    }
}
