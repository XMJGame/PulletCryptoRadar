using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Application;

public static class TenfoldEngine
{
    public const decimal MaximumMarketCap=300_000_000m;
    public const int RequiredHours=744;
    public const string Definition="Tenfold-Research-1:30日价格10倍目标；市值<=300M USD，非托管Top10>=30%为未校准假设，24h同身份/同分类占比增长；过去30日>=3倍标记已启动；概率未知，不自动执行。";
    private static readonly HashSet<string> Benchmarks=["BTC","ETH","WBTC","WETH","BTCB","PAXG","XAUT","USDC","USDT","FDUSD","TUSD","USDE","DAI"];
    private static bool Valid<T>(FeatureValue<T> f,DateTimeOffset now)where T:struct=>f.Status==DataStatus.Valid&&f.Value is not null&&f.SourceTime is {} t&&t<=now&&now-t<=TimeSpan.FromHours(1);
    public static int DiscoveryTier(MarketView m,DateTimeOffset now)
    {
        if(Benchmarks.Contains(m.Instrument.BaseAsset))return -1;
        var c=m.Features.Capital;var cap=c.MarketCapUsd;
        if(Valid(cap,now)&&cap.Value>0)
        {
            // An unverified reference is only a collection priority, never a verified small-cap claim.
            if(cap.Value>MaximumMarketCap)return -1;
            return c.TrustedIdentity?0:1;
        }
        return 2;
    }
    public static TenfoldProfile Evaluate(MarketView m,IReadOnlyList<Candle> rows,DateTimeOffset now,TenfoldProfile? previous=null)
    {
        var c=m.Features.Capital;var chain=m.Features.OnChain;var capValid=c.TrustedIdentity&&Valid(c.MarketCapUsd,now)&&c.MarketCapUsd.Value>0;
        var cap=capValid?c.MarketCapUsd.Value:null;var reference=Valid(c.MarketCapUsd,now)?c.MarketCapUsd.Value:null;
        var small=cap is >0 and <=MaximumMarketCap;
        var concentration=chain.TrustedIdentity&&chain.ConcentrationUsable&&Valid(chain.NonCustodialTop10Percent,now)?chain.NonCustodialTop10Percent.Value:null;
        var classification=ForwardEngine.Hash(new ForwardRules{Definition=chain.BindingRevision+":"+string.Join(";",chain.Labels.Where(l=>l.Verified&&l.UpdatedAt<=now).OrderBy(l=>l.Address,StringComparer.Ordinal).Select(l=>$"{l.Address}:{l.Type}:{l.Revision}"))});
        decimal? change=null;
        if(concentration is not null&&previous?.AdjustedTop10Percent is {} old&&previous.CoinGeckoId==c.CoinGeckoId&&previous.Chain==chain.Chain
            &&previous.ContractAddress==chain.ContractAddress&&previous.Provider==chain.Provider&&previous.ClassificationFingerprint==classification&&previous.ConcentrationSourceTime is {} oldTime
            &&chain.NonCustodialTop10Percent.SourceTime-oldTime is {} elapsed&&elapsed>=TimeSpan.FromHours(23)&&elapsed<=TimeSpan.FromHours(25))change=concentration-old;
        var bars=rows.Where(b=>b.IsValid&&b.IntervalMinutes==60&&b.ReceivedTime<=now&&b.EndTime<=now).OrderBy(b=>b.OpenTime).TakeLast(721).ToArray();
        var complete=bars.Length==721&&now-bars[^1].EndTime<TimeSpan.FromHours(1)&&!bars.Zip(bars.Skip(1)).Any(p=>p.First.EndTime!=p.Second.OpenTime);
        decimal? past=complete?bars[^1].Close/bars[0].Close:null;
        var supporting=new List<string>();var risks=new List<string>();var missing=new List<string>();
        if(small)supporting.Add($"已核验流通市值{cap:F0} USD，进入不超过3亿美元的实验研究范围");
        else if(capValid)risks.Add("已核验市值超过本研究的3亿美元上限");
        else missing.Add("身份或新鲜流通市值待核验，不能确认属于小市值币");
        if(concentration is {} top)
        {
            supporting.Add($"剔除已核验交易所/销毁/LP/桥后的Top10占总供应量{top:F2}%，不代表同一操盘方");
            risks.Add("尚未识别的交易所托管与地址拆分仍可能混入，不能视为庄家持仓占比");
            if(top>=50)risks.Add("筹码高度集中，同时存在集中抛售风险，不能只当利好");
        }
        else missing.Add("可信链上身份、地址分类或集中度数据不足");
        if(change is {} delta){if(delta>0)supporting.Add($"同来源分类调整Top10占比24h增加{delta:F2}个百分点，需继续核对归属和转账");else risks.Add($"Top10占比24h变化{delta:F2}个百分点，未支持集中增加假设");}
        else missing.Add("缺少可比较的24小时集中度留档，不能把单张快照认定为吸筹");
        if(past is null)missing.Add("缺少连续30日价格基线，暂不能判断是否已提前大涨");
        else if(past>=3)risks.Add($"过去30日已变为{past:F2}倍，需防止将已拉升阶段当作提前埋伏");
        if(capValid&&Valid(c.FdvUsd,now)&&c.FdvUsd.Value/cap>=5)risks.Add("FDV/流通市值至少5倍，解锁与稀释计划需核对");
        missing.Add("尚无已验证的同主体钱包净买入、交易所库存变化和未来解锁完整证据");
        missing.Add("30日十倍模型未完成独立样本校准，预测概率保持Unknown");
        var status=Benchmarks.Contains(m.Instrument.BaseAsset)?"基准/锚定资产排除":capValid&&!small?"市值超范围":!small?"小市值身份待核验":concentration is null?"筹码证据待补":concentration<30?"未见高集中筹码":past is null?"月度基线待补":past>=3?"已启动待复核":change is null?"集中度时序待积累":change<=0?"集中度未支持吸筹":"十倍假设待验证";
        return new TenfoldProfile(status,small,c.CoinGeckoId,chain.Chain,chain.ContractAddress,chain.Provider,cap,reference,c.MarketCapUsd.SourceTime,concentration,
            concentration is null?null:chain.NonCustodialTop10Percent.SourceTime,change,
            chain.TrustedIdentity&&Valid(chain.HolderCount,now)?chain.HolderCount.Value:null,
            chain.TrustedIdentity&&Valid(chain.HolderChange24hPercent,now)?chain.HolderChange24hPercent.Value:null,past,
            m.Price is >0?m.Price*10:null,cap*10,null,"目标：起点后30日内价格达到10倍；现有证据不足以给出可靠概率",supporting,risks,missing){ClassificationFingerprint=classification};
    }
}
