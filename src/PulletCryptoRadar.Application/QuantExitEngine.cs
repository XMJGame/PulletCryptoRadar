using PulletCryptoRadar.Core;
namespace PulletCryptoRadar.Application;
public static class QuantExitEngine
{
    public static (decimal First,decimal Second) Targets(QuantHypothesis d,decimal entry)
    {
        var side=d.Direction=="Long"?1m:-1m;var risk=side*(entry-d.Stop!.Value);
        var levels=d.Levels.Where(l=>l.Kind==(side>0?"Resistance":"Support")&&l.ConfirmedAt<=d.Hour).Select(l=>l.Price).ToArray();
        var first=levels.Where(v=>side*(v-entry)>=2*risk).OrderBy(v=>side*v).Cast<decimal?>().FirstOrDefault()??entry+side*2*risk;
        var second=levels.Where(v=>side*(v-first)>=risk).OrderBy(v=>side*v).Cast<decimal?>().FirstOrDefault()??entry+side*Math.Max(4*risk,side*(first-entry)+risk);
        return(first,second);
    }
    public static QuantExitResult Replay(QuantExitSeed seed,IReadOnlyList<Candle> source,DateTimeOffset availableAt,string policy,int multiplier,QuantStudyParameters p,bool targetFirst=false)
    {
        var side=seed.Direction=="Long"?1m:-1m;var initialRisk=side*(seed.Entry-seed.Stop)*seed.Quantity;var remaining=seed.Quantity;
        var fee=seed.Entry*seed.Quantity*p.FeePerSide*multiplier;decimal funding=0,gross=0;var stop=seed.Stop;var peak=seed.Entry;var stage=0;var ambiguous=0;
        var legs=new List<QuantExitLeg>();var horizon=seed.EntryAt.AddHours(p.HoldHours);var expected=seed.EntryAt;DateTimeOffset? closed=null;
        QuantExitResult End(string state,string reason)=>new(state,closed,state=="Closed"?gross-fee-funding:null,fee,funding,initialRisk,ambiguous,remaining,legs,reason);
        if(seed.Direction is not ("Long" or "Short")||seed.EntryAt.ToUnixTimeSeconds()%3600!=0||seed.Entry<=0||seed.Quantity<=0||seed.Stop<=0||seed.Atr<=0||initialRisk<=0||seed.Target1<=0||seed.Target2<=0||side*(seed.Target1-seed.Entry)<=0||side*(seed.Target2-seed.Target1)<=0||policy is not ("ScaleTail" or "FullAtFirst")||multiplier is <1 or >3||p.FirstFraction<=0||p.SecondFraction<=0||p.FirstFraction+p.SecondFraction>=1||p.HoldHours is <1 or >168||p.TrailAtr<=0||p.FeePerSide<0||p.SlippagePerSide<0||p.AdverseFundingPerHour<0||p.SlippagePerSide*multiplier>=1)
            return End("Invalid","情景种子、目标、退出比例或成本无效");
        var rows=source.Where(c=>c.Symbol==seed.Symbol&&c.IntervalMinutes==60&&c.IsValid&&c.OpenTime>=seed.EntryAt&&c.OpenTime<horizon&&c.EndTime<=availableAt&&c.ReceivedTime<=availableAt
            &&c.SourceTime>=c.EndTime.AddSeconds(-2)&&c.SourceTime<c.EndTime&&c.ReceivedTime>=c.SourceTime).OrderBy(c=>c.OpenTime).ToArray();
        if(rows.GroupBy(c=>c.OpenTime).Any(g=>g.Select(c=>(c.Open,c.High,c.Low,c.Close,c.QuoteVolume)).Distinct().Count()>1))return End("MissingPath","同小时冲突，保留未完成而不选择有利版本");
        rows=rows.DistinctBy(c=>c.OpenTime).ToArray();
        void Close(decimal qty,decimal raw,DateTimeOffset at,Candle bar,string reason)
        {
            qty=Math.Min(qty,remaining);if(qty<=0)return;var fill=raw*(1-side*p.SlippagePerSide*multiplier);var pnl=side*(fill-seed.Entry)*qty;var cost=fill*qty*p.FeePerSide*multiplier;
            gross+=pnl;fee+=cost;remaining-=qty;legs.Add(new(at,bar.OpenTime,qty,fill,pnl,cost,reason));if(remaining==0)closed=at;
        }
        bool StopHit(decimal price)=>side*(price-stop)<=0;
        bool TargetHit(decimal price,decimal target)=>side*(price-target)>=0;
        void TargetsAt(decimal favorable,Candle bar,DateTimeOffset at)
        {
            if(stage==0&&TargetHit(favorable,seed.Target1))
            {Close(policy=="FullAtFirst"?remaining:seed.Quantity*p.FirstFraction,seed.Target1,at,bar,"Target1");stage=1;}
            if(remaining>0&&stage==1&&TargetHit(favorable,seed.Target2))
            {Close(seed.Quantity*p.SecondFraction,seed.Target2,at,bar,"Target2");stage=2;}
        }
        foreach(var bar in rows)
        {
            if(bar.OpenTime!=expected)return End("MissingPath","退出前小时路径存在缺口；不填价或期末强平");expected=bar.EndTime;
            if(StopHit(bar.Open)){Close(remaining,bar.Open,bar.OpenTime,bar,"GapStop");return End("Closed","开盘已越止损，按更差开盘及不利滑点");}
            TargetsAt(bar.Open,bar,bar.OpenTime);if(remaining==0)return End("Closed","开盘已达到目标，按目标价及不利滑点");
            // Full-hour adverse funding for unknown intrabar timestamps; never fabricate a credit.
            funding+=remaining*seed.Entry*p.AdverseFundingPerHour*multiplier;
            var adverse=side>0?bar.Low:bar.High;var favorable=side>0?bar.High:bar.Low;
            var stopped=StopHit(adverse);var targeted=stage==0&&TargetHit(favorable,seed.Target1)||stage==1&&TargetHit(favorable,seed.Target2);
            if(stopped&&targeted)ambiguous++;
            if(stopped&&!targetFirst){Close(remaining,stop,bar.EndTime,bar,"Stop");return End("Closed","小时内顺序未知，采用止损优先情景");}
            TargetsAt(favorable,bar,bar.EndTime);if(remaining==0)return End("Closed","目标退出，触价成交为模型假设");
            if(stopped){Close(remaining,stop,bar.EndTime,bar,"Stop");return End("Closed","目标优先情景后剩余数量止损");}
            if(bar.EndTime>=horizon){Close(remaining,bar.Close,bar.EndTime,bar,"TimeLimit");return End("Closed","完整72h观察到期，按闭合价格及不利滑点");}
            peak=side>0?Math.Max(peak,bar.High):Math.Min(peak,bar.Low);
            if(stage>=2)stop=side>0?Math.Max(stop,peak-p.TrailAtr*seed.Atr):Math.Min(stop,peak+p.TrailAtr*seed.Atr);
        }
        return End(expected<horizon&&availableAt>=horizon?"MissingPath":"RightCensored","观察期或路径未完整，净结果保持Unknown");
    }
}
