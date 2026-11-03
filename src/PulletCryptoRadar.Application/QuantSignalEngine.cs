using System.Security.Cryptography;
using System.Text.Json;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Application;
public static class QuantRules
{
    public const string Definition="Shadow only; UTC-closed 1h/4h EMA20/60 and 6h slope; 3-bar EMA20 pullback then previous-bar break; prior20h ATR-buffered breakout; right2 pivots; OI quantity1h/24h; cost+funding-reserve; 2R/4R or confirmed levels; risk/correlation reservations; no execution; no return guarantee.";
    public static string Hash(QuantParameters p,ExecutionCostModel? costs=null)=>HashValue(new{Definition,Parameters=p,Execution=costs??new ExecutionCostModel()});
    public static string HashValue<T>(T value)=>Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value))).ToLowerInvariant();
    public static QuantMarketInput Input(MarketView m)=>new(m.Instrument.AssetId,m.Instrument.Symbol,m.Instrument.BaseAsset,m.Instrument.Active,m.Instrument.OnboardTime,
        m.Price,m.Volume24h,m.ExchangeReturn24h,m.PriceSourceTime,m.PriceReceivedTime,m.MarkPrice,m.FundingRate,m.FundingSourceTime){MarkReceivedAt=m.MarkReceivedTime};
    public static bool FreshPrice(QuantMarketInput m,DateTimeOffset now)=>m.Price>0&&m.PriceAt is {} at&&m.PriceReceivedAt is {} received&&at<=now&&received<=now&&received>=at
        &&now-at<=TimeSpan.FromSeconds(90)&&now-received<=TimeSpan.FromSeconds(90);
    public static bool Stable(string baseAsset)=>new[]{"USDC","FDUSD","TUSD","DAI","USDD","USDE","USD1"}.Contains(baseAsset,StringComparer.OrdinalIgnoreCase);
}
public sealed record QuantFrame(string Symbol,DateTimeOffset Hour,string? Issue,QuantFacts? Facts,IReadOnlyList<Candle> Bars,
    IReadOnlyList<decimal> Ema20,IReadOnlyList<QuantPivot> Pivots)
{public IReadOnlyList<Candle> Evidence {get;init;}=[];}
public static class QuantSignalEngine
{
    public static IReadOnlyList<Candle> MergeHourly(string symbol,IReadOnlyList<Candle> saved,IReadOnlyList<Candle> five,DateTimeOffset now)
    {
        var primary=saved.Where(c=>c.Symbol==symbol&&c.IntervalMinutes==60&&c.EndTime<=now&&c.ReceivedTime<=now).ToArray();
        var known=primary.Select(c=>c.OpenTime).ToHashSet();
        var synthesized=ForwardEngine.Hourly([],five.Where(c=>c.Symbol==symbol).ToArray(),now).Where(c=>!known.Contains(c.OpenTime));
        return primary.Concat(synthesized).OrderBy(c=>c.OpenTime).ToArray();
    }
    public static decimal[] Ema(IReadOnlyList<decimal> values,int length)
    {if(values.Count==0||length<=0)return [];var result=new decimal[values.Count];result[0]=values[0];for(var i=1;i<values.Count;i++)result[i]=result[i-1]+(values[i]-result[i-1])*2/(length+1);return result;}
    public static QuantFrame Frame(string symbol,IReadOnlyList<Candle> source,DateTimeOffset now,QuantParameters p)
        =>FrameAt(symbol,source,DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds()/3600*3600),now,p);
    // Reconstructed history keeps original reception times; this is not a replay of live availability.
    public static QuantFrame HistoricalFrame(string symbol,IReadOnlyList<Candle> source,DateTimeOffset hour,DateTimeOffset availableAt,QuantParameters p)
        =>FrameAt(symbol,source,hour,availableAt,p);
    private static QuantFrame FrameAt(string symbol,IReadOnlyList<Candle> source,DateTimeOffset hour,DateTimeOffset now,QuantParameters p)
    {
        if(hour>now||hour.ToUnixTimeSeconds()%3600!=0)return new(symbol,hour,"时间边界无效",null,[],[],[]);
        var visible=source.Where(c=>c.Symbol==symbol&&c.IntervalMinutes==60&&c.IsValid&&c.EndTime<=hour&&c.SourceTime>=c.EndTime.AddSeconds(-2)&&c.SourceTime<c.EndTime
            &&c.ReceivedTime>=c.SourceTime&&c.ReceivedTime<=now).OrderBy(c=>c.OpenTime).ToArray();
        var grouped=visible.GroupBy(c=>c.OpenTime).ToArray();
        var conflict=grouped.Any(g=>g.Select(c=>(c.Open,c.High,c.Low,c.Close,c.QuoteVolume)).Distinct().Count()>1);
        var bars=grouped.Select(g=>g.MinBy(c=>c.ReceivedTime)!).TakeLast(p.MinimumHours).ToArray();
        QuantFrame Invalid(string issue)=>new QuantFrame(symbol,hour,issue,null,bars,[],[]){Evidence=visible};
        if(conflict)return Invalid("同一小时存在冲突K线，不能挑有利版本");
        if(bars.Length<p.MinimumHours)return Invalid($"连续小时基线不足{p.MinimumHours}根");
        if(bars[^1].EndTime!=hour||bars.Any(b=>b.OpenTime.ToUnixTimeSeconds()%3600!=0)||bars.Zip(bars.Skip(1)).Any(x=>x.First.EndTime!=x.Second.OpenTime))
            return Invalid("小时基线缺口、错位或最新闭合小时未齐");
        var four=bars.GroupBy(b=>b.OpenTime.ToUnixTimeSeconds()/14400).Select(g=>g.OrderBy(b=>b.OpenTime).ToArray())
            .Where(g=>g.Length==4&&g[0].OpenTime.ToUnixTimeSeconds()%14400==0&&g[^1].EndTime<=hour).OrderBy(g=>g[0].OpenTime).ToArray();
        if(four.Length<p.SlowEma||four.Zip(four.Skip(1)).Any(x=>x.First[^1].EndTime!=x.Second[0].OpenTime))return Invalid("闭合4小时趋势基线不足或不连续");
        var closes=bars.Select(b=>b.Close).ToArray();var fast=Ema(closes,p.FastEma);var slow=Ema(closes,p.SlowEma);
        var fcloses=four.Select(g=>g[^1].Close).ToArray();var ff=Ema(fcloses,p.FastEma);var fs=Ema(fcloses,p.SlowEma);var i=bars.Length-1;
        var atr=Enumerable.Range(i-p.AtrHours+1,p.AtrHours).Average(j=>Math.Max(bars[j].High-bars[j].Low,Math.Max(Math.Abs(bars[j].High-bars[j-1].Close),Math.Abs(bars[j].Low-bars[j-1].Close))));
        var median=ForwardEngine.Median(bars.Skip(i-24).Take(24).Select(b=>b.QuoteVolume));var prior=bars.Skip(i-p.BreakoutHours).Take(p.BreakoutHours).ToArray();
        var pivots=new List<QuantPivot>();
        for(var j=2;j<=i-2;j++)
        {
            var other=new[]{bars[j-2],bars[j-1],bars[j+1],bars[j+2]};
            if(bars[j].Low<other.Min(b=>b.Low))pivots.Add(new("Support",bars[j].Low,bars[j].OpenTime,bars[j+2].EndTime));
            if(bars[j].High>other.Max(b=>b.High))pivots.Add(new("Resistance",bars[j].High,bars[j].OpenTime,bars[j+2].EndTime));
        }
        var facts=new QuantFacts(bars.Length,hour,bars[^1].Close,fast[^1],slow[^1],fast[i-p.SlopeHours],four.Length,four[^1][^1].EndTime,fcloses[^1],ff[^1],fs[^1],
            atr,atr/bars[^1].Close*100,median>0?bars[^1].QuoteVolume/median:0,(bars[^1].Close/bars[i-24].Close-1)*100,prior.Max(b=>b.High),prior.Min(b=>b.Low));
        return new QuantFrame(symbol,hour,null,facts,bars,fast,pivots){Evidence=bars};
    }
    public static QuantOiEvidence Oi(string symbol,IReadOnlyList<DerivativeSample> input,DateTimeOffset hour,DateTimeOffset captured,QuantParameters p)
    {
        var end=hour.AddMinutes(-5);var rows=input.Where(r=>r.Symbol==symbol&&r.Kind==DerivativeKind.OpenInterest5m&&r.Quantity>0&&r.SourceTime<=end&&r.ReceivedTime>=r.SourceTime&&r.ReceivedTime<=captured).OrderBy(r=>r.SourceTime).ToArray();
        if(rows.GroupBy(r=>r.SourceTime).Any(g=>g.Select(r=>r.Quantity).Distinct().Count()>1))return new("Unknown",null,null,0,null,null,null,"同一OI时点存在冲突数量，不能挑选有利版本");
        var latest=rows.LastOrDefault();var one=latest is null?null:rows.LastOrDefault(r=>r.SourceTime<=latest.SourceTime.AddHours(-1));
        var day=latest is null?null:rows.LastOrDefault(r=>r.SourceTime<=latest.SourceTime.AddHours(-24));
        if(latest is null||end-latest.SourceTime>TimeSpan.FromMinutes(10)||one is null||latest.SourceTime.AddHours(-1)-one.SourceTime>TimeSpan.FromMinutes(5))
            return new("Unknown",null,null,0,latest,one,day,"缺少当时可见且同合约的OI数量1h基线；不按名义价值计算");
        var h=(latest.Quantity!.Value/one.Quantity!.Value-1)*100;
        decimal? d=day is not null&&latest.SourceTime.AddHours(-24)-day.SourceTime<=TimeSpan.FromMinutes(10)?(latest.Quantity.Value/day.Quantity!.Value-1)*100:null;
        if(Math.Abs(h)>p.MaximumOi1hAbsolutePercent||d is {} delta&&Math.Abs(delta)>p.MaximumOi24hAbsolutePercent)
            return new("Extreme",h,d,0,latest,one,day,"数量变化过快，作为风险压力拦截；不推断多空持有者意图");
        return new(h>=0?"Increasing":"Decreasing",h,d,h<0||d is null?.5m:1,latest,one,day,"OI仅作增减仓背景，减仓或24h缺失降低风险，不直接推断价格方向");
    }
    public static IReadOnlyList<QuantHypothesis> Analyze(QuantMarketInput market,QuantFrame frame,QuantOiEvidence oi,DateTimeOffset captured,string mode,string hash,string inputHash,QuantParameters p)
        =>AnalyzeCore(market,frame,oi,captured,mode,hash,inputHash,p,false);
    public static IReadOnlyList<QuantHypothesis> Reconstruct(Instrument instrument,QuantFrame frame,DateTimeOffset availableAt,string hash,string inputHash,QuantParameters p)
    {
        var last=frame.Bars.LastOrDefault();var facts=frame.Facts;
        var derived=new QuantMarketInput(instrument.AssetId,instrument.Symbol,instrument.BaseAsset,instrument.Active,instrument.OnboardTime,last?.Close,
            frame.Bars.TakeLast(24).Sum(b=>b.QuoteVolume),facts?.Return24hPercent,last?.SourceTime,last?.ReceivedTime,null,null,null);
        var oi=new QuantOiEvidence("Unknown",null,null,0,null,null,null,"历史价格重建没有当时OI，不能当作完整量化策略验证");
        return AnalyzeCore(derived,frame,oi,availableAt,"HistoricalPriceStudy",hash,inputHash,p,true);
    }
    private static IReadOnlyList<QuantHypothesis> AnalyzeCore(QuantMarketInput market,QuantFrame frame,QuantOiEvidence oi,DateTimeOffset captured,string mode,string hash,string inputHash,QuantParameters p,bool reconstructed)
    {
        var result=new List<QuantHypothesis>();var f=frame.Facts;
        foreach(var direction in new[]{"Long","Short"})foreach(var strategy in new[]{"TrendPullback","Breakout20"})
        {
            var side=direction=="Long"?1m:-1m;var checks=new List<SignalCondition>();decimal? stop=null;string? basis=null;
            var issue=frame.Issue??(!market.Active||QuantRules.Stable(market.BaseAsset)?"非活跃合约或锚定资产，不参与此策略":!reconstructed&&!QuantRules.FreshPrice(market,captured)?"报价失鲜或时间无效":
                market.QuoteVolume24h<p.MinimumVolume24h||market.QuoteVolume24h is null?"24h成交额未达到本版本1000万门槛":market.OnboardAt>frame.Hour.AddHours(-p.MinimumHours)?"上市时间不足连续基线周期":null);
            if(issue is null&&f is not null)
            {
                var i=frame.Bars.Count-1;var c=frame.Bars[i];var previous=frame.Bars[i-1];var atr=f.Atr;
                checks.Add(new("Trend4h","4h均线趋势及收盘位置",(f.FourHourEma20/f.FourHourEma60-1)*100,direction=="Long"?"EMA20>60且收盘≥EMA20":"EMA20<60且收盘≤EMA20",side*(f.FourHourEma20-f.FourHourEma60)>0&&side*(f.FourHourClose-f.FourHourEma20)>=0));
                checks.Add(new("Trend1h","1h趋势及6h均线斜率",(f.Ema20/f.Ema60-1)*100,direction=="Long"?"EMA20>60且EMA20上行":"EMA20<60且EMA20下行",side*(f.Ema20-f.Ema60)>0&&side*(f.Ema20-f.Ema20Ago6)>0));
                checks.Add(new("Atr","小时ATR/收盘%",f.AtrPercent,$"0～{p.MaximumAtrFraction*100:G}%",atr>0&&f.AtrPercent<=p.MaximumAtrFraction*100));
                var extension=atr>0?side*(c.Close-f.Ema20)/atr:decimal.MaxValue;
                if(strategy=="TrendPullback")
                {
                    var touch=Enumerable.Range(i-3,3).Any(j=>frame.Bars[j].Low<=frame.Ema20[j]+p.PullbackTouchAtr*atr&&frame.Bars[j].High>=frame.Ema20[j]-p.PullbackTouchAtr*atr);
                    checks.Add(new("Touch","前三根闭合小时回踩EMA20带",null,$"EMA20±{p.PullbackTouchAtr:G}ATR相交",touch));
                    checks.Add(new("Recovery","恢复并越过前一根高/低",c.Close,direction=="Long"?"阳线且收盘越前高、回到EMA20上方":"阴线且收盘破前低、回到EMA20下方",side*(c.Close-c.Open)>0&&side*(c.Close-(side>0?previous.High:previous.Low))>0&&side*(c.Close-f.Ema20)>=0));
                    checks.Add(new("Volume","闭合小时成交额量比",f.VolumeRatio,$"≥{p.PullbackVolumeRatio:G}倍",f.VolumeRatio>=p.PullbackVolumeRatio));
                    checks.Add(new("Extension","顺势方向距EMA20的ATR数",extension,$"0～{p.PullbackMaximumExtensionAtr:G}ATR",extension>=0&&extension<=p.PullbackMaximumExtensionAtr));
                }
                else
                {
                    var level=side>0?f.Prior20High:f.Prior20Low;
                    checks.Add(new("Breakout","超出前20根闭合小时边界ATR数",atr>0?side*(c.Close-level)/atr:null,$">{p.BreakoutBufferAtr:G}ATR",side*(c.Close-level)>p.BreakoutBufferAtr*atr));
                    var strength=c.High>c.Low?(c.Close-c.Low)/(c.High-c.Low):(decimal?)null;
                    checks.Add(new("CloseStrength","小时收盘位置%",strength*100,direction=="Long"?$"≥{p.CloseStrength*100:G}%":$"≤{(1-p.CloseStrength)*100:G}%",strength is {} s&&(side>0?s>=p.CloseStrength:s<=1-p.CloseStrength)));
                    checks.Add(new("Volume","闭合小时成交额量比",f.VolumeRatio,$"≥{p.BreakoutVolumeRatio:G}倍",f.VolumeRatio>=p.BreakoutVolumeRatio));
                    checks.Add(new("Extension","顺势方向距EMA20的ATR数",extension,$"0～{p.BreakoutMaximumExtensionAtr:G}ATR",extension>=0&&extension<=p.BreakoutMaximumExtensionAtr));
                }
                if(checks.All(x=>x.Passed))
                {
                    if(strategy=="TrendPullback")
                    {
                        var pivot=frame.Pivots.Where(v=>v.At>=frame.Hour.AddHours(-p.PivotLookbackHours)&&v.Kind==(side>0?"Support":"Resistance")&&side*(c.Close-v.Price)>0).MaxBy(v=>v.At);
                        if(pivot is not null){stop=pivot.Price-side*p.StopBufferAtr*atr;basis="最近两根右侧闭合确认的"+(side>0?"支撑":"压力")+"加0.5ATR缓冲";}
                    }
                    else
                    {
                        var level=side>0?f.Prior20High:f.Prior20Low;var raw=level-side*p.StopBufferAtr*atr;
                        stop=side>0?Math.Min(raw,c.Close-atr):Math.Max(raw,c.Close+atr);basis="前20h边界的突破失效假设，0.5ATR缓冲且至少1ATR失效距离";
                    }
                }
            }
            var triggered=issue is null&&checks.Count>0&&checks.All(x=>x.Passed);
            var status=issue is not null?"Insufficient":!triggered?"NoSignal":stop is null or <=0?"StructureRejected":"PriceCandidate";
            var reason=issue??(!triggered?"价格条件未齐，逐项保留":stop is null or <=0?"没有有效已确认结构，不能补一个任意百分比止损":"价格结构候选，仍需OI、环境、后续报价及组合风控核验");
            result.Add(new($"{hash[..12]}:{frame.Hour.ToUnixTimeSeconds()}:{market.Symbol}:{strategy}:{direction}",hash,market.Symbol,strategy,direction,mode,frame.Hour,captured,captured.AddSeconds(p.SignalLifetimeSeconds),status,reason,inputHash,market,f,oi,stop,basis,frame.Pivots,checks));
        }
        return result;
    }
}
