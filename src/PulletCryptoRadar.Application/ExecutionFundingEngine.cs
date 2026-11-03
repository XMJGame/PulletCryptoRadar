using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Application;
public static class ExecutionFundingEngine
{
    public const string Version="Funding-Audit-1";
    public static FundingAuditResult Evaluate(FundingExposure exposure,FundingRateWindow? window,DateTimeOffset now,decimal legacyCost,DateTimeOffset? requiredThrough=null)
    {
        var through=requiredThrough??exposure.ClosedAt??now.AddMinutes(-2);var from=exposure.OpenedAt;
        FundingAuditResult Result(string status,string reason,IReadOnlyList<FundingAuditPayment> rows,bool complete=false)=>
            new(exposure.PositionId,exposure.Symbol,exposure.Direction,status,reason,from,through,window?.Through,
                complete?rows.Sum(p=>p.SignedCost):null,rows.Sum(p=>p.SignedCost),legacyCost,rows);
        if(exposure.PositionId==Guid.Empty||exposure.Direction is not ("Long" or "Short")||exposure.InitialQuantity<=0||from>now||through<from||through>now||exposure.ClosedAt is {} closed&&through>closed
            ||exposure.ClosedAt>now||exposure.Reductions.Any(r=>r.Quantity<=0||r.At<from||r.At>now||exposure.ClosedAt is {} end&&r.At>end)
            ||exposure.Reductions.Sum(r=>r.Quantity)>exposure.InitialQuantity
            ||exposure.ClosedAt is not null&&exposure.Reductions.Sum(r=>r.Quantity)!=exposure.InitialQuantity)
            return Result("Unknown","持仓生命周期或减仓数量不完整，不能重建结算时数量",[]);
        if(window is null||window.Symbol!=exposure.Symbol||window.ReceivedAt>now||window.From>window.Through||window.Through>window.ReceivedAt)
            return Result("Unknown","缺少有效的同合约实际资金费查询窗口",[]);
        if(window.Records.Any(r=>r.Symbol!=exposure.Symbol||r.FundingAt<window.From||r.FundingAt>window.Through||r.MarkPrice<=0||Math.Abs(r.Rate)>1||r.ReceivedAt>now||r.ReceivedAt<r.FundingAt
            ||r.RateType is not ("Regular" or "Special")))return Result("Unknown","实际结算记录含身份、价格或时间错误",[]);
        var records=window.Records.Where(r=>r.FundingAt>from&&r.FundingAt<=through).ToArray();
        var groups=records.GroupBy(r=>(r.FundingAt,r.RateType)).ToArray();
        if(groups.Any(g=>g.Select(r=>(r.Rate,r.MarkPrice)).Distinct().Count()!=1))return Result("Unknown","同一结算时点的来源记录冲突",[]);
        var payments=new List<FundingAuditPayment>();
        foreach(var row in groups.Select(g=>g.First()).OrderBy(r=>r.FundingAt))
        {
            // Funding is processed before a fill at the exact same timestamp: exposure interval (open, close].
            var quantity=exposure.InitialQuantity-exposure.Reductions.Where(r=>r.At<row.FundingAt).Sum(r=>r.Quantity);
            if(quantity<=0)continue;
            var cost=quantity*row.MarkPrice*row.Rate*(exposure.Direction=="Short"?-1:1);
            payments.Add(new(Version,exposure.PositionId,exposure.Symbol,exposure.Direction,row.FundingAt,row.RateType,quantity,row.Rate,row.MarkPrice,cost,row.ReceivedAt));
        }
        var complete=window.Complete&&window.From<=from&&window.Through>=through&&(exposure.ClosedAt is null||through>=exposure.ClosedAt);
        return Result(complete?"ObservedWindow":"Partial",complete?"实际返回记录及结算标记价核验；正数付费、负数收取。不是原账本重结算，也不证明供应方无遗漏。":"窗口未覆盖完整持仓期或返回被截断；只列已观察成本，完整成本为Unknown",payments,complete);
    }
}
