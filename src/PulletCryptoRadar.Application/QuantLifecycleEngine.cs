using PulletCryptoRadar.Core;
namespace PulletCryptoRadar.Application;

// Read-only reconciliation of the saved ledger; never submits exits or settles fees.
public static class QuantLifecycleEngine
{
    public const string Version="Quant-Lifecycle-1";
    public static QuantLifecycle Build(Guid id,PaperPosition? position,IReadOnlyList<PaperTrade> trades,IReadOnlyList<QuantFundingEvent> funding,
        QuantFundingCoverage? coverage,DateTimeOffset now)
    {
        var supplied=trades.Where(t=>t.PositionId==id&&t.Quant is not null&&t.ClosedAt<=now).ToArray();
        var legs=supplied.DistinctBy(t=>t.Id).OrderBy(t=>t.ClosedAt).ThenBy(t=>t.Id).ToArray();
        var meta=position?.Quant??legs.FirstOrDefault()?.Quant??throw new InvalidDataException("缺少新模型仓位原始计划");
        var intent=position?.Intent??legs[0].Evidence;var opened=position?.OpenedAt??legs[0].OpenedAt;
        var entry=position?.EntryPrice??legs[0].EntryPrice;var direction=position?.Direction??legs[0].Direction;
        var initial=meta.Plan.Quantity;var exited=legs.Sum(t=>t.Quantity);var remaining=position?.Quantity??0;
        var funds=funding.Where(f=>f.Evidence.PositionId==id&&f.BookedAt<=now).ToArray();
        var payments=funds.DistinctBy(f=>f.Key).ToArray();var late=payments.Sum(f=>f.ClosedAllocated);
        var observed=payments.Sum(f=>f.Evidence.SignedCost);var openFunding=position?.FundingCost??0;var settledFunding=legs.Sum(t=>t.FundingCost);
        var realized=legs.Sum(t=>t.NetPnl)-late;
        var checks=new List<QuantLifecycleCheck>();
        void Flag(string key,string name,bool valid,string reason)=>checks.Add(new(key,name,valid?"Pass":"Fail",null,null,reason));
        void Equal(string key,string name,decimal expected,decimal actual,string reason)
        {
            // Decimal allocation can round the final few digits across partial legs.
            var tolerance=.000000000000000001m*Math.Max(1,Math.Max(Math.Abs(expected),Math.Abs(actual)));
            checks.Add(new(key,name,Math.Abs(expected-actual)<=tolerance?"Pass":"Fail",expected,actual,reason));
        }
        void Unknown(string key,string name,string reason)=>checks.Add(new(key,name,"Unknown",null,null,reason));
        Flag("UniqueLedger","记录唯一且无冲突",supplied.GroupBy(t=>t.Id).All(g=>g.Select(QuantRules.HashValue).Distinct().Count()==1)
            &&funds.GroupBy(f=>f.Key).All(g=>g.Select(QuantRules.HashValue).Distinct().Count()==1),"相同ID的相同资料只计一次；不同资料不选有利版本");
        Flag("Identity","仓位与原计划一致",initial>0&&entry==meta.Plan.Entry&&(position is null||position.Id==id&&position.Quantity>0&&position.Quantity<=initial),"原始数量与入场来自保存计划，当前仓位ID及剩余数量须有效");
        Flag("LegIdentity","退出沿用同一入场",legs.All(t=>t.Version==QuantPaperEngine.Version&&t.OpenedAt==opened&&t.EntryPrice==entry&&t.Direction==direction
            &&t.Symbol==intent.Symbol&&t.AssetId==intent.AssetId&&t.Evidence.Key==intent.Key&&QuantRules.HashValue(t.Quant!.Plan)==QuantRules.HashValue(meta.Plan)&&t.Quantity>0&&t.ClosedAt>=opened)
            &&(position is null||position.OpenedAt<=now&&position.InitialQuantity==initial&&position.InitialMargin==meta.Plan.Margin),"同一仓位、入场、方向、原计划及正退出数量；不混入旧规则");
        Equal("Quantity","数量守恒",initial,exited+remaining,"原始数量 = 全部退出数量 + 当前剩余数量");
        Equal("Margin","原始保证金守恒",meta.Plan.Margin,legs.Sum(t=>t.Margin)+(position is null?0:StructuralPaperEngine.RemainingOriginalMargin(position)),"原始保证金按数量分摊；实际资金费单独核对");
        if(position is not null)Equal("OpenCollateral","剩余抵押额",StructuralPaperEngine.RemainingOriginalMargin(position)-openFunding,position.RemainingMargin,"当前抵押额 = 剩余原始保证金 − 分配给剩余仓位的资金费，负费为收取");
        var allQuotes=legs.All(t=>t.ExitQuote?.Fee is not null&&t.ExitQuote.EstimatedFill is not null&&t.ExitQuote.Quantity is not null);
        decimal? exitFees=allQuotes?legs.Sum(t=>t.ExitQuote!.Fee!.Value):null;
        if(allQuotes)
        {
            Equal("EntryFees","开仓手续费分摊",meta.Plan.Fee,legs.Sum(t=>t.Fees-t.ExitQuote!.Fee!.Value)+(position?.OpenFee??0),"原开仓费 = 已退出分摊 + 剩余开仓费，不能重复扣费");
            Flag("ExitCosts","退出费用与成交资料一致",legs.All(t=>t.ExitQuote!.Status=="Estimated"&&t.ExitQuote.Quantity==t.Quantity&&t.ExitQuote.EstimatedFill==t.ExitPrice
                &&t.ExitQuote.Fee>=0&&t.Fees>=t.ExitQuote.Fee),"保存的退出数量、价格和费用与后续盘口成本结果一致");
        }
        else Unknown("EntryFees","开仓手续费分摊","有退出腿缺少完整盘口费用，不能用当前费率追补历史");
        Equal("Funding","资金费入账守恒",observed,settledFunding+openFunding+late,"来源资金费 = 已结算分摊 + 剩余仓位分摊 + 迟到已平数量调整，正付负收");
        Flag("FundingAllocation","资金费分配一致",payments.All(f=>f.Evidence.FundingAt>=opened&&f.Evidence.FundingAt<=now
            &&f.Evidence.Symbol==intent.Symbol&&f.Evidence.Direction==direction&&f.BookedAt>=f.Evidence.FundingAt
            &&Math.Abs(f.Evidence.SignedCost-f.OpenAllocated-f.ClosedAllocated)<=.000000000000000001m*Math.Max(1,Math.Abs(f.Evidence.SignedCost))),"每笔来源费用等于入账时未平/已平分配；同币同方向，不会因分批重复计费");
        Equal("Net","结算净额守恒",legs.Sum(t=>t.GrossPnl-t.Fees-t.FundingCost)-late,realized,"结算毛额 − 已分摊手续费 − 已分摊资金费 − 迟到调整；不扣未来退出费");
        Flag("Gross","逐腿毛额与方向一致",legs.All(t=>Math.Abs(t.GrossPnl-StructuralPaperEngine.Side(t.Direction)*t.Quantity*(t.ExitPrice-t.EntryPrice))<=.000000000000000001m*Math.Max(1,Math.Abs(t.GrossPnl))),"按当时实际数量、入场、退出价和持仓方向核对");
        Flag("LegNet","逐腿净额守恒",legs.All(t=>Math.Abs(t.NetPnl-(t.GrossPnl-t.Fees-t.FundingCost))<=.000000000000000001m*Math.Max(1,Math.Abs(t.NetPnl))),"逐腿核对，避免多腿误差相互抵消");
        var afters=legs.ToDictionary(t=>t.Id,t=>new[]{opened,t.Quant!.ExitAfter??opened,t.Quant.LastFillAt??opened}.Max());
        if(legs.Any(t=>t.ExitQuote?.Book is null||t.Quant!.ExitAfter is null))Unknown("SubsequentBooks","后续退出盘口","有退出腿缺少当时盘口或触发时间，不能用目标价冒充成交或补证");
        else Flag("SubsequentBooks","后续退出盘口",legs.All(t=>t.ExitQuote!.Book!.TransactionAt>afters[t.Id]&&t.ExitQuote.Book.ReceivedAt>afters[t.Id]
            &&t.ExitQuote.Book.TransactionAt<=t.ClosedAt&&t.ExitQuote.Book.ReceivedAt<=t.ClosedAt),"盘口来源和接收均严格晚于触发/上次成交，且不晚于结算");
        var final=legs.Where(t=>t.FinalExit).ToArray();var closed=position is null&&final.Length==1?(DateTimeOffset?)final[0].ClosedAt:null;
        Flag("FinalExit","最终退出标记",position is not null?final.Length==0:closed is not null&&final[0].ClosedAt==legs.Last().ClosedAt,"仍持仓不能有最终退出；已平仓须且仅须最后一腿标记最终退出");
        Flag("CoverageIdentity","资金费范围归属",coverage is null||coverage.PositionId==id&&coverage.Symbol==intent.Symbol,"只使用本笔仓位和同一合约的来源范围");
        if(coverage?.Status=="Conflict")checks.Add(new("FundingCoverage","资金费完整范围","Fail",null,null,"资金费来源冲突，不能确认最终净额"));
        else if(coverage is null||coverage.CheckedAt>now||coverage.KnownThrough is null||coverage.KnownThrough>now||coverage.Status!="ObservedWindow"||closed is {} close&&coverage.KnownThrough<close)
            Unknown("FundingCoverage","资金费完整范围","范围缺失、尚未补齐或超出当前时点；最近费用不能按零确认");
        else checks.Add(new("FundingCoverage","资金费完整范围","Pass",null,null,position is null?"来源观察范围已覆盖最终退出；仍不证明供应方绝无遗漏":"当前观察范围已核对；仍持仓，最近10分钟费用暂记"));
        if(legs.Length==0)
            for(var i=0;i<checks.Count;i++)
                if(checks[i].Key is "LegIdentity" or "ExitCosts" or "Gross" or "LegNet" or "SubsequentBooks")
                    checks[i]=checks[i] with{Status="NotApplicable",Reason="尚无退出腿，此项没有现场样本，不作为退出验证通过"};
        var status=checks.Any(c=>c.Status=="Fail")?"Inconsistent":position is not null?"Open":checks.Any(c=>c.Status=="Unknown"&&c.Key!="FundingCoverage")?"Incomplete":checks.Any(c=>c.Status=="Unknown")?"AwaitFunding":"Complete";
        var result=new QuantLifecycle(Version,now,status,initial,exited,remaining,meta.Plan.Margin,meta.Plan.Fee,exitFees,settledFunding,openFunding,late,observed,realized,status=="Complete"?realized:null,closed,
            coverage?.Status??"Unknown",coverage?.KnownThrough,position?.StopPrice,opened.AddHours(meta.Plan.MaximumHoldHours),position?.Quant?.PendingExit,position?.Quant?.ExitAfter,checks,
            legs.Select(t=>new QuantLifecycleLeg(t.Id,t.ClosedAt,t.Reason,t.Quantity,t.ExitPrice,t.GrossPnl,t.ExitQuote?.Fee is {} fee?t.Fees-fee:null,t.ExitQuote?.Fee,t.FundingCost,t.NetPnl,t.FinalExit,afters[t.Id],t.ExitQuote?.Book?.TransactionAt,t.ExitQuote?.Book?.ReceivedAt){Timing=QuantExitTimingEngine.Build(t)}).ToArray());
        return result with{Attribution=QuantExitAttributionEngine.Build(meta,direction,legs,result)};
    }
}
