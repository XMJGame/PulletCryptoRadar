using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Application;
public static class TokenResearchEngine
{
    public static bool SafeUrl(string? url)=>Uri.TryCreate(url,UriKind.Absolute,out var u)&&u.Scheme=="https"&&u.UserInfo.Length==0;
    public static TokenResearchAnalysis Analyze(TokenCheckReport report,IReadOnlyList<TokenResearchClaim> claims)
    {
        var now=report.AsOf;
        var items=claims.Where(c=>report.IdentityVerified&&c.CoinGeckoId==report.Coin.Id&&c.Platform==report.Platform
            &&TokenCheckEngine.Same(c.Platform,c.ContractAddress,report.ContractAddress)&&SafeUrl(c.SourceUrl)
            &&c.CheckedAt<=now&&c.ReviewAfter>c.CheckedAt&&c.ReviewAfter-c.CheckedAt<=TimeSpan.FromDays(31)
            &&(c.PublishedAt is null||c.PublishedAt<=c.CheckedAt))
            .Select(c=>new TokenResearchItem(c,now>c.ReviewAfter?"待复核":c.SourceKind=="ExplorerHistoricalRecord"?"历史线索待复核":c.Category=="Announcement"&&c.EventAt<now.AddDays(-7)?"历史事件":"已核对原文",
                c.SourceKind=="ProjectDocumentation"?"项目方文件中的声明；不等于实际使用、收入或履约已获第三方验证":c.SourceKind=="ExplorerHistoricalRecord"?"历史浏览器记录/代码名称线索，不等于本次已确认控制人或托管身份":"原始公告中的事件；不能单独解释当前涨跌"))
            .Take(40).ToArray();
        var active=items.Where(i=>i.Status!="待复核").Select(i=>i.Claim).ToArray();
        var fresh=report.IdentityVerified&&report.Project is {} p&&p.CoinGeckoId==report.Coin.Id&&TokenCheckEngine.Fresh(p.SourceTime,p.ReceivedTime,now,TimeSpan.FromHours(1));
        var project=fresh?report.Project:null;
        var releases=active.Where(c=>c.Category=="UnlockEvent"&&c.EventAt>now&&c.EventAt<=now.AddDays(7)&&c.TokenAmount>0).ToArray();
        decimal? amount=releases.Length==0?null:releases.Sum(c=>c.TokenAmount!.Value);
        var missing=new List<string>{"解锁日历未建立完整覆盖：条款不等于精确日期/数量；未列出事件不代表未来7天没有解锁",
            "尚未自动巡检最新公告/退市或证实当前涨跌原因；历史上所公告不能当作今天的利好",
            "产品实际用户、可核验收入、团队身份及代币价值捕获仍未完成独立尽调"};
        if(items.Length==0)missing.Add("所选完整链合约尚无经过原文核对的项目研究条目");
        return new(items,project?.CirculatingSupply>0&&project.TotalSupply>0?project.CirculatingSupply/project.TotalSupply*100:null,
            project?.FdvUsd>0&&project.MarketCapUsd>0?project.FdvUsd/project.MarketCapUsd:null,amount,
            amount is not null&&project?.CirculatingSupply>0?amount/project.CirculatingSupply*100:null,false,
            "当前行情原因未确认；下列文件和事件提供研究背景，时间相近也不证明因果",missing);
    }
    public static TokenCheckReport Enrich(TokenCheckReport report,TokenHolderAnalysis holders,TokenResearchAnalysis research)
    {
        var risks=report.Risks.ToList();
        if(research.UnlockToCirculatingPercent is >=5)risks.Add("已记录的未来7天解锁量至少相当于当前流通量5%；日历仍不完整，须进一步核验释放与接收地址");
        if(holders.Fresh&&holders.OrderedTop10&&holders.RemainingObservedPercent is >=50)
            risks.Add("前十中扣除已核验托管/基础设施后的已观察余额至少占供应50%；其中包含未知归属，不能解释为单一庄家持仓");
        return report with{HolderAnalysis=holders,Research=research,Risks=risks.Distinct().ToArray(),
            Missing=report.Missing.Concat(research.Missing).Distinct().ToArray(),
            EvidenceMeaning="10项核验范围中的已取比例；地址和项目文件仅为部分证据，日历/尽调/盘口未完成，不因增加链接提高分数。不是胜率/上涨概率"};
    }
}
