using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Application;

public static class OpportunityEngine
{
    public const string Version="Opportunity-Pools-2-Tenfold";
    public const string Definition="30日十倍研究：排除BTC/ETH及锚定资产，流通市值<=3亿美元为待验证研究范围，未核验参考仅用于核验优先级；提前研究1/3，涨幅1/3，走弱1/6，其余轮换；72h窄幅不再是十倍硬门槛；十倍研究不直接执行旧A规则。";
    public static string Hash(int limit)=>ForwardEngine.Hash(new ForwardRules{Version=Version,Definition=Definition+TenfoldEngine.Definition+$"深度上限{limit}"});
    public static string Eligibility(MarketView m,DateTimeOffset now)
    {
        if(!m.Instrument.Active)return "非活跃合约";
        if(m.Price is not >0||m.ExchangeReturn24h is null||m.Volume24h is null)return "报价字段不足";
        if(m.PriceSourceTime is not {} source||m.PriceReceivedTime is not {} received||source>now||received>now||received<source
            ||now-source>TimeSpan.FromSeconds(90)||now-received>TimeSpan.FromSeconds(90))return "报价失鲜或时间无效";
        return m.Volume24h<5_000_000?"成交额不足500万":"可参与粗筛";
    }
    public static IReadOnlyList<(MarketView Market,IReadOnlyList<string> Tracks)> Select(IReadOnlyList<MarketView> markets,DateTimeOffset now,int limit=60)
    {
        limit=Math.Clamp(limit,2,100);
        var eligible=markets.Where(m=>Eligibility(m,now)=="可参与粗筛").OrderBy(m=>m.Instrument.Symbol,StringComparer.Ordinal).ToArray();
        var selected=new Dictionary<string,(MarketView Market,List<string> Tracks)>(StringComparer.Ordinal);
        void Add(IEnumerable<MarketView> source,string track,int count)
        {
            foreach(var m in source.Take(count))
            {
                if(selected.TryGetValue(m.Instrument.Symbol,out var old)){old.Tracks.Add(track);continue;}
                if(selected.Count<limit)selected.Add(m.Instrument.Symbol,(m,[track]));
            }
        }
        // Missing OI/identity does not decide who gets the chance to collect OI/history.
        // Quiet does not mean accumulation: the deep check must still validate compression and flow.
        var third=Math.Max(1,limit/3);
        var quiet=eligible.Where(m=>TenfoldEngine.DiscoveryTier(m,now)>=0&&m.ExchangeReturn24h is >=-15 and <=20)
            .OrderBy(m=>TenfoldEngine.DiscoveryTier(m,now)).ThenByDescending(m=>m.Volume24h).ToArray();
        var stable=Math.Max(1,third/2);Add(quiet,"Early",stable);
        var quietRotation=quiet.Skip(stable).ToArray();
        if(quietRotation.Length>0)
        {
            var start=(int)((now.ToUnixTimeSeconds()/3600*Math.Max(1,third-stable))%quietRotation.Length);
            Add(quietRotation.Skip(start).Concat(quietRotation.Take(start)),"Early",third-stable);
        }
        Add(eligible.Where(m=>m.ExchangeReturn24h>=3).OrderByDescending(m=>m.ExchangeReturn24h),"Momentum",third);
        Add(eligible.Where(m=>m.ExchangeReturn24h<=-3).OrderBy(m=>m.ExchangeReturn24h),"Weakness",Math.Max(1,limit/6));
        if(eligible.Length>0)
        {
            var offset=(int)((now.ToUnixTimeSeconds()/300*Math.Max(1,limit/6))%eligible.Length);
            Add(eligible.Skip(offset).Concat(eligible.Take(offset)).Where(m=>!selected.ContainsKey(m.Instrument.Symbol)),"Explore",limit-selected.Count);
        }
        return selected.Values.Select(v=>(v.Market,(IReadOnlyList<string>)v.Tracks)).ToArray();
    }
    public static int HourlyFetchLimit(IReadOnlyList<Candle> saved,DateTimeOffset end)
    {
        var hour=DateTimeOffset.FromUnixTimeSeconds(end.ToUnixTimeSeconds()/3600*3600);
        return HourlyWarmupEngine.Plan(saved.FirstOrDefault()?.Symbol??"",saved,hour,end).FetchLimit;
    }
    public static OpportunityRow Evaluate(MarketView m,IReadOnlyList<string> tracks,IReadOnlyList<Candle> rows,DerivativeRuntimeSnapshot oi,DateTimeOffset now)
    {
        var hour=DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds()/3600*3600);
        var bars=rows.Where(c=>c.Symbol==m.Instrument.Symbol&&c.IsValid&&c.EndTime<=hour&&c.ReceivedTime<=now).OrderBy(c=>c.OpenTime).TakeLast(337).ToArray();
        var contiguous=bars.Length>=73&&bars[^1].EndTime==hour&&!bars.Zip(bars.Skip(1)).Any(p=>p.First.EndTime!=p.Second.OpenTime);
        decimal? range=contiguous?(bars.TakeLast(72).Max(c=>c.High)/bars.TakeLast(72).Min(c=>c.Low)-1)*100:null;
        var median=ForwardEngine.Median(bars.Skip(Math.Max(0,bars.Length-25)).Take(24).Select(c=>c.QuoteVolume));
        decimal? ratio=contiguous&&median>0?bars[^1].QuoteVolume/median:null;
        var support=new List<string>();var risks=new List<string>();var missing=new List<string>();
        if(tracks.Contains("Early"))support.Add("24h涨跌幅处于-5%～8%，仅为未大幅启动的观察入口");
        if(tracks.Contains("Momentum"))support.Add("进入全市场涨幅观察，需区分突破延续、回踩和过热");
        if(tracks.Contains("Weakness"))risks.Add("进入走弱观察，不将下跌本身作为做空依据");
        if(tracks.Contains("Explore"))missing.Add("轮换补齐对象，入选不代表已有信号");
        if(range is {} width){if(width<=12)support.Add($"近72小时区间{width:F2}%，符合收敛观察范围");else risks.Add(tracks.Contains("Early")?$"近72小时区间{width:F2}%，不符合提前收敛条件":$"近72小时宽幅波动{width:F2}%，需核对结构与过热风险");}
        else missing.Add("最近闭合小时/72小时连续基线不足");
        if(ratio is {} v){if(v>=1.2m)support.Add($"最新闭合小时成交额为前24小时中位数的{v:F2}倍");else risks.Add($"最新闭合小时成交额量比{v:F2}，启动量能尚弱");}
        var decisions=PaperAnalysis.Analyze(hour.AddDays(-30),m,bars,oi,now,research:true);
        var evidence=decisions[0];
        if(evidence.Oi24hPercent is {} change)
        {if(change is >=0 and <=30)support.Add($"OI数量24h变化{change:F2}%，符合当前实验范围");else risks.Add($"OI数量24h变化{change:F2}%，不符合当前实验入场范围");}
        else missing.Add("OI数量24小时同来源比较不足");
        if(!m.Features.Capital.TrustedIdentity)missing.Add("资产市值身份未核验，不使用市值/FDV比率");
        if(!m.Features.OnChain.TrustedIdentity||m.Features.OnChain.HolderCount.Status!=DataStatus.Valid)missing.Add("可信持币地址变化不足，不能判断吸筹");
        missing.Add("钱包归属与消息催化未形成完整证据");
        if(m.FundingRate is {} rate&&m.FundingSourceTime is {} funding&&funding<=now&&now-funding<TimeSpan.FromMinutes(10)&&Math.Abs(rate)>=.003m)risks.Add("资金费率绝对值偏高，需核对结算周期与拥挤风险");
        if(Math.Abs(m.ExchangeReturn24h??0)>40)risks.Add("24h波动超过40%，处于当前突破实验幅度范围之外");
        var plans=decisions.Where(d=>d.Status=="Candidate"&&d.Plan is not null).ToArray();
        var state=Eligibility(m,now)!="可参与粗筛"?"失鲜/不适用":!contiguous||bars.Length<337||evidence.Oi24hPercent is null?"待补数据":plans.Length>0&&evidence.Oi24hPercent is >=0 and <=30?"结构候选":"观察中";
        if(!StructuralPaperEngine.Fresh(m,now))missing.Add("45秒内价格与标记价未同时满足；模拟成交另行等待新报价");
        if(bars.Length<337)missing.Add($"小时基线{bars.Length}/337，暂不能形成完整执行计划");
        var result=new OpportunityRow(m.Instrument.AssetId,m.Instrument.Symbol,tracks,m.Price,m.ExchangeReturn24h,m.Volume24h,m.PriceSourceTime,m.PriceReceivedTime,bars.Length,range,ratio,
            evidence.Oi24hPercent,evidence.OiSourceTime,evidence.OiReceivedTime,state,support,risks,missing,plans);
        return result;
    }
}
