using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Application;

public static class ForwardEngine
{
    public static string Hash(ForwardRules rules)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(rules))));
    public static DateTimeOffset Bucket(DateTimeOffset t)=>DateTimeOffset.FromUnixTimeSeconds(t.ToUnixTimeSeconds()/300*300);
    public static bool Fresh(MarketView m,DateTimeOffset now,ForwardRules r)=>m.Instrument.Active&&m.Price>0&&m.ExchangeReturn24h is not null
        &&m.PriceSourceTime is {} source&&source<=now&&now-source<=TimeSpan.FromSeconds(r.PriceAgeSeconds)
        &&m.PriceReceivedTime is {} received&&received<=now&&received>=source&&now-received<=TimeSpan.FromSeconds(r.PriceAgeSeconds);
    public static ForwardSnapshot Capture(IReadOnlyList<MarketView> markets,DateTimeOffset now,ForwardRules rules)
    {
        var ranks=markets.Where(m=>Fresh(m,now,rules)).OrderByDescending(m=>m.ExchangeReturn24h).ThenBy(m=>m.Instrument.Symbol,StringComparer.Ordinal)
            .Select((m,i)=>(m.Instrument.AssetId,Rank:i+1)).ToDictionary(x=>x.AssetId,x=>x.Rank);
        return new(now,Bucket(now),markets.Where(m=>m.Instrument.Active).OrderBy(m=>m.Instrument.Symbol,StringComparer.Ordinal)
            .Select(m=>new ForwardRank(m.Instrument.AssetId,m.Instrument.Symbol,ranks.TryGetValue(m.Instrument.AssetId,out var rank)?rank:null,
                m.Price,m.ExchangeReturn24h,m.Volume24h,m.PriceSourceTime,m.PriceReceivedTime,Fresh(m,now,rules),m.Features.Derivatives.OpenInterestQuantity,
                m.FundingRate,m.FundingSourceTime,m.Features.Capital.TrustedIdentity,m.Features.OnChain.HolderCount.Value is not null)).ToArray());
    }
    public static decimal Median(IEnumerable<decimal> source)
    {var a=source.Order().ToArray();return a.Length==0?0:a.Length%2==0?(a[a.Length/2-1]+a[a.Length/2])/2:a[a.Length/2];}
    public static IReadOnlyList<Candle> Hourly(IReadOnlyList<Candle> saved,IReadOnlyList<Candle> five,DateTimeOffset now)
    {
        var grouped=five.Where(c=>c.IntervalMinutes==5&&c.IsValid&&c.EndTime<=now&&c.ReceivedTime<=now)
            .GroupBy(c=>c.OpenTime.ToUnixTimeSeconds()/3600).Select(g=>g.OrderBy(c=>c.OpenTime).ToArray())
            .Where(g=>g.Length==12&&g[0].OpenTime.Minute==0&&Enumerable.Range(1,11).All(i=>g[i].OpenTime==g[i-1].EndTime))
            .Select(g=>new Candle(g[0].Symbol,60,g[0].OpenTime,g[0].Open,g.Max(c=>c.High),g.Min(c=>c.Low),g[^1].Close,g.Sum(c=>c.QuoteVolume),g[^1].SourceTime,g.Max(c=>c.ReceivedTime)));
        return saved.Concat(grouped).Where(c=>c.IntervalMinutes==60&&c.IsValid&&c.EndTime<=now&&c.ReceivedTime<=now)
            .GroupBy(c=>c.OpenTime).Select(g=>g.OrderBy(c=>c.ReceivedTime).First()).OrderBy(c=>c.OpenTime).ToArray();
    }
    public static IReadOnlyList<ForwardDecision> Decide(ForwardCohort cohort,MarketView market,IReadOnlyList<Candle> history,
        ForwardRank rank,DerivativeRuntimeSnapshot derivatives,DateTimeOffset now)
    {
        var rules=cohort.Rules;var end=DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds()/3600*3600);
        var bars=history.Where(c=>c.Symbol==market.Instrument.Symbol&&c.EndTime<=end&&c.ReceivedTime<=now).TakeLast(rules.MinimumHours).ToArray();
        var count=bars.Length;string? missing=count<rules.MinimumHours?"历史预热不足337根小时线":null;
        if(missing is null&&(bars[^1].EndTime!=end||Enumerable.Range(1,count-1).Any(i=>bars[i].OpenTime!=bars[i-1].EndTime)))missing="小时基线存在缺口，或最新小时尚未完整";
        if(!rank.Fresh)missing="报价失鲜或无效，不生成入场计划";
        if(now-end>TimeSpan.FromMinutes(rules.MaximumDecisionDelayMinutes))missing="本小时采集延迟超过10分钟，不补造信号";
        var signalEnd=bars.LastOrDefault()?.EndTime??end;
        if(signalEnd<cohort.StartedAt)missing="信号小时早于本轮起点，仅作预热，不补历史候选";
        decimal? ratio=null,ret=null,oi=null;DateTimeOffset? oiTime=null,oiReceived=null;var oiStatus="Unknown：缺少当时可见的连续OI";
        var source=derivatives.Series.GetValueOrDefault(DerivativeKind.OpenInterest5m)??[];
        var visible=source.Where(s=>s.Quantity>0&&s.SourceTime<=end.AddMinutes(-5)&&s.ReceivedTime<=now&&s.ReceivedTime>=s.SourceTime).OrderBy(s=>s.SourceTime).ToArray();
        var latest=visible.LastOrDefault();
        if(latest is not null&&end.AddMinutes(-5)-latest.SourceTime<=TimeSpan.FromMinutes(15))
        {
            var old=visible.LastOrDefault(s=>s.SourceTime<=end.AddMinutes(-5).AddHours(-24));
            if(old is not null&&end.AddMinutes(-5).AddHours(-24)-old.SourceTime<=TimeSpan.FromMinutes(15))
            {oi=(latest.Quantity!.Value/old.Quantity!.Value-1)*100;oiTime=latest.SourceTime;oiReceived=latest.ReceivedTime;oiStatus=oi is >=0 and <=30?"范围内：数量24h增幅0～30%":"反证：数量24h增幅不在0～30%";}
        }
        bool a=false,b=false;decimal atr=0,support=0;List<decimal> pressures=[];
        if(missing is null)
        {
            var i=bars.Length-1;var c=bars[i];var prior=bars.Skip(i-72).Take(72).ToArray();var low=prior.Min(x=>x.Low);var high=prior.Max(x=>x.High);
            var median=Median(bars.Skip(i-24).Take(24).Select(x=>x.QuoteVolume));ratio=median>0?c.QuoteVolume/median:0;ret=c.Close/bars[i-24].Close-1;
            atr=Enumerable.Range(i-13,14).Average(j=>Math.Max(bars[j].High-bars[j].Low,Math.Max(Math.Abs(bars[j].High-bars[j-1].Close),Math.Abs(bars[j].Low-bars[j-1].Close))));
            for(var j=Math.Max(2,i-335);j<=i-2;j++)
            {
                var other=new[]{bars[j-2],bars[j-1],bars[j+1],bars[j+2]};
                if(bars[j].Low<other.Min(x=>x.Low)&&bars[j].Low<c.Close)support=bars[j].Low;
                if(bars[j].High>other.Max(x=>x.High)&&bars[j].High>c.Close)pressures.Add(bars[j].High);
            }
            a=high/low-1<=rules.CompressionMax&&c.Close>=low+(high-low)*.7m&&c.Close<=high&&ret is >=0 and <=.12m&&ratio>=rules.VolumeRatioA;
            b=ret is >=.08m and <=.4m&&c.Close>high*(1+rules.BreakoutBuffer)&&ratio>=rules.VolumeRatioB&&c.High>c.Low&&(c.Close-c.Low)/(c.High-c.Low)>=.7m;
        }
        var result=new List<ForwardDecision>();
        foreach(var strategy in new[]{"A","B"})
        {
            var candidate=strategy=="A"?a:b;var status=missing is not null?"Insufficient":candidate?"Candidate":"NoSignal";
            var reason=missing??(candidate?"量价条件满足，计划仅按当时参考报价计算；OI另列支持或反证":"量价条件未满足；保留未启动/失败对照");ForwardPlan? plan=null;
            if(candidate&&missing is null)
            {
                var entry=market.Price!.Value*(1+rules.SlippagePerSide);var stop=support-.5m*atr;
                var liquidation=entry*(1-1/rules.Leverage)/(1-rules.MaintenanceRate);
                if(support<=0||atr<=0||stop<=0||stop>=entry){status="Skipped";reason="没有已确认且低于入场的有效支撑";}
                else if(stop<=liquidation+.5m*atr){status="Skipped";reason="结构止损超出10倍逐仓估算强平安全空间";}
                else
                {
                    var risk=entry-stop;var qty=Math.Min(rules.MarginCap*rules.Leverage/entry,rules.ReferenceRisk/(risk+entry*(2*rules.FeePerSide+rules.SlippagePerSide+.001m)));
                    var target1=pressures.Where(p=>p>=entry+risk).Order().Cast<decimal?>().FirstOrDefault();
                    var first=target1??entry+2*risk;var target2=pressures.Where(p=>p>=first+risk).Order().Cast<decimal?>().FirstOrDefault();
                    plan=new(entry,stop,qty,qty*entry/rules.Leverage,qty*(risk+entry*(2*rules.FeePerSide+rules.SlippagePerSide+.001m)),first,
                        target2??entry+Math.Max(4*risk,first-entry+risk),target1 is null?"2R风险释放投影":"已确认历史压力",target2 is null?"风险投影":"已确认历史压力",atr);
                }
            }
            result.Add(new($"{end.ToUnixTimeSeconds()}:{market.Instrument.Symbol}:{strategy}",market.Instrument.Symbol,strategy,end,now,status,reason,count,rank.Rank,ratio,ret*100,
                oi,oiTime,oiReceived,oiStatus,plan,cohort.RulesHash));
        }
        return result;
    }
    public static ForwardOutcome Evaluate(ForwardDecision d,int hours,IReadOnlyList<ForwardSnapshot> snapshots,DateTimeOffset now,ForwardRules rules)
    {
        var due=d.CapturedAt.AddHours(hours);var samples=snapshots.Where(s=>s.CapturedAt>d.CapturedAt&&s.CapturedAt<=due)
            .Select(s=>(Snapshot:s,Row:s.Rows.FirstOrDefault(r=>r.Symbol==d.Symbol))).Where(p=>p.Row is {Fresh:true,Price:>0}&&p.Row.SourceTime>d.CapturedAt
                &&p.Row.SourceTime<=p.Snapshot.CapturedAt&&p.Row.ReceivedTime>=p.Row.SourceTime&&p.Row.ReceivedTime<=p.Snapshot.CapturedAt)
            .OrderBy(p=>p.Snapshot.CapturedAt).ToArray();
        if(now<due)throw new ArgumentException("观察期未到，禁止提前发布未来标签");
        var expected=hours*60/rules.SnapshotMinutes;var end=samples.LastOrDefault();
        var gaps=samples.Length==0||samples[0].Snapshot.CapturedAt-d.CapturedAt>TimeSpan.FromSeconds(rules.MaximumCoverageGapSeconds)
            ||due-samples[^1].Snapshot.CapturedAt>TimeSpan.FromSeconds(rules.MaximumCoverageGapSeconds)
            ||samples.Zip(samples.Skip(1)).Any(p=>p.Second.Snapshot.CapturedAt-p.First.Snapshot.CapturedAt>TimeSpan.FromSeconds(rules.MaximumCoverageGapSeconds));
        var complete=!gaps&&samples.Length>=expected*.9m;var entry=d.Plan!.ReferenceEntry;
        decimal Pct(decimal price)=>(price/entry-1)*100;
        var terminal=end.Row is not null&&due-end.Snapshot.CapturedAt<=TimeSpan.FromSeconds(rules.MaximumCoverageGapSeconds)?end.Row.Price:null;
        var stop=samples.Any(p=>p.Row!.Price<=d.Plan.Stop);var t1=samples.Any(p=>p.Row!.Price>=d.Plan.Target1);var t2=samples.Any(p=>p.Row!.Price>=d.Plan.Target2);
        return new($"{d.Id}:{hours}",d.Id,hours,due,now,complete?"Observed":"Incomplete",samples.Length,expected,complete,terminal,terminal is null?null:Pct(terminal.Value),
            samples.Length==0?null:Pct(samples.Max(p=>p.Row!.Price!.Value)),samples.Length==0?null:Pct(samples.Min(p=>p.Row!.Price!.Value)),
            stop?true:complete?false:null,t1?true:complete?false:null,t2?true:complete?false:null,
            "5分钟采样标签：未采样到的触碰不可排除；目标/止损只标记曾观测越线，不能确定可成交或先后，未计算净利润/资金费，不向模拟账户记账。");
    }
}
