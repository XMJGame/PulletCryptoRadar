using PulletCryptoRadar.Core;
namespace PulletCryptoRadar.Application;

public static class QuantEntryEngine
{
    public const string Version="Quant-Entry-Recheck-1";
    public static string ReviewId(PaperIntent intent,DateTimeOffset at,long revision)=>QuantRules.HashValue(new{Version,revision,intent.Key,At=at});
    public static QuantEntryEvidence Evidence(PaperIntent intent,QuantLiveControl control,QuantPaperInput input,QuantPortfolio portfolio,decimal reserved,
        IReadOnlyList<QuantFundingCoverage> funding,bool verified)
    {
        var d=intent.QuantIntent!.Decision;var names=portfolio.Reservations.Select(r=>r.Symbol).Append(d.Symbol).ToHashSet(StringComparer.Ordinal);
        var market=input.Markets.FirstOrDefault(m=>m.Instrument.Symbol==d.Symbol);
        return new(market is null?null:QuantRules.Input(market),input.Snapshot?.Context??intent.QuantIntent.Assessment.Context,portfolio,reserved,input.Execution.GetValueOrDefault(d.Symbol),
            input.Frames.Where(p=>names.Contains(p.Key)).OrderBy(p=>p.Key,StringComparer.Ordinal).Select(p=>new QuantEntryFrame(p.Key,p.Value.Hour,p.Value.Issue,p.Value.Bars.TakeLast(control.Parameters.CorrelationHours+1).ToArray())).ToArray(),funding.ToArray(),verified);
    }
    public static QuantEntryReview Capture(PaperIntent intent,QuantLiveControl control,QuantEntryEvidence evidence,DateTimeOffset at,long revision,
        string phase,string outcome,string reason,QuantAssessment? proposed=null,QuantAssessment? selected=null)
    {
        var old=intent.QuantIntent!.Assessment.Plan!;var raw=proposed?.Plan;var plan=selected?.Plan;
        var values=new QuantEntryValue[]{
            new("Quantity","数量",old.Quantity,raw?.Quantity,plan?.Quantity,plan is null?null:plan.Quantity>0&&plan.Quantity<=old.Quantity,"不得超过原批准数量；仍按当前数量网格向下取整"),
            new("Leverage","杠杆",old.Leverage,raw?.Leverage,plan?.Leverage,plan is null?null:plan.Leverage>=1&&plan.Leverage<=old.Leverage,"不得超过原批准杠杆"),
            new("Risk","含成本计划风险U",old.EstimatedRisk,raw?.EstimatedRisk,plan?.EstimatedRisk,plan is null?null:plan.EstimatedRisk<=old.EstimatedRisk,"不得超过原批准风险及当前组合剩余额度"),
            new("Stop","结构止损",old.Stop,raw?.Stop,plan?.Stop,plan is null?null:plan.Stop==old.Stop,"取整止损必须保持原批准值；不移动结构"),
            new("Margin","保证金U",old.Margin,raw?.Margin,plan?.Margin,plan is null?null:plan.Margin<=control.Parameters.MaximumMargin,"可以因报价/杠杆变化而改变，仍受当前保证金与现金额度约束"),
            new("Fee","入场手续费U",old.Fee,raw?.Fee,plan?.Fee,null,"展示当前费用，不将手续费变化单独当作扩仓许可"),
            new("Cash","扣除其他意向预留后的现金U",intent.QuantIntent.Assessment.Portfolio?.Cash,evidence.Portfolio.Cash,evidence.Portfolio.Cash-evidence.CashReserved,
                plan is null?null:plan.Margin+plan.Fee<=evidence.Portfolio.Cash-evidence.CashReserved,"实际成交须足额保留其他意向的保证金与手续费")};
        return new(ReviewId(intent,at,revision),Version,at,revision,phase,outcome,reason,intent,control,proposed,selected,values,evidence);
    }
    public static QuantEntryReview Evaluate(PaperIntent intent,QuantLiveControl control,QuantEntryEvidence evidence,DateTimeOffset at,long revision)
    {
        if(intent.QuantIntent is not {} original||original.Assessment.Plan is not {} approved)throw new InvalidDataException("缺少原批准计划");
        if(!evidence.SignalVerified)return Capture(intent,control,evidence,at,revision,"Signal","Cancelled","原始前瞻信号未核验，不进行复核成交");
        if(at>original.Decision.ExpiresAt)return Capture(intent,control,evidence,at,revision,"Signal","Cancelled","冻结信号已过期，不补单");
        if(evidence.Funding.Any(f=>f.Status!="ObservedWindow"))return Capture(intent,control,evidence,at,revision,"Funding","Waiting","已有新仓资金费覆盖未齐或来源冲突");
        if(evidence.Market is not {} market||evidence.Execution is not {} execution||evidence.Context is not {} context)
            return Capture(intent,control,evidence,at,revision,"Inputs","Waiting","等待身份、行情、环境或执行资料");
        if(market.AssetId!=original.Decision.Market.AssetId||market.Symbol!=intent.Symbol)return Capture(intent,control,evidence,at,revision,"Identity","Cancelled","复核资产身份与原批准标的不一致");
        if(market.PriceAt is not {} priceAt||market.PriceReceivedAt is not {} priceReceived||market.MarkAt is not {} markAt||market.MarkReceivedAt is not {} markReceived||execution.Book is null
            ||priceAt<=intent.CreatedAt||priceReceived<=intent.CreatedAt||markAt<=intent.CreatedAt||markReceived<=intent.CreatedAt||execution.Book.TransactionAt<=intent.CreatedAt||execution.Book.ReceivedAt<=intent.CreatedAt)
            return Capture(intent,control,evidence,at,revision,"SubsequentQuotes","Waiting","报价、标记价和盘口须严格晚于意向，尚未齐备");
        var frames=evidence.Frames.ToDictionary(f=>f.Symbol,f=>new QuantFrame(f.Symbol,f.Hour,f.Issue,null,f.Bars,[],[]),StringComparer.Ordinal);
        var d=original.Decision;var raw=QuantPortfolioEngine.Assess(d,market,context,evidence.Portfolio,frames,execution,at,control.Parameters,control.Costs);
        var chosen=QuantPortfolioEngine.Assess(d,market,context,evidence.Portfolio,frames,execution,at,control.Parameters,control.Costs,
            new(approved.Quantity,approved.Leverage,approved.EstimatedRisk,approved.Stop));
        var outcome=chosen.Status=="ShadowReady"&&chosen.Plan is not null?"Ready":chosen.Status=="AwaitData"?"Waiting":"Cancelled";
        var reason=outcome=="Ready"?"后续行情按原批准数量、杠杆、风险上限复核通过":chosen.Reason;
        var review=Capture(intent,control,evidence,at,revision,"Admission",outcome,reason,raw,chosen);
        var violated=review.Values.Where(v=>v.Passed==false).ToArray();
        if(outcome=="Ready"&&violated.Length>0)review=review with{Outcome="Cancelled",Reason=string.Join("；",violated.Select(v=>$"{v.Name}约束未通过，原{v.Approved:G10}、复核{v.Selected:G10}"))};
        return review;
    }
    public static QuantEntrySummary Summary(QuantEntryReview review)
    {
        var d=review.Intent.QuantIntent!.Decision;var selected=review.Selected?.Plan;var proposed=review.Proposed?.Plan;
        var capped=review.Phase=="Admission"&&selected is not null&&(proposed is null||selected.Quantity<proposed.Quantity||selected.Leverage<proposed.Leverage||selected.EstimatedRisk<proposed.EstimatedRisk);
        return new(review.Id,review.Version,review.At,review.Revision,d.Symbol,d.Direction,d.Id,review.Phase,review.Outcome,review.Reason,review.Values,capped,review.Selected?.Quote?.BookAt??review.Evidence.Execution?.Book?.TransactionAt);
    }
}
