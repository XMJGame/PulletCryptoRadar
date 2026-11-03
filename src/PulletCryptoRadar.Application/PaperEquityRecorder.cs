using PulletCryptoRadar.Core;
namespace PulletCryptoRadar.Application;

public static class PaperEquityRecorder
{
    public const string Version="Paper-Performance-1";
    public static PaperBatch Attach(PaperState before,PaperBatch batch,IReadOnlyList<PaperTrade> previousTrades,IReadOnlyList<QuantFundingEvent> previousFunding)
    {
        var s=batch.State;
        var changed=batch.Trades.Count>0||batch.FundingEvents.Count>0||!before.Positions.Select(p=>(p.Id,p.Quantity)).SequenceEqual(s.Positions.Select(p=>(p.Id,p.Quantity)));
        if(!changed&&before.LastEquitySampleAt is {} last&&s.UpdatedAt.ToUnixTimeSeconds()/60==last.ToUnixTimeSeconds()/60)return batch;
        var sample=Capture(s,previousTrades.Concat(batch.Trades).ToArray(),previousFunding.Concat(batch.FundingEvents).ToArray(),s.UpdatedAt);
        return batch with{State=s with{LastEquitySampleAt=s.UpdatedAt},EquitySamples=[sample]};
    }
    public static PaperEquitySample Capture(PaperState s,IReadOnlyList<PaperTrade> trades,IReadOnlyList<QuantFundingEvent> funding,DateTimeOffset now)
    {
        var issues=new List<string>();
        string Cohort(QuantLivePosition? q,DateTimeOffset opened)=>q is null?"Legacy":s.QuantControl is {} c&&opened>=c.ActivatedAt&&QuantRules.Hash(q.Parameters,q.Costs)==c.RulesHash?"Current":"Other";
        var rows=s.Positions.Select(p=>
        {
            var fresh=p.LastMark>0&&p.EntryPrice>0&&p.Quantity>0&&p.Direction is "Long" or "Short"
                &&p.LastMarkSourceTime!=default&&p.LastQuoteTime!=default&&p.LastMarkSourceTime<=s.UpdatedAt&&p.LastQuoteTime<=s.UpdatedAt
                &&p.LastMarkSourceTime<=now&&p.LastQuoteTime<=now&&now-p.LastMarkSourceTime<=TimeSpan.FromSeconds(45)&&now-p.LastQuoteTime<=TimeSpan.FromSeconds(45);
            decimal? gross=fresh?StructuralPaperEngine.Gross(p,p.LastMark):null;
            decimal? value=gross is {} g?p.Quant is not null?p.RemainingMargin+g:Math.Max(0,p.RemainingMargin+g):null;
            var adjustment=value-p.RemainingMargin-gross;
            return new PaperEquityMark(p.Id,p.Symbol,Cohort(p.Quant,p.OpenedAt),p.Direction,p.Quantity,
                StructuralPaperEngine.RemainingOriginalMargin(p),p.RemainingMargin,p.OpenFee,p.FundingCost,p.LastMark,p.LastMarkSourceTime,p.LastQuoteTime,fresh,gross,adjustment,value);
        }).ToArray();
        var accountFresh=s.UpdatedAt<=now&&now-s.UpdatedAt<=TimeSpan.FromSeconds(30);
        var marksFresh=accountFresh&&rows.All(r=>r.Fresh);
        if(!accountFresh)issues.Add("账户超过30秒未更新或时钟在未来");
        if(rows.Any(r=>!r.Fresh))issues.Add("持仓已提交标记价缺失、失鲜或时间无效");
        var covers=s.QuantFunding??[];
        var fundingObserved=covers.All(c=>c.Status=="ObservedWindow"&&c.CheckedAt<=s.UpdatedAt&&c.KnownThrough is {} t&&t<=c.CheckedAt)
            &&s.Positions.Where(p=>p.Quant is not null).All(p=>covers.Any(c=>c.PositionId==p.Id))
            &&trades.Where(t=>t.Quant is not null).All(t=>covers.Any(c=>c.PositionId==(t.PositionId??t.Id)));
        if(!fundingObserved)issues.Add("新模型资金费观察窗口待核对，未当作零费用");
        var origins=trades.GroupBy(t=>t.PositionId??t.Id).ToDictionary(g=>g.Key,g=>Cohort(g.First().Quant,g.First().OpenedAt));
        foreach(var row in rows)origins[row.PositionId]=row.Cohort;
        var unknownFunding=funding.Any(f=>!origins.ContainsKey(f.Evidence.PositionId));
        if(unknownFunding)issues.Add("存在无法关联仓位或成交的资金费事件");
        var contributions=new[]{"Legacy","Current","Other"}.Select(cohort=>
        {
            var settled=trades.Where(t=>Cohort(t.Quant,t.OpenedAt)==cohort).ToArray();var open=rows.Where(r=>r.Cohort==cohort).ToArray();
            var late=funding.Where(f=>origins.GetValueOrDefault(f.Evidence.PositionId,"Other")==cohort).Sum(f=>f.ClosedAllocated);
            var realized=settled.Sum(t=>t.NetPnl)-late;
            decimal? openNet=open.All(p=>p.Fresh)?open.Sum(p=>p.Value!.Value-p.OriginalMargin-p.OpenFee):null;
            return new PaperContribution(cohort,open.Length,settled.Length,realized,openNet,openNet+realized,
                settled.Sum(t=>t.Fees)+open.Sum(p=>p.OpenFee),settled.Sum(t=>t.FundingCost)+late+open.Sum(p=>p.FundingCost));
        }).ToArray();
        decimal? grossTotal=rows.All(r=>r.Fresh)?rows.Sum(r=>r.Gross!.Value):null;
        decimal? adjustmentTotal=rows.All(r=>r.Fresh)?rows.Sum(r=>r.Adjustment!.Value):null;
        var valued=s.Cash+rows.Sum(r=>r.RemainingMargin)+grossTotal+adjustmentTotal;
        decimal? expected=contributions.All(c=>c.TotalNet is not null)?s.Rules.InitialCash+contributions.Sum(c=>c.TotalNet!.Value):null;
        var difference=valued-expected;var reconciled=!unknownFunding&&difference is {} diff&&Math.Abs(diff)<=.00000001m;
        if(difference is {} discrepancy&&Math.Abs(discrepancy)>.00000001m)issues.Add("净值与初始本金及完整成交/持仓贡献不守恒");
        return new(Version,s.UpdatedAt,s.Revision,s.Rules.InitialCash,s.Cash,rows.Sum(r=>r.OriginalMargin),rows.Sum(r=>r.RemainingMargin),
            grossTotal,adjustmentTotal,marksFresh&&fundingObserved&&reconciled?valued:null,marksFresh,fundingObserved,reconciled,expected,difference,rows,contributions,issues);
    }
}
