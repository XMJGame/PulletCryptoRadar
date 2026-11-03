using System.Globalization;
using System.Numerics;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Application;
public static class TokenMonitorEngine
{
    public static string Amount(BigInteger raw,int decimals)
    {
        if(decimals is <0 or >36)throw new ArgumentOutOfRangeException(nameof(decimals));
        var sign=raw<0?"-":"";var value=BigInteger.Abs(raw);var scale=BigInteger.Pow(10,decimals);
        var fraction=decimals==0?"":(value%scale).ToString(CultureInfo.InvariantCulture).PadLeft(decimals,'0').TrimEnd('0');
        return sign+(value/scale).ToString("N0",CultureInfo.InvariantCulture)+(fraction.Length>0?"."+fraction:"");
    }
    public static decimal? Percent(BigInteger value,BigInteger supply)=>supply>0&&BigInteger.Abs(value)<=supply?
        (decimal)(value*BigInteger.Pow(10,18)/supply)/10_000_000_000_000_000m:null;
    public static BigInteger Permitted7Days(VestingAllocation allocation,DateTimeOffset at)
    {
        var cap=BigInteger.Parse(allocation.RawCap,CultureInfo.InvariantCulture);var released=BigInteger.Parse(allocation.RawReleased,CultureInfo.InvariantCulture);var amount=BigInteger.Parse(allocation.RawReleaseAmount,CultureInfo.InvariantCulture);
        if(amount<=0||cap-released<amount||allocation.NextRelease is not {} next||next>at.AddDays(7))return 0;
        var count=allocation.PeriodSeconds==0?(cap-released)/amount:new BigInteger(1+(long)(at.AddDays(7)- (next<at?at:next)).TotalSeconds/allocation.PeriodSeconds);
        return BigInteger.Min(count,(cap-released)/amount)*amount;
    }
    public static bool ActiveIdentity(TokenMonitorState state,DateTimeOffset now)=>!state.IdentityRevoked&&!state.Halted&&state.Identity is {} id&&id.SourceTime<=id.CheckedAt&&id.CheckedAt<=now&&id.ReviewAfter>=now;
    public static TokenMonitorSnapshot FreezeFinding(TokenMonitorSnapshot snapshot,TokenMonitorView view,DateTimeOffset analyzedAt)=>snapshot with
    {
        FindingVersion="Token-Monitor-1",AnalyzedAt=analyzedAt,
        Finding=$"固定观察{view.Balances.Count}个地址；"+(view.BaselineTime is null?"24h基线不足，不能判断吸筹或派发；":"已有可比24h基线，余额变化只描述该集合，不推断控制人；")+
            (view.EarliestPermitted7Days is {} amount?$"按此区块参数的7天普通释放上界{amount}，不代表实际解锁或卖出；":"缺少可用锁仓参数，释放量未知；")+
            view.TransferCoverage
    };
    public static TokenMonitorView View(TokenMonitorTarget target,TokenMonitorState state,IReadOnlyList<TokenMonitorSnapshot> history,
        IReadOnlyList<AddressLabel> labels,IReadOnlyList<TokenResearchClaim> claims,DateTimeOffset now)
    {
        var s=state.Latest;var valid=s is not null&&s.TargetId==target.Id&&s.ContractAddress==target.ContractAddress&&s.SourceTime<=s.ReceivedTime&&s.ReceivedTime<=now;
        if(!valid)s=null;
        var fresh=s is not null&&ActiveIdentity(state,now)&&now-s.SourceTime<=TimeSpan.FromMinutes(45);
        var prior=s is not null?history.Where(p=>p.TargetId==target.Id&&p.ContractAddress==target.ContractAddress&&p.Decimals==s.Decimals&&p.Window.AddressSetHash==s.Window.AddressSetHash
            &&p.SourceTime<=p.ReceivedTime&&p.ReceivedTime<=now&&Math.Abs((s.SourceTime-p.SourceTime).TotalHours-24)<=1).MinBy(p=>Math.Abs((s.SourceTime-p.SourceTime).TotalHours-24)):null;
        var supply=s is not null?BigInteger.Parse(s.RawTotalSupply,CultureInfo.InvariantCulture):BigInteger.Zero;
        var vetted=labels.Where(l=>l.Chain=="bsc"&&l.Verified&&l.UpdatedAt<=now&&now-l.UpdatedAt<=TimeSpan.FromDays(90)&&TokenResearchEngine.SafeUrl(l.EvidenceUrl)).ToArray();
        var clues=claims.Where(c=>c.CoinGeckoId==target.CoinGeckoId&&c.Platform==target.Platform&&TokenCheckEngine.Same(c.Platform,c.ContractAddress,target.ContractAddress)
            &&c.Category=="AddressHint"&&c.CheckedAt<=now&&c.ReviewAfter>=now&&TokenResearchEngine.SafeUrl(c.SourceUrl)).ToArray();
        string? Candidate(string a)=>clues.FirstOrDefault(c=>c.Address is {} address&&address.Equals(a,StringComparison.OrdinalIgnoreCase))?.RoleHint;
        bool Exchange(string a)=>vetted.Any(l=>l.Address.Equals(a,StringComparison.OrdinalIgnoreCase)&&l.Type==WalletLabelType.Exchange);
        var balances=(s?.Balances??[]).Select(b=>
        {
            var raw=BigInteger.Parse(b.RawBalance,CultureInfo.InvariantCulture);var before=prior?.Balances.FirstOrDefault(p=>p.Address==b.Address);
            var label=vetted.FirstOrDefault(l=>l.Address.Equals(b.Address,StringComparison.OrdinalIgnoreCase));
            var role=s?.Vesting?.Address==b.Address?"已核对代码的Vesting合约；受益人控制权未知":Exchange(b.Address)?"已核验交易所":Candidate(b.Address)??(label is not null?label.Type.ToString():"固定观察地址，归属未知");
            return new MonitorBalanceView(b.Address,Amount(raw,s!.Decimals),fresh?Percent(raw,supply):null,
                fresh&&before is not null?Amount(raw-BigInteger.Parse(before.RawBalance,CultureInfo.InvariantCulture),s.Decimals):null,
                fresh&&before is not null?Percent(raw,supply)-Percent(BigInteger.Parse(before.RawBalance,CultureInfo.InvariantCulture),BigInteger.Parse(prior!.RawTotalSupply,CultureInfo.InvariantCulture)):null,role,
                label is not null?"已有人工标签；控制权仍需独立证明":Candidate(b.Address) is not null?"历史线索未升级归属":"按确认区块读取余额，不推定实际控制人");
        }).ToArray();
        var releases=(s?.Vesting?.Allocations??[]).Select(a=>
        {
            var remaining=BigInteger.Parse(a.RawCap,CultureInfo.InvariantCulture)-BigInteger.Parse(a.RawReleased,CultureInfo.InvariantCulture);var amount=BigInteger.Parse(a.RawReleaseAmount,CultureInfo.InvariantCulture);
            var status=s!.Vesting!.TgeReleased==false?"TGE未释放，普通释放条件未齐":amount==0?"普通单次额度为零":remaining<amount?"普通批次已不足，余款仍受全计划完成条件限制":a.NextRelease<=s.SourceTime?"按此区块参数已到可调用时间；未证明已转出":"尚未到当前nextRelease";
            return new VestingReleaseView(a.Name,a.Recipient,Amount(BigInteger.Parse(a.RawCap,CultureInfo.InvariantCulture),s.Decimals),Amount(BigInteger.Parse(a.RawReleased,CultureInfo.InvariantCulture),s.Decimals),Amount(remaining,s.Decimals),Amount(amount,s.Decimals),a.PeriodSeconds,a.NextRelease,status,
                s.Vesting.TgeReleased?Amount(Permitted7Days(a,s.SourceTime),s.Decimals):"Unknown");
        }).ToArray();
        BigInteger? permitted=s?.Vesting?.TgeReleased==true?s.Vesting.Allocations.Aggregate(BigInteger.Zero,(n,a)=>n+Permitted7Days(a,s.SourceTime)):null;
        var events=history.Append(s).OfType<TokenMonitorSnapshot>().Where(p=>p.TargetId==target.Id&&p.ContractAddress==target.ContractAddress&&p.Window.AddressSetHash==s?.Window.AddressSetHash)
            .SelectMany(p=>p.Transfers).Where(t=>t.Chain=="bsc"&&t.ContractAddress==target.ContractAddress&&t.BlockTime>=now.AddHours(-24)&&t.BlockTime<=t.ReceivedTime&&t.ReceivedTime<=now
                &&t.From!=t.To&&t.From!="0x"+new string('0',40)&&BigInteger.TryParse(t.RawAmount,out var n)&&n>0).DistinctBy(t=>(t.TransactionHash,t.LogIndex)).ToArray();
        var coverage=s is not null&&s.Window.StartTime<=now.AddHours(-24)&&now-s.Window.ThroughTime<=TimeSpan.FromMinutes(5)&&fresh?
            "固定观察地址过滤器已覆盖近24h确认区块；只覆盖这些地址，不是全链资金流":s is null?"尚未建立确认区块窗口":"窗口不足24h或仍在追赶；没有事件不表示没有转账";
        var caveats=new List<string>{"固定地址来自已核验报告/索引器样本，余额占比之和不是当前全链Top10；未跟踪的新大户不会被自动发现",
            "24h对比须有同观察集合、同精度且相隔23～25小时的基线；集合改变后重新积累，不将缺失余额当零",
            "7天额度按此区块当前参数、管理员最早调用普通release计算，不是确定解锁日历/实际流通增量或卖压；延迟调用会改变下一次时间",
            "仅采集固定合约的只读方法与过滤后的Transfer；转入交易所不等于卖出，同一来源不证明同一控制人",
            "存在未核验的多跳、Gas来源和机构标签；历史Gate线索单独计数，不计作已核验交易所净流入"};
        if(permitted is {} due&&s?.Vesting is {} v&&due>BigInteger.Parse(v.RawBalance,CultureInfo.InvariantCulture))caveats.Add("理论可调用额度超过当前锁仓余额，不能保证全部履约；未将上界当作实际释放");
        return new(target,ActiveIdentity(state,now)?"固定合约监测身份已留证（非实时交易映射）":state.IdentityRevoked?"身份冲突，已暂停":state.Halted?"区块锚点变化，已暂停":"等待完整身份核验",state.Identity,state.IdentityError,state.RpcError,
            state.NextRpcAttempt,state.Halted,fresh,s?.BlockNumber,s?.BlockHash,s?.SourceTime,prior?.SourceTime,
            fresh?Percent((s?.Balances??[]).Aggregate(BigInteger.Zero,(n,b)=>n+BigInteger.Parse(b.RawBalance,CultureInfo.InvariantCulture)),supply):null,balances,s?.Vesting,releases,
            permitted is {} total&&s is not null?Amount(total,s.Decimals):null,fresh&&permitted is {} value?Percent(value,supply):null,coverage,s?.Window.StartTime,s?.Window.ThroughTime,s is null?null:events.Length,
            s is null?null:events.Count(t=>Exchange(t.To)&&!Exchange(t.From)),s is null?null:events.Count(t=>!Exchange(t.To)&&Candidate(t.To)?.Contains("交易所",StringComparison.Ordinal)==true),
            events.OrderByDescending(t=>t.BlockTime).Take(20).ToArray(),caveats){Finding=s?.Finding,FindingVersion=s?.FindingVersion,AnalyzedAt=s?.AnalyzedAt};
    }
}
