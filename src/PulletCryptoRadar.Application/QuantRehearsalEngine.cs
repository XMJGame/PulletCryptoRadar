using PulletCryptoRadar.Core;
namespace PulletCryptoRadar.Application;
public static class QuantRehearsalEngine
{
    public static QuantRehearsalRun Run(QuantRehearsalInput input)
    {
        var now=input.CapturedAt;var baseline=input.Paper.State;var parameters=input.Cohort?.Parameters??new();
        if(baseline.UpdatedAt>now||input.Cohort?.StartedAt>now||input.Shadow?.AsOf>now||input.Cohort is {} c&&c.RulesHash!=QuantRules.Hash(parameters,input.Costs)||input.Shadow is {} s&&(input.Cohort is null||s.RulesHash!=input.Cohort.RulesHash))throw new InvalidDataException("副本时间边界或策略/成本版本不一致");
        var ledger=QuantRehearsalLedgerEngine.Fork(baseline,input.Cohort?.StartedAt??now);var outcomes=new List<QuantRehearsalOutcome>();
        var frames=input.Hourly.ToDictionary(p=>p.Key,p=>QuantSignalEngine.Frame(p.Key,p.Value,now,parameters),StringComparer.Ordinal);
        var markets=input.Markets.ToDictionary(m=>m.Instrument.Symbol,StringComparer.Ordinal);var shadow=input.Shadow;
        var candidates=shadow?.Decisions.Where(d=>d.Status=="PriceCandidate").ToArray()??[];
        var intents=shadow?.Assessments.Where(a=>a.Mode=="Forward"&&a.Status=="ShadowReady").OrderBy(a=>a.AsOf).ThenBy(a=>a.DecisionId,StringComparer.Ordinal).ToArray()??[];
        foreach(var intent in intents)
        {
            var decision=candidates.SingleOrDefault(d=>d.Id==intent.DecisionId);
            if(decision is null||!markets.TryGetValue(intent.Symbol,out var market)||!input.Execution.TryGetValue(intent.Symbol,out var evidence)||intent.Context is null)
            {outcomes.Add(new(intent.DecisionId,intent.Symbol,intent.Strategy,intent.Direction,"AwaitData","当前副本缺少对应冻结判断、报价、环境或执行资料",null));continue;}
            if(!input.ArchivedSignals.TryGetValue(decision.InputHash,out var bundle)||bundle.Symbol!=decision.Symbol||bundle.Hour!=decision.Hour||QuantRules.HashValue(bundle)!=decision.InputHash)
            {outcomes.Add(new(intent.DecisionId,intent.Symbol,intent.Strategy,intent.Direction,"AwaitData","缺少同一信号的内容寻址原始档案，未仅凭通过标签成交",null));continue;}
            var originalFrame=QuantSignalEngine.Frame(decision.Symbol,bundle.Candles,decision.CapturedAt,parameters);
            var originalOi=QuantSignalEngine.Oi(decision.Symbol,bundle.Oi,decision.Hour,decision.CapturedAt,parameters);
            var reproduced=QuantSignalEngine.Analyze(decision.Market,originalFrame,originalOi,decision.CapturedAt,"Forward",decision.RulesHash,decision.InputHash,parameters).Single(d=>d.Strategy==decision.Strategy&&d.Direction==decision.Direction) with{InputArchived=true};
            if(QuantRules.HashValue(reproduced)!=QuantRules.HashValue(decision))
            {outcomes.Add(new(intent.DecisionId,intent.Symbol,intent.Strategy,intent.Direction,"Rejected","原始信号复算不一致，未生成副本成交",null));continue;}
            var portfolio=Portfolio(input,ledger,markets);
            var result=QuantRehearsalLedgerEngine.Open(ledger,decision,intent,QuantRules.Input(market),intent.Context,portfolio,frames,evidence,now,parameters,input.Costs);
            ledger=result.Ledger;outcomes.Add(new(decision.Id,decision.Symbol,decision.Strategy,decision.Direction,result.Status,result.Reason,result.Assessment?.Plan));
        }
        var duplicateSafe=true;
        foreach(var intent in intents.Where(i=>ledger.UsedSignals.Contains(i.DecisionId,StringComparer.Ordinal)))
        {
            var d=candidates.Single(d=>d.Id==intent.DecisionId);var result=QuantRehearsalLedgerEngine.Open(ledger,d,intent,QuantRules.Input(markets[d.Symbol]),intent.Context!,Portfolio(input,ledger,markets),frames,input.Execution[d.Symbol],now,parameters,input.Costs);
            duplicateSafe&=result.Status=="Duplicate"&&QuantRules.HashValue(result.Ledger)==QuantRules.HashValue(ledger);
        }
        var audits=new List<FundingAuditResult>();var exits=new List<ExecutionQuote>();
        foreach(var position in baseline.Positions)
        {
            var reductions=input.LegacyTrades.Where(t=>t.PositionId==position.Id).Select(t=>new ExposureReduction(t.ClosedAt,t.Quantity)).ToArray();
            audits.Add(ExecutionFundingEngine.Evaluate(new(position.Id,position.Symbol,position.Direction,position.OpenedAt,position.InitialQuantity,null,reductions),input.Funding.GetValueOrDefault(position.Symbol),now,position.FundingCost+input.LegacyTrades.Where(t=>t.PositionId==position.Id).Sum(t=>t.FundingCost)));
            var evidence=input.Execution.GetValueOrDefault(position.Symbol);exits.Add(ExecutionCostEngine.Quote(new(position.Symbol,position.Direction=="Short"?"Buy":"Sell",position.Quantity,ReduceOnly:true),evidence?.Rules,evidence?.Book,now,input.Costs));
        }
        QuantRehearsalLedgerEngine.Validate(ledger);var immutable=QuantRules.HashValue(ledger.Baseline)==ledger.BaselineHash;
        var checks=new QuantStudyGate[]{
            new("BaselinePreserved",immutable?"Pass":"Fail","冻结原现金、持仓、意向和旧规则；不调用IPaperPersistence"),
            new("CopyConservation","Pass","副本现金＋抵押额＝原现金＋已实现毛额－手续费－实际已观察资金费；亏损缺口不截成零"),
            new("EntryReplay",!duplicateSafe?"Fail":ledger.UsedSignals.Count>0?"Pass":"Unknown",intents.Length==0?"当前没有已通过准入的有效前瞻意向，未虚构成交；本次没有现场新仓重放样本":$"前瞻意向{intents.Length}项，副本入场{ledger.UsedSignals.Count}笔；重复操作检查{(duplicateSafe?"一致":"失败")}；零成交不视为现场入场通过"),
            new("ActualFunding",audits.Count==0?"Unknown":audits.All(a=>a.Status=="ObservedWindow")?"Pass":"Unknown","核对原仓实际来源，不改旧账；Partial/Unknown不填零，也不认定供应方绝无遗漏"),
            new("LiveMigration","Unknown","当前仅副本演练；新执行原语尚未接入原账户连续入场/止盈/止损调度")};
        var report=new QuantRehearsalReport(input.Id,QuantRehearsalLedgerEngine.Version,now,QuantRules.HashValue(input),baseline.Revision,baseline.Rules.Version,baseline.Cash,ledger.Cash,baseline.Positions.Count,baseline.Pending.Count,candidates.Length,intents.Length,ledger.UsedSignals.Count,outcomes,audits,exits,checks,
            ["这是冻结账户副本与本地证据核验；不创建、平掉或重置原模拟仓，不发送真实委托。",
            "只接受原始输入已存档的Forward判断和已通过准入的意向；当前预览、历史价格回放、失效意向不成交。成交检查另需严格晚于意向的报价、标记价和盘口，并重新核对组合风险。",
            "盘口VWAP及残余滑点是模拟假设；手续费仍为每边0.05%配置假设。未接入账户费率、真实撮合或保证金档位，不能声称不会强平或穿仓。",
            "实际资金费按结算标记价、方向和当时数量计算。副本支持迟到资金费按已减/未减数量分配及幂等，现场报告不以新规则重结算原仓。",
            "原持仓及原意向保留旧版本并占用五仓、保证金和风险额度；副本新入场不得扩大原意向数量、杠杆和风险，也不移动结构止损。",
            "新开仓、reduce-only减仓与资金费原语已独立测试；原账户混合版本持仓的自动退出调度、断线补证与原子提交迁移仍待下一步接线演练。",
            "每次生成冻结一个副本；报告不是持续运行的第二个模拟账户，不将关闭后缺失数据补成当时可用资料。"]);
        return new(input,report,ledger);
    }
    private static QuantPortfolio Portfolio(QuantRehearsalInput input,QuantRehearsalLedger ledger,IReadOnlyDictionary<string,MarketView> markets)
    {
        var original=QuantPortfolioEngine.Portfolio(input.Paper,input.Paper.State.UpdatedAt);decimal? equity=input.Paper.CompleteEquity;
        if(equity is not null)
        {
            equity+=ledger.Fills.Sum(f=>f.Gross-f.Fee)-ledger.Funding.Sum(f=>f.Evidence.SignedCost);
            foreach(var p in ledger.Positions.Where(p=>p.Quantity>0))
            {if(!markets.TryGetValue(p.Symbol,out var m)||!StructuralPaperEngine.Fresh(m,input.CapturedAt)){equity=null;break;}equity+=(p.Direction=="Long"?1:-1)*p.Quantity*(m.MarkPrice!.Value-p.Entry);}
        }
        return original with{Cash=ledger.Cash,Equity=equity,Reservations=original.Reservations.Concat(ledger.Positions.Where(p=>p.Quantity>0).Select(p=>new QuantReservation(p.Symbol,p.Direction,p.PlannedRisk*p.Quantity/p.InitialQuantity,p.Collateral,false))).ToArray()};
    }
}
