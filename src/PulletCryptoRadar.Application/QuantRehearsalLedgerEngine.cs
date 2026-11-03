using PulletCryptoRadar.Core;
namespace PulletCryptoRadar.Application;

// A separate research ledger. It is deliberately not accepted by IPaperPersistence.
public static class QuantRehearsalLedgerEngine
{
    public const string Version="Quant-Migration-DryRun-1";
    public static QuantRehearsalLedger Fork(PaperState baseline,DateTimeOffset effectiveAt)
    {
        if(baseline.Cash<0||baseline.Revision<=0||effectiveAt<baseline.StartedAt)throw new InvalidDataException("账户副本起点无效");
        return new(Version,QuantRules.HashValue(baseline),baseline,effectiveAt,baseline.Cash,[],[],[],[]);
    }
    public static void Validate(QuantRehearsalLedger s)
    {
        if(s.Version!=Version||QuantRules.HashValue(s.Baseline)!=s.BaselineHash||s.UsedSignals.Distinct().Count()!=s.UsedSignals.Count||s.Positions.Select(p=>p.Id).Distinct().Count()!=s.Positions.Count||
            s.Fills.Select(f=>f.Key).Distinct().Count()!=s.Fills.Count||s.Funding.Select(f=>f.Key).Distinct().Count()!=s.Funding.Count||
            s.Positions.Any(p=>p.InitialQuantity<=0||p.Quantity<0||p.Quantity>p.InitialQuantity||p.InitialQuantity-p.Quantity!=p.Reductions.Sum(r=>r.Quantity)))throw new InvalidDataException("副本身份或数量不守恒");
        var expected=s.Baseline.Cash+s.Fills.Sum(f=>f.Gross-f.Fee)-s.Funding.Sum(f=>f.Evidence.SignedCost);
        if(Math.Abs(s.Cash+s.Positions.Sum(p=>p.Collateral)-expected)>.00000001m)throw new InvalidDataException("副本现金、抵押额与费用不守恒");
    }
    public static QuantRehearsalOperation Open(QuantRehearsalLedger state,QuantHypothesis d,QuantAssessment intent,QuantMarketInput market,
        QuantMarketContext context,QuantPortfolio portfolio,IReadOnlyDictionary<string,QuantFrame> frames,ExecutionPlanningInputs inputs,
        DateTimeOffset now,QuantParameters parameters,ExecutionCostModel costs)
    {
        Validate(state);QuantRehearsalOperation End(string status,string reason,QuantAssessment? a=null)=>new(status,reason,state,a,a?.Quote);
        if(state.UsedSignals.Contains(d.Id,StringComparer.Ordinal))return End("Duplicate","该冻结信号已记入副本，不重复成交");
        if(d.CapturedAt<state.EffectiveAt||d.Mode!="Forward"||!d.InputArchived||intent.Mode!="Forward"||intent.Status!="ShadowReady"||intent.Plan is not {} original||intent.DecisionId!=d.Id||intent.Symbol!=d.Symbol||intent.Direction!=d.Direction||intent.Strategy!=d.Strategy||intent.AsOf<d.CapturedAt||intent.AsOf>now)
            return End("Rejected","仅演练生效时间之后、有原始存档且已通过准入的前瞻意向");
        if(state.Cash<=0||state.Positions.Any(p=>p.Quantity>0&&p.Collateral<=0))return End("Rejected","副本现金不足或已出现抵押额缺口，不隐去负余额");
        var baselineReservations=state.Baseline.Positions.Select(p=>new QuantReservation(p.Symbol,p.Direction,StructuralPaperEngine.RemainingRisk(p,state.Baseline.Rules),StructuralPaperEngine.RemainingOriginalMargin(p),false))
            .Concat(state.Baseline.Pending.Select(p=>new QuantReservation(p.Symbol,p.StructuralEvidence?.Direction??"Long",p.StructuralEvidence?.Plan?.EstimatedRisk??state.Baseline.Rules.RiskPerTrade,p.StructuralEvidence?.Plan?.Margin??state.Baseline.Rules.MarginPerTrade,true)))
            .Concat(state.Positions.Where(p=>p.Quantity>0).Select(p=>new QuantReservation(p.Symbol,p.Direction,p.PlannedRisk*p.Quantity/p.InitialQuantity,p.Collateral,false))).ToArray();
        if(portfolio.Cash!=state.Cash||portfolio.Revision!=state.Baseline.Revision||QuantRules.HashValue(portfolio.Reservations.OrderBy(r=>r.Symbol,StringComparer.Ordinal).ToArray())!=QuantRules.HashValue(baselineReservations.OrderBy(r=>r.Symbol,StringComparer.Ordinal).ToArray()))
            return End("Rejected","执行前组合必须包含原仓、原意向及副本已占用风险");
        if(market.PriceAt<=intent.AsOf||market.PriceReceivedAt<=intent.AsOf||market.MarkAt<=intent.AsOf||market.MarkReceivedAt<=intent.AsOf||inputs.Book is null||inputs.Book.TransactionAt<=intent.AsOf||inputs.Book.ReceivedAt<=intent.AsOf)
            return End("AwaitData","等待严格晚于意向的下一次报价、标记价与盘口，不用生成计划时的盘口成交");
        var fresh=QuantPortfolioEngine.Assess(d,market,context,portfolio,frames,inputs,now,parameters,costs);
        if(fresh.Status!="ShadowReady"||fresh.Plan is not {} plan)return End(fresh.Status,fresh.Reason,fresh);
        if(plan.Quantity>original.Quantity||plan.Leverage>original.Leverage||plan.Stop!=original.Stop||plan.EstimatedRisk>original.EstimatedRisk+.00000001m)
            return End("Rejected","新报价使数量、杠杆、风险扩大或结构止损变化，需另立意向，不扩大原计划",fresh);
        var pendingCash=state.Baseline.Pending.Sum(p=>p.StructuralEvidence?.Plan is {} prior?prior.Margin+prior.ReferenceEntry*prior.Quantity*state.Baseline.Rules.FeeRate:state.Baseline.Rules.MarginPerTrade*(1+state.Baseline.Rules.Leverage*state.Baseline.Rules.FeeRate));
        if(plan.Margin+plan.Fee>state.Cash-pendingCash)return End("Rejected","扣除原未成交意向的保证金及开仓费预留后，可用现金不足",fresh);
        var digest=System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(state.BaselineHash+":"+d.Id));var id=new Guid(digest.AsSpan(0,16));
        var position=new QuantLedgerPosition(id,d.Id,d.Symbol,d.Direction,now,plan.Quantity,plan.Quantity,plan.Entry,plan.Leverage,plan.Stop,plan.Target1,plan.Target2,plan.EstimatedRisk,plan.Margin,plan.Fee,0,null,[]);
        var fill=new QuantLedgerFill("Entry:"+d.Id,id,"Entry",now,plan.Quantity,plan.Entry,0,plan.Fee,0,0,0,-plan.Fee,fresh.Quote!);
        var next=state with{Cash=state.Cash-plan.Margin-plan.Fee,Positions=state.Positions.Append(position).ToArray(),UsedSignals=state.UsedSignals.Append(d.Id).ToArray(),Fills=state.Fills.Append(fill).ToArray()};Validate(next);
        return new("CopiedFill","只在账户副本记账；盘口VWAP、残余滑点及假设手续费，不是原盘或真实成交",next,fresh,fresh.Quote);
    }
    // Explicit reduce-only primitive for migration tests; strategy-triggered live exits are not enabled here.
    public static QuantRehearsalOperation Reduce(QuantRehearsalLedger state,Guid positionId,string operationKey,decimal quantity,
        ExecutionPlanningInputs inputs,DateTimeOffset now,ExecutionCostModel costs)
    {
        Validate(state);QuantRehearsalOperation End(string status,string reason,ExecutionQuote? q=null)=>new(status,reason,state,null,q);
        if(string.IsNullOrWhiteSpace(operationKey)||operationKey.Length>160)return End("Rejected","减仓幂等键无效");
        var key="Reduce:"+operationKey;var previous=state.Fills.SingleOrDefault(f=>f.Key==key);
        if(previous is not null){if(previous.PositionId!=positionId||previous.Evidence.RequestedQuantity!=quantity)throw new InvalidDataException("减仓幂等键已用于不同请求");return End("Duplicate","该减仓操作已记账");}
        var p=state.Positions.SingleOrDefault(p=>p.Id==positionId);if(p is null||p.Quantity<=0||quantity<=0||quantity>p.Quantity)return End("Rejected","减仓持仓或数量无效");
        var latest=p.Reductions.Select(r=>r.At).Append(p.OpenedAt).Max();
        if(inputs.Book is null||inputs.Book.TransactionAt<=latest||inputs.Book.ReceivedAt<=latest||now<latest)return End("AwaitData","退出必须使用严格晚于开仓或上次减仓的盘口");
        var quote=ExecutionCostEngine.Quote(new(p.Symbol,p.Direction=="Long"?"Sell":"Buy",quantity,ReduceOnly:true),inputs.Rules,inputs.Book,now,costs);
        if(quote.Status!="Estimated"||quote.Quantity is not {} qty||qty<=0)return End(quote.Status,quote.Reason,quote);
        var fraction=qty/p.Quantity;var released=p.Collateral*fraction;var entryFee=p.EntryFeeRemaining*fraction;var funding=p.FundingRemaining*fraction;
        var gross=(p.Direction=="Long"?1m:-1m)*qty*(quote.EstimatedFill!.Value-p.Entry);var fee=quote.Fee!.Value;var remaining=p.Quantity-qty;
        var nextPosition=p with{Quantity=remaining,Collateral=p.Collateral-released,EntryFeeRemaining=p.EntryFeeRemaining-entryFee,FundingRemaining=p.FundingRemaining-funding,ClosedAt=remaining==0?now:null,Reductions=p.Reductions.Append(new(now,qty)).ToArray()};
        var fill=new QuantLedgerFill(key,p.Id,"Reduce",now,qty,quote.EstimatedFill.Value,gross,fee,released,entryFee,funding,gross-fee-entryFee-funding,quote);
        var next=state with{Cash=state.Cash+released+gross-fee,Positions=state.Positions.Select(x=>x.Id==p.Id?nextPosition:x).ToArray(),Fills=state.Fills.Append(fill).ToArray()};Validate(next);
        return new("CopiedReduction",remaining==0?"副本全量退出；亏损超过抵押额时保留负余额，不捏造兜底资金":"副本按市价数量步长减仓，余数继续保留",next,null,quote);
    }
    public static (QuantRehearsalLedger Ledger,FundingAuditResult Audit) Funding(QuantRehearsalLedger state,Guid positionId,FundingRateWindow? window,DateTimeOffset now)
    {
        Validate(state);var p=state.Positions.Single(p=>p.Id==positionId);var audit=ExecutionFundingEngine.Evaluate(new(p.Id,p.Symbol,p.Direction,p.OpenedAt,p.InitialQuantity,p.ClosedAt,p.Reductions),window,now,0);
        var cash=state.Cash;var collateral=p.Collateral;var outstanding=p.FundingRemaining;var events=state.Funding.ToList();
        foreach(var payment in audit.Payments)
        {
            var key=$"{p.Id:N}:{payment.FundingAt.ToUnixTimeMilliseconds()}:{payment.RateType}";var prior=events.SingleOrDefault(e=>e.Key==key);
            if(prior is not null)
            {
                if(prior.Evidence.Quantity!=payment.Quantity||prior.Evidence.Rate!=payment.Rate||prior.Evidence.MarkPrice!=payment.MarkPrice||prior.Evidence.SignedCost!=payment.SignedCost)throw new InvalidDataException("已记资金费事件冲突，禁止覆盖");
                continue;
            }
            // Reductions at the settlement timestamp happen after funding. A late observation allocates
            // the already-released portion to cash and the still-open portion to collateral, exactly once.
            var open=payment.SignedCost*p.Quantity/payment.Quantity;var closed=payment.SignedCost-open;
            collateral-=open;outstanding+=open;cash-=closed;events.Add(new(key,now,open,closed,payment));
        }
        var next=state with{Cash=cash,Funding=events,Positions=state.Positions.Select(x=>x.Id==p.Id?p with{Collateral=collateral,FundingRemaining=outstanding}:x).ToArray()};Validate(next);return(next,audit);
    }
}
