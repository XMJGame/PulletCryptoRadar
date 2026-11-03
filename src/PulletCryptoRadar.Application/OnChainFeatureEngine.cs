using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Application;

public sealed class OnChainFeatureEngine
{
    public OnChainFeatures Calculate(ChainBinding binding,OnChainSample? sample,IReadOnlyList<AddressLabel> labels,OnChainSample? prior,bool identityTrusted,DateTimeOffset now)
    {
        if(sample is not null&&(sample.Chain!=binding.Chain||!ChainIdentity.Same(binding.Chain,sample.ContractAddress,binding.ContractAddress)))sample=null;
        var status=sample is null?DataStatus.Missing:sample.SourceTime>now||sample.ReceivedTime>now?DataStatus.Invalid:now-sample.SourceTime>TimeSpan.FromMinutes(45)?DataStatus.Stale:DataStatus.Valid;
        var source=sample?.Provider??"OnChain";var sourceTime=sample?.SourceTime;
        FeatureValue<decimal> F(decimal? value)=>new(value,value is null?DataStatus.Missing:status,sourceTime,source);
        var rows=sample?.Holders??[];var activeLabels=labels.Where(l=>l.Chain==binding.Chain&&l.Verified&&l.UpdatedAt<=now).ToDictionary(l=>l.Address,ChainIdentity.Comparer(binding.Chain));
        WalletLabelType Type(TokenHolder h)=>activeLabels.TryGetValue(h.Address,out var label)?label.Type:
            h.Address.Equals("0x0000000000000000000000000000000000000000",StringComparison.OrdinalIgnoreCase)||h.Address.Equals("0x000000000000000000000000000000000000dead",StringComparison.OrdinalIgnoreCase)?WalletLabelType.Burn:WalletLabelType.Unknown;
        var ranked=sample?.OrderedTopHolders==true&&rows.Count>0;
        var complete=sample?.HolderCount is {} total&&total<=rows.Count;
        var raw=new Dictionary<int,FeatureValue<decimal>>();foreach(var n in new[]{10,25,50,100})raw[n]=F(ranked&&(rows.Count>=n||complete)?rows.Take(n).Sum(h=>h.Percent):null);
        var eligible=rows.Where(h=>Type(h) is not(WalletLabelType.Burn or WalletLabelType.LP or WalletLabelType.Bridge)).ToArray();
        var adjusted=F(ranked&&(eligible.Length>=10||complete)?eligible.Take(10).Sum(h=>h.Percent):null);
        var nonCustodial=eligible.Where(h=>Type(h) is not(WalletLabelType.Exchange or WalletLabelType.Custodian or WalletLabelType.Broker)).ToArray();
        var nonCustodialTop10=F(ranked&&(nonCustodial.Length>=10||complete)?nonCustodial.Take(10).Sum(h=>h.Percent):null);
        var observed=rows.Sum(h=>h.Percent);var known=rows.Where(h=>Type(h) is not(WalletLabelType.Unknown or WalletLabelType.Contract)).Sum(h=>h.Percent);
        var coverage=observed>0?100*known/observed:0;
        decimal? change=null,changePercent=null;
        if(sample?.HolderCount is {} current&&prior?.HolderCount is >0&&prior.Provider==sample.Provider&&prior.Chain==sample.Chain
            &&ChainIdentity.Same(sample.Chain,prior.ContractAddress,sample.ContractAddress)&&sample.SourceTime-prior.SourceTime>=TimeSpan.FromHours(23)
            &&sample.SourceTime-prior.SourceTime<=TimeSpan.FromHours(25.5)&&prior.ReceivedTime<=sample.ReceivedTime)
        {change=(decimal)current-prior.HolderCount;changePercent=change/prior.HolderCount*100;}
        return new(){TrustedIdentity=identityTrusted&&binding.Enabled&&binding.Verified,Chain=binding.Chain,ContractAddress=binding.ContractAddress,Provider=sample?.Provider,
            BindingRevision=binding.Revision,ReturnedHolders=rows.Count,HolderCount=new(sample?.HolderCount,sample?.HolderCount is null?DataStatus.Missing:status,sourceTime,source),
            HolderChange24h=F(change),HolderChange24hPercent=F(changePercent),RawTopPercent=raw,AdjustedTop10Percent=adjusted,
            NonCustodialTop10Percent=nonCustodialTop10,ObservedExchangePercent=F(sample is null?null:rows.Where(h=>Type(h)==WalletLabelType.Exchange).Sum(h=>h.Percent)),
            ObservedProjectPercent=F(sample is null?null:rows.Where(h=>Type(h) is WalletLabelType.Project or WalletLabelType.Team or WalletLabelType.Investor or WalletLabelType.Treasury).Sum(h=>h.Percent)),
            ClassificationCoveragePercent=coverage,ConcentrationUsable=identityTrusted&&binding.Enabled&&binding.Verified&&status==DataStatus.Valid&&adjusted.Value is not null&&coverage>=50,
            Labels=labels.Where(l=>l.Chain==binding.Chain).OrderBy(l=>l.Address,ChainIdentity.Comparer(binding.Chain)).ToArray(),
            Caveats=[sample?.TimeBasis??"尚无链上快照","AdjustedTop10 从已返回排名中排除已核对 Burn/LP/Bridge 后取前十；分母仍为总供应量，非流通量",
                "NonCustodialTop10进一步排除已核对Exchange/Custodian/Broker；做市商本身不等于托管，不自动排除",
                "Exchange/Project 占比仅为已返回地址中已标注余额的下界，不代表全链总量；普通合约不等于 LP 或项目方",
                "Unknown 与未核对标签不排除；普通 Contract 也不计入已核对分类覆盖。覆盖不足 50% 时集中度不进入可信评分；Provider 报告的 tag 仅为线索",
                change is null?"尚无同提供者、同链同合约约 24h 的可靠人数基线，变化保持 Unknown":"24h 变化使用同一索引器的历史人数，非逐块人数",
                !identityTrusted?"资产合约身份未核对，原始数据只作参考，不进入可信评分":"合约参考身份可用；市值与备用交易所仍按各自身份门槛判断"]};
    }
}
