using System.Numerics;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Application;
public static class TokenHolderResearch
{
    private static bool Custody(WalletLabelType t)=>t is WalletLabelType.Exchange or WalletLabelType.Custodian or WalletLabelType.Broker or WalletLabelType.LP or WalletLabelType.Bridge or WalletLabelType.Burn;
    private static string Role(WalletLabelType type)=>type switch{WalletLabelType.Exchange=>"交易所",WalletLabelType.Project=>"项目方",WalletLabelType.Team=>"团队",WalletLabelType.Investor=>"投资者",WalletLabelType.Treasury=>"金库",WalletLabelType.LP=>"流动性池",WalletLabelType.Bridge=>"跨链桥",WalletLabelType.Burn=>"销毁地址",WalletLabelType.Whale=>"大户（控制人未确认）",WalletLabelType.Contract=>"合约（用途未确认）",WalletLabelType.MarketMaker=>"做市商",WalletLabelType.Broker=>"经纪/券商",WalletLabelType.Custodian=>"托管机构",_=>"未知"};
    public static string Explorer(string chain)=>chain switch{"bsc"=>"https://bscscan.com","ethereum"=>"https://etherscan.io","base"=>"https://basescan.org","arbitrum"=>"https://arbiscan.io","polygon"=>"https://polygonscan.com","solana"=>"https://solscan.io",_=>""};
    public static string AddressUrl(string chain,string address)=>Explorer(chain)+"/"+(chain=="solana"?"account/":"address/")+Uri.EscapeDataString(address);
    private static bool InfrastructureHint(string tag)=>new[]{"exchange","binance","coinbase","gate.io","okx","bybit","kucoin","bridge","liquidity","pancake","uniswap","router","custod"}.Any(s=>tag.Contains(s,StringComparison.OrdinalIgnoreCase));
    public static TokenHolderAnalysis Analyze(string chain,string contract,bool identity,OnChainSample? sample,IReadOnlyList<OnChainSample> history,
        IReadOnlyList<AddressLabel> labels,IReadOnlyList<TokenTransfer> transfers,FlowCursor? cursor,DateTimeOffset now,IReadOnlyList<TokenResearchClaim>? addressClues=null)
    {
        string Key(string a)=>ChainIdentity.Normalize(chain,a);
        bool Same(string a,string b)=>Key(a)==Key(b);
        var matched=sample is not null&&sample.Chain==chain&&Same(sample.ContractAddress,contract);
        if(!matched)sample=null;
        var fresh=identity&&sample is not null&&TokenCheckEngine.Fresh(sample.SourceTime,sample.ReceivedTime,now,TimeSpan.FromMinutes(45));
        var labelMap=labels.Where(l=>l.Chain==chain&&ChainIdentity.Address(chain,l.Address)&&l.Verified&&l.Type!=WalletLabelType.Unknown
                &&TokenResearchEngine.SafeUrl(l.EvidenceUrl)&&l.UpdatedAt<=now&&now-l.UpdatedAt<=TimeSpan.FromDays(90))
            .GroupBy(l=>Key(l.Address)).Where(g=>g.Select(l=>l.Type).Distinct().Count()==1).ToDictionary(g=>g.Key,g=>g.MaxBy(l=>l.Revision)!);
        var security=sample?.Security;
        var securityOk=security is not null&&security.Chain==chain&&Same(security.ContractAddress,contract)&&security.Error is null
            &&TokenCheckEngine.Fresh(security.SourceTime,security.ReceivedTime,now,TimeSpan.FromMinutes(45));
        var hints=securityOk?security!.Holders:[];
        var raw=sample?.Holders??[];
        var valid=raw.Where(h=>ChainIdentity.Address(chain,h.Address)&&h.Percent is >=0 and <=100).ToArray();
        var unique=valid.GroupBy(h=>Key(h.Address)).Select(g=>g.First()).OrderByDescending(h=>h.Percent).Take(10).ToArray();
        var ordered=chain!="solana"&&sample?.OrderedTopHolders==true&&valid.Length==raw.Count&&valid.Select(h=>Key(h.Address)).Distinct().Count()==valid.Length
            &&valid.Length>=10&&valid.Sum(h=>h.Percent)<=100.01m;
        var prior=sample is not null?history.Where(s=>s.Chain==chain&&Same(s.ContractAddress,contract)&&s.Provider==sample.Provider&&s.OrderedTopHolders==sample.OrderedTopHolders
                &&s.SourceTime<=s.ReceivedTime&&s.ReceivedTime<=now&&Math.Abs((sample.SourceTime-s.SourceTime).TotalHours-24)<=1)
            .MinBy(s=>Math.Abs((sample.SourceTime-s.SourceTime).TotalHours-24)):null;
        var rows=unique.Select((h,i)=>
        {
            var label=labelMap.GetValueOrDefault(Key(h.Address));var hint=hints.FirstOrDefault(x=>Same(x.Address,h.Address));
            var clue=identity?(addressClues??[]).FirstOrDefault(c=>c.Category=="AddressHint"&&c.Platform==ChainIdentity.Platform(chain)
                &&Same(c.ContractAddress,contract)&&c.Address is {} a&&ChainIdentity.Address(chain,a)&&Same(a,h.Address)
                &&c.CheckedAt<=now&&c.ReviewAfter>=now&&TokenResearchEngine.SafeUrl(c.SourceUrl)):null;
            var tag=hint?.ReportedTag is {Length:>0} t?t:h.ReportedTag;
            var role=label is not null?Role(label.Type):clue?.RoleHint??(hint?.IsContract==true?"合约地址（用途未知）":h.Percent>=1?"大额地址（归属未知）":"归属未知");
            var old=prior?.Holders.FirstOrDefault(p=>Same(p.Address,h.Address));
            return new HolderResearchRow(ordered?i+1:null,h.Address,h.Percent,role,identity&&label is not null?"有人工归属证据":clue is not null?"历史公开资料线索，未计入已核验":tag.Length>0?"第三方标签待核验":"仅链上余额/检测线索",tag,
                hint?.IsContract,hint?.IsLocked,hint?.TokenAccount,AddressUrl(chain,h.Address),label?.EvidenceUrl??clue?.SourceUrl,label?.UpdatedAt??clue?.CheckedAt,
                fresh&&old is not null?h.Percent-old.Percent:null);
        }).ToArray();
        var known=unique.Where(h=>identity&&labelMap.TryGetValue(Key(h.Address),out var l)&&Custody(l.Type)).Sum(h=>h.Percent);
        var classified=unique.Where(h=>identity&&labelMap.ContainsKey(Key(h.Address))).Sum(h=>h.Percent);
        var total=unique.Sum(h=>h.Percent);var topKeys=unique.Select(h=>Key(h.Address)).ToHashSet();
        bool Hub(string address)=>labelMap.TryGetValue(Key(address),out var l)&&(Custody(l.Type)||l.Type==WalletLabelType.Contract)
            ||hints.Any(h=>Same(h.Address,address)&&(h.IsContract==true||InfrastructureHint(h.ReportedTag)))
            ||unique.Any(h=>Same(h.Address,address)&&InfrastructureHint(h.ReportedTag));
        var observations=identity?transfers.Where(t=>t.Chain==chain&&Same(t.ContractAddress,contract)&&ChainIdentity.Address(chain,t.From)&&ChainIdentity.Address(chain,t.To)
                &&t.BlockTime>=now.AddHours(-24)&&t.BlockTime<=t.ReceivedTime&&t.ReceivedTime<=now&&!Same(t.From,t.To)
                &&BigInteger.TryParse(t.RawAmount,out var amount)&&amount>0&&!Key(t.From).Equals("0x"+new string('0',40)))
            .DistinctBy(t=>(t.TransactionHash,t.LogIndex)).ToArray():[];
        HolderRelationship Edge(string kind,TokenTransfer[] group,string counterparty,IReadOnlyList<string> addresses,string meaning)=>new(kind,addresses,counterparty,group.Length,
            group.Min(t=>t.BlockTime),group.Max(t=>t.BlockTime),group.Select(t=>Explorer(chain)+"/tx/"+Uri.EscapeDataString(t.TransactionHash)).Distinct().Take(5).ToArray(),meaning);
        var relations=new List<HolderRelationship>();
        foreach(var g in observations.Where(t=>topKeys.Contains(Key(t.From))&&topKeys.Contains(Key(t.To))).GroupBy(t=>(Key(t.From),Key(t.To))))
            relations.Add(Edge("前十地址之间有转账",g.ToArray(),g.Key.Item1,[g.Key.Item1,g.Key.Item2],"只证明该代币转账；不能证明共同控制，向交易所充值也不等于卖出"));
        foreach(var g in observations.GroupBy(t=>Key(t.From)))
        {
            var destinations=g.Where(t=>topKeys.Contains(Key(t.To))).Select(t=>Key(t.To)).Distinct().ToArray();
            if(destinations.Length<2||Hub(g.Key))continue;
            relations.Add(Edge("共同代币转出来源线索",g.Where(t=>destinations.Contains(Key(t.To))).ToArray(),g.Key,destinations,
                "仅观测到共同代币来源，未检查全链原生币Gas首笔来源；未排除所有未标记交易所/分发合约，不能据此认定同一庄家"));
        }
        var mapChain=chain=="ethereum"?"eth":chain;
        var tools=new List<ResearchLink>{new("区块浏览器持币页",Explorer(chain)+"/token/"+Uri.EscapeDataString(contract)+(chain=="solana"?"#holders":"#balances")),
            new("Bubblemaps 地址关联图（外部按需打开）",$"https://iframe.bubblemaps.io/map?chain={mapChain}&address={Uri.EscapeDataString(contract)}&partnerId=demo")};
        var complete=cursor is not null&&cursor.Chain==chain&&Same(cursor.ContractAddress,contract)&&cursor.UpdatedAt<=now&&cursor.LastBlockTime<=now
            &&cursor.StartTime<=now.AddHours(-24)&&now-cursor.LastBlockTime<=TimeSpan.FromMinutes(15)&&observations.Length<1000;
        var caveats=new List<string>{"“大户”描述规模，“做市商/经纪机构”描述业务；均不能仅凭余额或转账认定。庄家/操盘手不自动打标签",
            "已剔除部分只扣除本次已返回地址中有有效人工证据的交易所/托管/经纪、桥、池和销毁地址；未知项仍在剩余值中，未补位重排Top10",
            "提供者标签、合约与锁定检测是线索；归属证据90天后待复核。共同交易所出金、空投和路由可造成虚假关联",
            "余额占比变化使用同提供者约24h快照，缺少基线或地址未在旧样本中则Unknown；占比变化不等于净买卖"};
        if(!fresh)caveats.Add("分布缺失/失鲜或身份未核验，当前集中度指标不输出；历史地址仍可复查");
        if(chain=="solana")caveats.Add("Solana条目按Owner去重展示，保留Token Account线索；返回账户不构成全链Owner Top10，不计算全链前十集中度");
        return new(chain,contract,sample?.SourceTime,fresh,ordered,fresh&&ordered?total:null,fresh&&ordered?known:null,fresh&&ordered?total-known:null,
            fresh&&total>0?classified/total*100:null,rows,relations.Take(30).ToArray(),tools,
            ordered?"供应量百分比，非流通量百分比；剩余值仍含未知归属，不是庄家控盘比例":"仅展示已返回的大地址样本，不宣称全链前十或庄家占比",
            complete?"本地已确认区块覆盖近24h；仅该代币转账，关系仍非控制权证明":"本地转账窗口不完整/未采集或达到1000条上限；没有连线不代表没有关联",caveats);
    }
}
