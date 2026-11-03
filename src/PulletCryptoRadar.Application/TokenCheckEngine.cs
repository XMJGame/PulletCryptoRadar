using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Application;
public static class TokenCheckEngine
{
    public const string Version="Token-QuickCheck-1.2";
    public static bool Fresh(DateTimeOffset? source,DateTimeOffset? received,DateTimeOffset now,TimeSpan age)=>source is {} s&&received is {} r&&s<=r&&r<=now&&now-s<=age;
    public static string? Chain(string platform)=>platform switch{"solana"=>"solana","ethereum"=>"ethereum","binance-smart-chain"=>"bsc","base"=>"base","arbitrum-one"=>"arbitrum","polygon-pos"=>"polygon",_=>null};
    public static bool IdentityMatches(MarketCoin coin,string platform,string contract,TokenVenueIdentity? venue,DateTimeOffset now)
    {
        if(!coin.Platforms.TryGetValue(platform,out var expected)||!Same(platform,expected,contract)||venue is null||venue.Delisted
            ||!venue.Symbol.Equals(coin.Symbol,StringComparison.OrdinalIgnoreCase)||!Fresh(venue.SourceTime,venue.ReceivedTime,now,TimeSpan.FromMinutes(45)))return false;
        return venue.Contracts.TryGetValue(platform,out var actual)&&Same(platform,expected,actual);
    }
    public static bool Same(string platform,string a,string b)=>platform=="solana"?a==b:a.StartsWith("0x",StringComparison.Ordinal)&&b.StartsWith("0x",StringComparison.Ordinal)?a.Equals(b,StringComparison.OrdinalIgnoreCase):a==b;
    public static decimal? OiChange(string symbol,IReadOnlyList<DerivativeSample> series,DateTimeOffset now,out DateTimeOffset? source)
    {
        var rows=series.Where(s=>s.Symbol==symbol&&s.Kind==DerivativeKind.OpenInterest5m&&s.Quantity>0&&s.SourceTime<=s.ReceivedTime&&s.ReceivedTime<=now).OrderBy(s=>s.SourceTime).ToArray();
        var last=rows.LastOrDefault();source=last?.SourceTime;
        if(last is null||now-last.SourceTime>TimeSpan.FromMinutes(11))return null;
        var prior=rows.Where(s=>Math.Abs((last.SourceTime-s.SourceTime).TotalHours-24)<=1d/6).MinBy(s=>Math.Abs((last.SourceTime-s.SourceTime).TotalHours-24));
        return prior is null?null:(last.Quantity/prior.Quantity-1)*100;
    }
    public static TokenTechnical Technical(string symbol,IReadOnlyList<Candle> input,DateTimeOffset now,decimal? price)
    {
        var rows=input.Where(b=>b.Symbol==symbol&&b.IsValid&&b.IntervalMinutes==60&&b.EndTime<=now&&b.SourceTime<=b.ReceivedTime&&b.ReceivedTime<=now)
            .GroupBy(b=>b.OpenTime).Select(g=>g.MaxBy(b=>b.SourceTime)!).OrderBy(b=>b.OpenTime).TakeLast(169).ToArray();
        var continuous=rows.Length>=72&&now-rows[^1].EndTime<=TimeSpan.FromMinutes(75)&&!rows.Zip(rows.Skip(1)).Any(p=>p.First.EndTime!=p.Second.OpenTime);
        if(!continuous)return new(rows.Length,false,rows.LastOrDefault()?.Close,null,null,null,null,null,null,null,null,null,null,"历史不足或不连续","补齐至少72根连续且新鲜的闭合小时线");
        decimal Ema(int length){var n=rows[0].Close;foreach(var b in rows.Skip(1))n+=(b.Close-n)*2/(length+1);return n;}
        var ema20=Ema(20);var ema60=Ema(60);var last=rows[^1];
        var atr=rows.Zip(rows.Skip(1)).TakeLast(14).Average(p=>Math.Max(p.Second.High-p.Second.Low,Math.Max(Math.Abs(p.Second.High-p.First.Close),Math.Abs(p.Second.Low-p.First.Close))));
        var mark=price??last.Close;var lows=new List<Candle>();var highs=new List<Candle>();
        for(var i=2;i<rows.Length-2;i++)
        {
            if(Enumerable.Range(i-2,5).Where(j=>j!=i).All(j=>rows[i].Low<rows[j].Low))lows.Add(rows[i]);
            if(Enumerable.Range(i-2,5).Where(j=>j!=i).All(j=>rows[i].High>rows[j].High))highs.Add(rows[i]);
        }
        var support=lows.Where(b=>b.Low<mark).MaxBy(b=>b.Low);var resistance=highs.Where(b=>b.High>mark).MinBy(b=>b.High);
        var stop=support is not null?support.Low-.5m*atr:(decimal?)null;
        var rr=stop is >0&&stop<mark&&resistance is not null?(resistance.High-mark)/(mark-stop):null;
        var trend=last.Close>ema20&&ema20>ema60?"上行结构":last.Close<ema20&&ema20<ema60?"下行结构":"震荡或趋势未确认";
        var confirmation=resistance is not null?$"等待小时收盘站稳历史压力 {resistance.High:G8}，并复核成交量与OI；跌破结构支撑须重新评估":
            "上方缺少已确认历史压力，不能把风险倍数投影称为真实目标位；等待回踩与新结构形成";
        return new(rows.Length,true,last.Close,(last.Close/rows[^25].Close-1)*100,ema20,ema60,atr,support?.Low,support?.EndTime,resistance?.High,resistance?.EndTime,
            stop is >0?stop:null,rr,trend,confirmation);
    }
    public static TokenCheckReport Build(MarketCoin coin,TokenCheckRequest request,bool identity,string identityReason,TokenProjectSnapshot? project,
        TickerEvent? ticker,MarkPriceEvent? mark,IReadOnlyList<Candle> candles,IReadOnlyList<DerivativeSample> oi,OnChainSample? chain,
        IReadOnlyList<TokenCheckSource> sources,IReadOnlyList<string> failures,DateTimeOffset now,string? instrument)
    {
        var good=new List<string>();var risks=new List<string>();var missing=new List<string>(failures);
        var projectFresh=project is not null&&project.CoinGeckoId==coin.Id&&Fresh(project.SourceTime,project.ReceivedTime,now,TimeSpan.FromHours(1));
        var quoteFresh=identity&&ticker is {Price:>0}&&ticker.Symbol==instrument&&Fresh(ticker.SourceTime,ticker.ReceivedTime,now,TimeSpan.FromSeconds(90));
        var price=quoteFresh?ticker!.Price:projectFresh?project!.PriceUsd:null;
        var tech=Technical(instrument??"",candles,now,quoteFresh?price:null);
        var oiChange=identity&&instrument is not null?OiChange(instrument,oi,now,out _):null;
        DateTimeOffset? oiTime=null;if(identity&&instrument is not null)_=OiChange(instrument,oi,now,out oiTime);
        var chainFresh=identity&&chain is not null&&chain.Chain==Chain(request.Platform)&&Same(request.Platform,chain.ContractAddress,request.ContractAddress)
            &&Fresh(chain.SourceTime,chain.ReceivedTime,now,TimeSpan.FromMinutes(45));
        var security=chain?.Security;var securityFresh=identity&&security is not null&&security.Chain==Chain(request.Platform)&&Same(request.Platform,security.ContractAddress,request.ContractAddress)
            &&security.Error is null&&Fresh(security.SourceTime,security.ReceivedTime,now,TimeSpan.FromMinutes(45));
        if(identity)good.Add("所选链与完整合约/Mint已通过身份交叉核对，分析只对应当前选定项目");else missing.Add("独立来源未确认完整链合约，不能把同名交易对或市值合入可信结论");
        if(projectFresh&&project!.MarketCapUsd is >0 and <=300_000_000)good.Add("流通市值在3亿美元以下，具备小市值研究空间；这不证明低估或十倍潜力");
        if(projectFresh&&project!.FdvUsd>0&&project.MarketCapUsd>0&&project.FdvUsd/project.MarketCapUsd>=3)risks.Add($"FDV约为流通市值的{project.FdvUsd/project.MarketCapUsd:F1}倍，未流通供应构成稀释观察项；不等于近期全部解锁");
        if(!projectFresh)missing.Add("缺少新鲜的项目估值/流通供应资料");
        if(!quoteFresh)missing.Add("缺少身份已关联且90秒内的新鲜交易所报价，不能评价当前入场时点");
        if(quoteFresh&&ticker!.QuoteVolume24h<5_000_000)risks.Add("该永续合约24h报价成交额不足500万USDT，执行流动性须进一步核对盘口");
        if(quoteFresh&&ticker!.Return24h>=20)risks.Add("24小时涨幅已超过20%，追涨与回撤风险较高");
        if(quoteFresh&&ticker!.Return24h<=-15)risks.Add("24小时跌幅超过15%，暂未证明止跌，不能仅因跌深抄底");
        if(tech.Continuous){if(tech.Trend=="上行结构")good.Add("闭合小时线位于EMA20之上且EMA20高于EMA60，当前量价结构偏强");else if(tech.Trend=="下行结构")risks.Add("闭合小时线处于EMA20/EMA60下行排列，当前买入属于逆势观察");}
        else missing.Add("小时线不足或不连续，支撑压力和趋势暂不输出");
        if(tech.Support is null)missing.Add("缺少右侧两根闭合K线确认的历史支撑，不能随意设止损");
        if(tech.Resistance is null)missing.Add("缺少上方已确认历史压力，目标位暂不编造");
        if(tech.RewardRiskToResistance is <1.5m)risks.Add("按当前参考价至历史压力的结构收益风险比不足1.5，入场空间偏窄；未扣费用");
        if(oiChange is {} change){if(change>=30)risks.Add("合约OI数量24h增长超过30%，存在拥挤/杠杆放大观察项");else if(change>=0)good.Add($"同合约OI数量24h变化{change:F2}%，可与量价交叉观察，不能当作净买入");else risks.Add($"OI数量24h变化{change:F2}%，未支持新增持仓驱动假设");}
        else missing.Add("缺少同合约新鲜且约24h可比的OI数量基线");
        decimal? funding=identity&&mark is not null&&mark.Symbol==instrument&&Fresh(mark.SourceTime,mark.ReceivedTime,now,TimeSpan.FromMinutes(2))?mark.FundingRate:null;
        if(funding is {} f&&Math.Abs(f)>=.001m)risks.Add("本次资金费率绝对值至少0.1%，需核对该合约结算频率和拥挤方向");
        if(!chainFresh)missing.Add("持币分布失鲜或尚未取得，不将旧筹码当作当前吸筹证据");
        if(chain?.Chain=="solana")missing.Add("Solana返回Token Account条目不证明全链Owner Top10；集中度保持未知");
        if(securityFresh)
        {
            foreach(var key in new[]{"is_honeypot","cannot_sell_all","owner_change_balance","hidden_owner","selfdestruct","mintable","freezable","balance_mutable_authority","non_transferable"})
                if(security!.Flags.GetValueOrDefault(key)==true)risks.Add($"合约权限/安全检测提示 {key}=是，需逐项核实权限范围，暂不参与");
            if(security!.Flags.Values.All(v=>v is null))missing.Add("安全检测字段均未知");
        }
        else missing.Add("尚无新鲜安全检测证据；未检出不等于安全");
        missing.AddRange(["交易所托管/项目方/桥/LP归属尚需逐项核验，原始大地址不等于庄家","未自动核验最新退市公告、近期解锁日历、团队/产品收入和重大新闻","没有盘口深度/预期滑点，成交额不能替代流动性深度"]);
        var severe=securityFresh&&new[]{"is_honeypot","cannot_sell_all","owner_change_balance","hidden_owner","selfdestruct","non_transferable"}.Any(k=>security!.Flags.GetValueOrDefault(k)==true);
        var avoid=severe||tech.Trend=="下行结构"||quoteFresh&&(ticker!.Return24h>=20||ticker.Return24h<=-15||ticker.QuoteVolume24h<5_000_000)||tech.RewardRiskToResistance<1.5m;
        var verdict=!identity||!quoteFresh||!tech.Continuous?"资料不足":avoid?"暂不考虑当前买入":tech.Trend=="上行结构"&&oiChange is >=0 and <30&&chainFresh&&securityFresh&&tech.RewardRiskToResistance>=1.5m?"值得继续研究":"等待确认";
        var summary=verdict switch{"资料不足"=>"关键身份、时效或结构证据未齐，当前不能给出可买判断。","暂不考虑当前买入"=>"当前结构或风险存在明确反证，优先等待风险缓解与新证据。","值得继续研究"=>"部分市场证据同向，但项目、解锁和执行条件仍需复核，尚不构成买入指令。",_=>"证据尚未形成一致判断，等待结构、OI与链上证据补充。"};
        var potential=projectFresh&&project!.MarketCapUsd is >0 and <=300_000_000?"小市值研究对象；估值空间只是条件之一。尚无证据估计十倍概率或长期收益。":"长期潜力待核验；先补齐流通估值、稀释、产品使用与团队资料。";
        // Four due-diligence families are intentionally unverified: attribution, releases/news,
        // product/team and executable depth. Six market feeds cannot claim complete diligence.
        var coverage=new[]{identity,quoteFresh,projectFresh,tech.Continuous,oiChange is not null,chainFresh&&securityFresh}.Count(v=>v)*100/10;
        return new(Guid.NewGuid(),Version,now,now.AddMinutes(2),coin,request.Platform,request.ContractAddress,request.AssetId,instrument,identity,identityReason,verdict,summary,potential,coverage,
            "10项核验中的已取比例：市场六项；归属、解锁/公告、项目尽调、盘口深度待补。不是胜率/上涨概率",project,price,quoteFresh?ticker!.QuoteVolume24h:null,funding,oiChange,oiTime,tech,chain,
            good,risks,missing.Distinct().ToArray(),[tech.Confirmation,"先核对近期解锁/退市与项目资料，再决定是否值得观察；快查不会执行买卖","价格或来源超过有效时间后重新分析；结构价位仅作参考，跌破后重新评估"],sources,
            "独立按需快查：不修改妖币池、资产映射、自选或模拟仓；不发送真实委托；复用带原始时间的缓存与受预算限制的采集");
    }
}
