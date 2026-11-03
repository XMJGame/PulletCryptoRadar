using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Application;

public sealed record StrategyLaneReview(string Key,string Name,string Stage,int OpenPositions,int ClosedPositions,int SettlementLegs,
    decimal RealizedNet,decimal ClosedFees,decimal ClosedFunding,decimal? OpenGrossPnl,decimal? AverageClosedNet,decimal? ClosedWinRate);
public sealed record StrategyReviewSnapshot(DateTimeOffset AsOf,IReadOnlyList<StrategyLaneReview> Lanes,int EarlyResearchObjects,int VerifiedSmallCaps,
    IReadOnlyDictionary<string,int> ResearchBlockers,string Accounting);
public static class StrategyReview
{
    private static string Lane(string version)=>version==QuantPaperEngine.Version?"Quant":version==StructuralPaperEngine.Version?"Trend":version=="Paper-Tenfold-Experiment-1"?"Tenfold":"Legacy";
    public static StrategyReviewSnapshot Build(PaperState state,IReadOnlyList<PaperTrade> trades,IReadOnlyDictionary<int,MarketView> markets,OpportunitySnapshot? opportunities,DateTimeOffset now,IReadOnlyList<QuantFundingEvent>? funding=null)
    {
        var lanes=new List<StrategyLaneReview>();
        foreach(var key in new[]{"Quant","Trend","Tenfold","Legacy"})
        {
            var legs=trades.Where(t=>Lane(t.Version)==key).ToArray();
            var positions=state.Positions.Where(p=>Lane(p.Quant is not null?QuantPaperEngine.Version:p.Intent.StructuralEvidence is null?"Legacy":state.Rules.Version)==key).ToArray();
            if(key=="Quant"&&state.QuantControl is null&&legs.Length==0&&positions.Length==0)continue;
            if(key=="Legacy"&&legs.Length==0&&positions.Length==0)continue;
            var closed=legs.GroupBy(t=>t.PositionId??t.Id).Where(g=>g.Any(t=>t.FinalExit)).ToArray();
            var complete=positions.All(p=>StructuralPaperEngine.Fresh(markets.GetValueOrDefault(p.AssetId),now));
            decimal? open=complete?positions.Sum(p=>StructuralPaperEngine.Gross(p,markets[p.AssetId].MarkPrice!.Value)):null;
            var late=(funding??[]).Where(e=>legs.Any(t=>t.PositionId==e.Evidence.PositionId)).ToArray();decimal Net(IEnumerable<PaperTrade> rows)=>rows.Sum(t=>t.NetPnl)-late.Where(e=>rows.Any(t=>t.PositionId==e.Evidence.PositionId)).Sum(e=>e.ClosedAllocated);
            lanes.Add(new(key,key=="Quant"?"新版量化模拟实验":key=="Trend"?"结构趋势实验":key=="Tenfold"?"30日十倍妖币研究":"旧版/未分类记录",
                key=="Quant"?"独立执行版本、实际资金费；净额为已观察成本暂记，研究未验证":key=="Tenfold"?"取证与前瞻观察；尚无独立执行规则，不借用趋势仓收益":key=="Trend"?state.QuantControl is not null?"原仓原规则管理，已不再用此版本开新仓":state.Enabled?"实验新仓启用；研究未验证":"新仓暂停；已有仓位继续风控":"历史版本保留",
                positions.Length,closed.Length,legs.Length,Net(legs),legs.Sum(t=>t.Fees),legs.Sum(t=>t.FundingCost)+late.Sum(e=>e.ClosedAllocated),open,
                closed.Length>0?closed.Sum(g=>Net(g))/closed.Length:null,
                closed.Length>0?100m*closed.Count(g=>Net(g)>0)/closed.Length:null));
        }
        var early=opportunities?.Version==OpportunityEngine.Version&&opportunities.AsOf<=now&&now-opportunities.AsOf<=TimeSpan.FromMinutes(3)
            ?opportunities.Rows.Where(r=>r.Tracks.Contains("Early")).ToArray():[];
        return new(now,lanes,early.Length,early.Count(r=>r.Tenfold?.VerifiedSmallCap==true),
            early.GroupBy(r=>r.Tenfold?.Status??"研究证据待刷新").ToDictionary(g=>g.Key,g=>g.Count()),
            "共享原1000 USDT模拟账本，不新增本金。分组统计不是独立账户收益率；新旧模型按执行版本分组，迟到资金费调整计入所属版本。开放仓仅报新鲜毛浮盈，费用/资金费与权益以总账为准。十倍研究对象不等于已买入。旧版不追认成十倍交易。");
    }
}
