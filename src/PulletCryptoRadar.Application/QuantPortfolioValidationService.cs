using PulletCryptoRadar.Core;
namespace PulletCryptoRadar.Application;

public sealed class QuantPortfolioValidationService(PaperService paper,IQuantExecutionCoordinator coordinator,IPaperRepository ledger,
    IQuantShadowRepository signals,IQuantPortfolioValidationRepository repository)
{
    private readonly SemaphoreSlim gate=new(1,1);
    public QuantPortfolioValidationReport? Status()=>repository.Latest();
    public QuantPortfolioValidationRun? Read(Guid id)=>repository.Read(id);
    public async Task<QuantPortfolioValidationReport> RunAsync(DateOnly? date,CancellationToken ct)
    {
        if(!await gate.WaitAsync(0,ct))throw new InvalidOperationException("组合核验正在生成");
        try
        {
            var source=await paper.ReviewContextAsync(ct);var control=source.State.QuantControl??throw new InvalidOperationException("新执行尚未启用");
            var market=await coordinator.CaptureAsync(source.State,source.Trades,ct);var today=PaperEngine.Day(market.At);var selected=date??today;
            if(selected>today||selected<today.AddDays(-60))throw new ArgumentException("请选择最近60天内且不晚于今天的上海日期");
            var from=QuantDailyEngine.Start(selected);var through=from.AddDays(1);if(through>source.State.UpdatedAt)through=source.State.UpdatedAt;
            var refs=new List<QuantPortfolioSourceRef>();
            foreach(var id in ledger.EntryReviewIds(from,through))
            {
                ct.ThrowIfCancellationRequested();var record=ledger.EntryReview(id)??throw new InvalidDataException("复核索引与原始记录不一致");
                if(record.At<from||record.At>=from.AddDays(1)||record.At>source.State.UpdatedAt||record.Revision>source.State.Revision||QuantRules.HashValue(record.Control)!=QuantRules.HashValue(control))continue;
                var d=record.Intent.QuantIntent!.Decision;var original=signals.ReadInput(control.RulesHash,d.InputHash);
                var frozen=new QuantPortfolioSource(record,original);refs.Add(new(record.Id,repository.SaveSource(frozen)));
            }
            var input=new QuantPortfolioFrozenInput(Guid.NewGuid(),QuantPortfolioValidationEngine.Version,selected.ToString("yyyy-MM-dd"),market.At,source.State,source.Trades,source.Funding,QuantPortfolioValidationEngine.Freeze(market),refs);
            var run=QuantPortfolioValidationEngine.Run(input,repository.ReadSource);ct.ThrowIfCancellationRequested();repository.Save(run);return run.Report;
        }
        finally{gate.Release();}
    }
}
