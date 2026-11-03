using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Application;

public static class PaperAnalysis
{
    public const string Version="Paper-Analysis-LongShort-1";
    public static string RulesHash=>ForwardEngine.Hash(new ForwardRules{Version=Version,Definition="双向量价/OI分析；多头支撑、空头压力、止损0.5ATR、已确认结构或2R/4R投影；OI24h增幅0～30%，量比使用报价成交额；无地址证据不视为安全。"});
    public static IReadOnlyList<ForwardDecision> Analyze(DateTimeOffset started,MarketView m,IReadOnlyList<Candle> rows,
        DerivativeRuntimeSnapshot oi,DateTimeOffset now,bool research=false)
    {
        var hash=research?ForwardEngine.Hash(new ForwardRules{Version="Opportunity-Structure-Watch-1",Definition="仅供当前观察，不接收为模拟执行信号"}):RulesHash;
        var cohort=new ForwardCohort(Guid.Empty,started,now.AddDays(1),new(){MaximumDecisionDelayMinutes=research?60:10},ForwardEngine.Hash(new()));
        var rank=ForwardEngine.Capture([m],now,new()).Rows.Single();
        var longs=ForwardEngine.Decide(cohort,m,rows,rank,oi,now).Select(d=>
        {
            var p=d.Plan;
            if(p is not null){var unitRisk=p.ReferenceEntry-p.Stop+p.ReferenceEntry*.0025m;var qty=Math.Min(1000/p.ReferenceEntry,6/unitRisk);
                p=p with{Quantity=qty,Margin=qty*p.ReferenceEntry/10,EstimatedRisk=qty*unitRisk};}
            return d with{Id=$"{d.Id}:Long",RulesHash=hash,Direction="Long",Plan=p};
        }).ToArray();
        var end=DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds()/3600*3600);
        var bars=rows.Where(c=>c.Symbol==m.Instrument.Symbol&&c.ReceivedTime<=now&&c.EndTime<=end).TakeLast(337).ToArray();
        var i=bars.Length-1;var missing=longs[0].Status=="Insufficient";
        var result=new List<ForwardDecision>(longs);
        decimal ratio=0,ret=0,atr=0,resistance=0;bool a=false,b=false;List<decimal> lows=[];
        IReadOnlyList<SignalCondition> aConditions=[],bConditions=[];
        if(!missing)
        {
            var c=bars[i];var prior=bars.Skip(i-72).Take(72).ToArray();var low=prior.Min(x=>x.Low);var high=prior.Max(x=>x.High);
            var volume=ForwardEngine.Median(bars.Skip(i-24).Take(24).Select(x=>x.QuoteVolume));ratio=volume>0?c.QuoteVolume/volume:0;ret=c.Close/bars[i-24].Close-1;
            atr=Enumerable.Range(i-13,14).Average(j=>Math.Max(bars[j].High-bars[j].Low,Math.Max(Math.Abs(bars[j].High-bars[j-1].Close),Math.Abs(bars[j].Low-bars[j-1].Close))));
            for(var j=2;j<=i-2;j++)
            {
                var other=new[]{bars[j-2],bars[j-1],bars[j+1],bars[j+2]};
                if(bars[j].High>other.Max(x=>x.High)&&bars[j].High>c.Close)resistance=bars[j].High;
                if(bars[j].Low<other.Min(x=>x.Low)&&bars[j].Low<c.Close)lows.Add(bars[j].Low);
            }
            aConditions=SignalConditionEngine.Check("A","Short",low,high,c.Close,c.Low,c.High,ret,ratio,new());
            bConditions=SignalConditionEngine.Check("B","Short",low,high,c.Close,c.Low,c.High,ret,ratio,new());
            a=aConditions.All(g=>g.Passed);b=bConditions.All(g=>g.Passed);
        }
        foreach(var strategy in new[]{"A","B"})
        {
            var template=longs.Single(d=>d.Strategy==strategy);var triggered=!missing&&(strategy=="A"?a:b);ForwardPlan? plan=null;
            var status=missing?"Insufficient":triggered?"Candidate":"NoSignal";var reason=missing?template.Reason:triggered?"空头量价条件满足，检查已确认压力失效点及OI":"空头条件未触发，不因涨得多而盲目做空";
            if(triggered)
            {
                var entry=m.Price!.Value*.9995m;var stop=resistance+.5m*atr;var distance=stop-entry;
                var liq=entry*(1+1m/10)/(1+.005m);
                if(resistance<=0||atr<=0||distance<=0||stop>=liq-.5m*atr){status="Skipped";reason="空头压力止损不存在或超出逐仓估算强平安全空间";}
                else
                {
                    var support=lows.Where(l=>l<=entry-distance).OrderDescending().Cast<decimal?>().FirstOrDefault();var first=support??entry-2*distance;
                    var support2=lows.Where(l=>l<=first-distance).OrderDescending().Cast<decimal?>().FirstOrDefault();var second=support2??entry-Math.Max(4*distance,entry-first+distance);
                    if(second<=0){status="Skipped";reason="空头目标超出有效正价格范围";}
                    else {var qty=Math.Min(1000/entry,6/(distance+entry*.0025m));plan=new(entry,stop,qty,qty*entry/10,qty*(distance+entry*.0025m),first,second,
                        support is null?"2R风险释放投影":"已确认历史支撑",support2 is null?"风险投影":"已确认历史支撑",atr);}
                }
            }
            result.Add(template with{Id=$"{end.ToUnixTimeSeconds()}:{m.Instrument.Symbol}:{strategy}:Short",Direction="Short",Status=status,Reason=reason,Plan=plan,QuoteVolumeRatio=missing?null:ratio,Return24h=missing?null:ret*100,Conditions=strategy=="A"?aConditions:bConditions});
        }
        return result;
    }
}
