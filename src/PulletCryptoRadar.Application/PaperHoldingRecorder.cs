using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Application;

public static class PaperHoldingRecorder
{
    public static PaperBatch Attach(PaperState before,PaperBatch batch,IReadOnlyDictionary<int,MarketView> markets)
    {
        var state=batch.State;var now=state.UpdatedAt;var previous=before.Positions.ToDictionary(p=>p.Id);
        var current=state.Positions.ToDictionary(p=>p.Id);
        var action=batch.Trades.Count>0||current.Keys.Any(id=>!previous.ContainsKey(id));
        if(!action&&before.LastHoldingSampleAt is {} last&&now-last<TimeSpan.FromMinutes(1))return batch;
        if(previous.Count==0&&current.Count==0)return batch;
        var positions=previous.Values.Concat(current.Values).GroupBy(p=>p.Id).Select(g=>g.Last()).ToArray();
        bool Fresh(MarketView? m)=>state.Rules.ExperimentalSimulation?StructuralPaperEngine.Fresh(m,now):PaperEngine.Fresh(m,now);
        var complete=state.Positions.All(p=>Fresh(markets.GetValueOrDefault(p.AssetId)))&&(state.QuantFunding??[]).All(c=>c.Status=="ObservedWindow");
        var equity=state.Cash+state.Positions.Sum(p=>QuantPaperEngine.MarkValue(p,p.LastMark));
        var snapshots=positions.Select(p=>
        {
            var input=markets.GetValueOrDefault(p.AssetId);var fresh=Fresh(input);
            var executions=batch.Trades.Where(t=>(t.PositionId??t.Id)==p.Id).ToArray();
            var kind=!current.ContainsKey(p.Id)?"退出上下文（数量为退出前）":!previous.ContainsKey(p.Id)?"开仓后":executions.Length>0?"分批减仓后":"持仓定期观察";
            return new PaperHoldingSnapshot(p.Id,now,state.Revision,p.Quant is not null?QuantPaperEngine.Version:state.Rules.Version,kind,p,input,fresh,
                fresh?StructuralPaperEngine.Gross(p,input!.MarkPrice!.Value):null,complete?equity:null,executions);
        }).ToArray();
        return batch with{State=state with{LastHoldingSampleAt=now},HoldingSnapshots=snapshots};
    }
}
