using PulletCryptoRadar.Core;
namespace PulletCryptoRadar.Application;
public sealed class QuantRehearsalService(PaperService paper,QuantShadowService shadow,IMarketRuntimeStore market,IAssetRepository history,
    ExecutionEvidenceService execution,IExecutionEvidenceRepository evidence,IQuantRehearsalRepository repository,IQuantShadowRepository signals,TimeProvider time)
{
    private readonly SemaphoreSlim gate=new(1,1);
    public QuantRehearsalReport? Status()=>repository.Latest()?.Report;
    public async Task<QuantRehearsalReport> RunAsync(CancellationToken ct)
    {
        if(!await gate.WaitAsync(0,ct))throw new InvalidOperationException("副本演练正在生成");
        try
        {
            PaperOverview? overview=null;IReadOnlyList<PaperTrade> trades=[];
            for(var i=0;i<3;i++){var context=await paper.ExecutionContextAsync(ct);var current=await paper.OverviewAsync(ct);if(context.State.Revision!=current.State.Revision)continue;overview=current;trades=context.Trades;break;}
            if(overview is null)throw new InvalidOperationException("账本正在变化，本次未冻结不一致副本，请稍后重试");
            if(overview.State.QuantControl is not null)throw new InvalidOperationException("新执行已启用；旧版副本报告保留为历史证据，不再生成旧模型副本");
            var live=shadow.Status();var names=overview.State.Positions.Select(p=>p.Symbol).Concat(live.Latest?.Decisions.Where(d=>d.Status=="PriceCandidate").Select(d=>d.Symbol)??[]).Concat(new[]{"BTCUSDT","ETHUSDT"}).Distinct(StringComparer.Ordinal).Take(67).ToHashSet(StringComparer.Ordinal);
            var markets=market.GetAll().Where(m=>names.Contains(m.Instrument.Symbol)).ToArray();var hourly=new Dictionary<string,IReadOnlyList<Candle>>(StringComparer.Ordinal);
            foreach(var m in markets){var saved=await history.ReadHourlyHistoryAsync(m.Instrument.AssetId,(live.Cohort?.Parameters.MinimumHours??337)+4,ct);hourly[m.Instrument.Symbol]=QuantSignalEngine.MergeHourly(m.Instrument.Symbol,saved,market.GetCandles(m.Instrument.AssetId,5),time.GetUtcNow());}
            var archived=new Dictionary<string,QuantInputBundle>(StringComparer.Ordinal);
            foreach(var d in live.Latest?.Decisions.Where(d=>d.Status=="PriceCandidate"&&d.Mode=="Forward"&&d.InputArchived)??[])
            {if(!archived.ContainsKey(d.InputHash)&&signals.ReadInput(d.RulesHash,d.InputHash) is {} bundle)archived[d.InputHash]=bundle;}
            var captured=time.GetUtcNow();var input=new QuantRehearsalInput(Guid.NewGuid(),captured,overview,trades,live.Cohort,live.Latest,markets,hourly,names.ToDictionary(s=>s,execution.PlanningInputs,StringComparer.Ordinal),names.ToDictionary(s=>s,evidence.Funding,StringComparer.Ordinal),execution.Model){ArchivedSignals=archived};
            var run=QuantRehearsalEngine.Run(input);var replay=QuantRehearsalEngine.Run(input);
            if(QuantRules.HashValue(run)!=QuantRules.HashValue(replay))throw new InvalidDataException("相同冻结副本重放不一致，未保存报告");
            repository.Save(run);return run.Report;
        }
        finally{gate.Release();}
    }
}
