using PulletCryptoRadar.Core;
namespace PulletCryptoRadar.Application;

// Uses the complete saved ledger, independently of the 30-row daily path display.
public static class QuantReviewEngine
{
    public const string Version="Quant-Review-1";
    public static QuantReview Build(DateOnly date,DateTimeOffset now,PaperState state,IReadOnlyList<PaperTrade> allTrades,
        IReadOnlyList<QuantFundingEvent> funding,QuantDailyReport daily)
    {
        var control=state.QuantControl??throw new InvalidOperationException("新执行尚未启用");
        bool Matches(PaperIntent intent,QuantLivePosition? meta,DateTimeOffset opened)=>meta is not null&&opened>=control.ActivatedAt&&opened<=now
            &&intent.QuantIntent?.Decision.RulesHash==control.RulesHash&&QuantRules.Hash(meta.Parameters,meta.Costs)==control.RulesHash;
        var positions=state.Positions.Where(p=>Matches(p.Intent,p.Quant,p.OpenedAt)).ToDictionary(p=>p.Id);
        var trades=allTrades.Where(t=>t.Version==control.Version&&t.PositionId is not null&&t.ClosedAt<=now&&Matches(t.Evidence,t.Quant,t.OpenedAt)).ToArray();
        var ids=positions.Keys.Concat(trades.Select(t=>t.PositionId!.Value)).Distinct().ToArray();
        var rows=ids.Select(id=>
        {
            var p=positions.GetValueOrDefault(id);var source=trades.Where(t=>t.PositionId==id).ToArray();var legs=source.DistinctBy(t=>t.Id).OrderBy(t=>t.ClosedAt).ToArray();
            var meta=p?.Quant??legs[0].Quant!;var intent=p?.Intent??legs[0].Evidence;var opened=p?.OpenedAt??legs[0].OpenedAt;
            var d=intent.QuantIntent!.Decision;var result=QuantLifecycleEngine.Build(id,p,source,funding,(state.QuantFunding??[]).FirstOrDefault(c=>c.PositionId==id),now);
            // Missing or conflicting final flags remain visible if any settlement belongs to the period.
            var closed=result.ClosedAt??(p is null&&legs.Length>0?legs[^1].ClosedAt:(DateTimeOffset?)null);var complete=result.Status=="Complete"&&result.FinalNet is not null&&meta.Plan.EstimatedRisk>0;
            var gross=legs.Sum(t=>t.GrossPnl);var fees=legs.Sum(t=>t.Fees);var actualFunding=legs.Sum(t=>t.FundingCost)+result.LateFundingAdjustment;
            var net=result.FinalNet;var risk=meta.Plan.EstimatedRisk;var beyond=complete?Math.Max(0,-net!.Value-risk):(decimal?)null;
            var reason=result.Status=="Complete"?"数量、费用和已观察资金费范围核对通过":result.Status=="Open"?"仍持仓，不纳入完整交易指标":
                string.Join("；",result.Checks.Where(c=>c.Status is "Fail" or "Unknown").Select(c=>c.Name+"："+c.Reason));
            var status=result.Status=="Complete"&&risk<=0?"Incomplete":result.Status;if(risk<=0)reason="缺少有效原计划风险，不能计算R或纳入完整交易指标";
            return new QuantReviewTrade(id,d.Symbol,d.Strategy,d.Direction,intent.QuantIntent.Assessment.Context?.State??"Unknown",opened,closed,p is not null,status,risk,
                complete?gross:null,complete?fees:null,complete?actualFunding:null,complete?fees+actualFunding:null,complete?net:null,
                complete&&risk>0?net/risk:null,complete&&gross!=0?100*(fees+actualFunding)/Math.Abs(gross):null,beyond,complete&&risk>0?100*beyond/risk:null,reason){Attribution=result.Attribution};
        }).ToArray();
        var from=QuantDailyEngine.Start(date);var end=from.AddDays(1);var weekFrom=from.AddDays(-(((int)date.DayOfWeek+6)%7));var weekEnd=weekFrom.AddDays(7);
        QuantReviewPeriod Period(string kind,DateTimeOffset start,DateTimeOffset finish)
        {
            var selected=rows.Where(r=>r.OpenNow?r.OpenedAt<finish:r.ClosedAt>=start&&r.ClosedAt<finish).ToArray();
            var confirmed=selected.Where(r=>r.Status=="Complete").ToArray();
            var groups=new[]{("Direction",(Func<QuantReviewTrade,string>)(r=>r.Direction)),("Strategy",r=>r.Strategy),("Environment",r=>r.Environment)}
                .SelectMany(pair=>confirmed.GroupBy(pair.Item2).OrderBy(g=>g.Key,StringComparer.Ordinal).Select(g=>new QuantReviewGroup(pair.Item1,g.Key,Metrics(g.ToArray())))).ToArray();
            return new(kind,start,now<finish?now:finish,selected.Count(r=>r.OpenNow),selected.Count(r=>r.Status=="AwaitFunding"),selected.Count(r=>r.Status=="Incomplete"),
                selected.Count(r=>r.Status=="Inconsistent"),Metrics(confirmed),groups,selected.OrderByDescending(r=>r.ClosedAt??r.OpenedAt).Take(100).ToArray(),selected.Length>100);
        }
        var exitLegs=trades.Where(t=>t.ClosedAt>=from&&t.ClosedAt<end).GroupBy(t=>t.Id).Select(g=>g.First()).Select(QuantExitTimingEngine.Build).ToArray();
        QuantReviewTiming Timing(string metric,Func<QuantExitTiming,decimal?> value)
        {
            var values=exitLegs.Select(value).Where(v=>v is >=0).Select(v=>v!.Value).Order().ToArray();
            decimal? At(decimal percentile)=>values.Length>0?values[Math.Max(0,(int)Math.Ceiling(values.Length*percentile)-1)]:null;
            return new(metric,values.Length,At(.5m),At(.95m),values.Length>0?values[^1]:null);
        }
        var quality=new QuantReviewQuality(exitLegs.Length,exitLegs.Count(t=>t.Status=="Recorded"),exitLegs.Count(t=>t.Status=="HistoricalPartial"),exitLegs.Count(t=>t.Status=="Invalid"),
            [Timing("ReceiveToObservation",t=>t.Status=="Recorded"?t.ReceiveToObservationMs:null),Timing("ObservationToComputation",t=>t.Status=="Recorded"?t.ObservationToComputationMs:null),
             Timing("BookReceiveToComputation",t=>t.BookReceiveToComputationMs)],daily.AccountStale,daily.Hours.Count(h=>h.Status!="Collecting"),daily.Hours.Count(h=>h.Status=="MissingUniverse"),
            daily.Hours.Count(h=>h.Status=="Incomplete"),daily.InputIssues.Count(i=>i.Category=="Baseline"));
        var confirmedCount=rows.Count(r=>r.Status=="Complete");var inconsistent=rows.Count(r=>r.Status=="Inconsistent");var elapsed=Math.Max(0,(now-control.ActivatedAt).Ticks/(decimal)TimeSpan.TicksPerDay);
        var readiness=new QuantReviewReadiness(inconsistent>0||quality.InvalidTiming>0||daily.AccountStale||rows.Any(r=>r.Status=="Incomplete")||quality.MissingHours>0||quality.IncompleteHours>0?"CheckEvidence":elapsed<30||confirmedCount<100?"Collecting":"ReviewRequired",
            elapsed,confirmedCount,30,100,positions.Count,rows.Count(r=>r.Status=="AwaitFunding"),rows.Count(r=>r.Status=="Incomplete"),inconsistent,control.ActivatedAt.AddDays(30),
            "先按30个经过日及100笔已核对完整交易检查研究样本是否具备评审条件；这是原计划最低建议，通常仍需30～60日/100～200笔及成本压力、集中度和不确定性评估。达到数量只进入评审，不自动确认盈利或修改规则。");
        return new(Version,now,Period("Day",from,end),Period("Week",weekFrom,weekEnd),quality,readiness,
            ["仅当前启用版本、规则哈希及生效后的原始账本；不混入旧仓或价格回放。指标使用全部账本，列表最多100笔，独立于持仓路径30笔显示限制。",
             "日报/周报整笔结果按最终退出日归属，周一00:00至下周一00:00均按上海时间。包括截至本次报告已知的迟到资金费；按入账日的当日结算在日报上方另列，两种净额不能相加。未平仓与待补费/不完整/不一致记录不纳入完整交易指标。",
             "R=整笔净额/入场含成本计划风险；亏损超支=max(0,实际净亏损−计划风险)。净成本=双边手续费+有符号实际资金费（正付负收）；汇总成本占比以每笔毛盈亏绝对值之和为分母，毛额为0保留未知。",
             "利润因子=完整交易盈利之和/净亏损绝对值之和；没有亏损样本保留未知，不显示无穷大。均值、胜率与R均为描述，单笔或小样本不证明正期望。",
             "退出计时按当日退出腿描述，不是独立完整交易样本。P50/P95用最近秩定义，样本不足不评价快慢；旧资料仅有部分时序，不补造引擎判定时刻。范围完整不保证供应方无遗漏。",
             "本模块不计算缺少日初完整净值的账户日收益率或全账户最大回撤，不因日目标强行交易或自动调参。冻结时基线未就绪属于历史覆盖问题，补数后不追改旧判断。"]);
    }
    public static QuantReviewMetrics Metrics(IReadOnlyList<QuantReviewTrade> rows)
    {
        var complete=rows.Where(r=>r.Status=="Complete"&&r.NetPnl is not null).ToArray();var n=complete.Length;
        var gross=complete.Sum(r=>r.GrossPnl!.Value);var fees=complete.Sum(r=>r.Fees!.Value);var funds=complete.Sum(r=>r.Funding!.Value);var net=complete.Sum(r=>r.NetPnl!.Value);
        var wins=complete.Count(r=>r.NetPnl>0);var losses=complete.Count(r=>r.NetPnl<0);var profit=complete.Where(r=>r.NetPnl>0).Sum(r=>r.NetPnl!.Value);var loss=-complete.Where(r=>r.NetPnl<0).Sum(r=>r.NetPnl!.Value);
        var rs=complete.Where(r=>r.NetR is not null).Select(r=>r.NetR!.Value).ToArray();var absoluteGross=complete.Sum(r=>Math.Abs(r.GrossPnl!.Value));
        return new(n,wins,losses,n-wins-losses,gross,fees,funds,fees+funds,net,complete.Sum(r=>r.PlannedRisk),n>0?100m*wins/n:null,n>0?net/n:null,
            rs.Length==n&&n>0?rs.Average():null,loss>0?profit/loss:null,absoluteGross>0?100*(fees+funds)/absoluteGross:null,complete.Count(r=>r.LossBeyondPlan>0),
            complete.Sum(r=>r.LossBeyondPlan??0),rs.Length>0?rs.Min():null);
    }
}
