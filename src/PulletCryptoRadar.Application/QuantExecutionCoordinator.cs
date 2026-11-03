using PulletCryptoRadar.Core;
namespace PulletCryptoRadar.Application;
public interface IQuantExecutionFeed
{QuantSnapshot? Latest {get;} void Publish(QuantSnapshot snapshot);}
public sealed class QuantExecutionFeed:IQuantExecutionFeed
{private QuantSnapshot? latest;public QuantSnapshot? Latest=>Volatile.Read(ref latest);public void Publish(QuantSnapshot snapshot)=>Volatile.Write(ref latest,snapshot);}
public sealed record QuantPaperInput(DateTimeOffset At,IReadOnlyList<MarketView> Markets,QuantSnapshot? Snapshot,
    IReadOnlyDictionary<string,QuantFrame> Frames,IReadOnlyDictionary<string,ExecutionPlanningInputs> Execution,
    IReadOnlyDictionary<string,FundingRateWindow?> Funding,IReadOnlyDictionary<string,QuantInputBundle> Archived)
{public IReadOnlyList<string> Issues {get;init;}=[];}
public interface IQuantExecutionCoordinator
{Task<QuantPaperInput> CaptureAsync(PaperState state,IReadOnlyList<PaperTrade> trades,CancellationToken ct);}
public sealed class QuantExecutionCoordinator(IMarketRuntimeStore market,IAssetRepository history,IQuantExecutionFeed feed,
    ExecutionEvidenceService execution,IExecutionEvidenceRepository funding,IQuantShadowRepository signals,TimeProvider time):IQuantExecutionCoordinator
{
    public async Task<QuantPaperInput> CaptureAsync(PaperState state,IReadOnlyList<PaperTrade> trades,CancellationToken ct)
    {
        var snapshot=feed.Latest;var parameters=state.QuantControl!.Parameters;
        var names=state.Positions.Select(p=>p.Symbol).Concat(state.Pending.Select(p=>p.Symbol)).Concat(snapshot?.Decisions.Where(d=>d.Mode=="Forward"&&d.Status=="PriceCandidate").Select(d=>d.Symbol)??[])
            .Concat(trades.Where(t=>t.Quant is not null&&t.FinalExit).OrderByDescending(t=>t.ClosedAt).Take(20).Select(t=>t.Symbol)).Distinct(StringComparer.Ordinal).ToArray();
        var all=market.GetAll();var frames=new Dictionary<string,QuantFrame>(StringComparer.Ordinal);var issues=new List<string>();
        foreach(var m in all.Where(m=>names.Contains(m.Instrument.Symbol,StringComparer.Ordinal)))
        {
            try{var rows=await history.ReadHourlyHistoryAsync(m.Instrument.AssetId,parameters.MinimumHours+4,ct);var now=time.GetUtcNow();frames[m.Instrument.Symbol]=QuantSignalEngine.Frame(m.Instrument.Symbol,QuantSignalEngine.MergeHourly(m.Instrument.Symbol,rows,market.GetCandles(m.Instrument.AssetId,5),now),now,parameters);}
            catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
            catch(Exception ex){var issue=m.Instrument.Symbol+"小时缓存读取失败："+ex.GetType().Name;issues.Add(issue);frames[m.Instrument.Symbol]=QuantSignalEngine.Frame(m.Instrument.Symbol,[],time.GetUtcNow(),parameters) with{Issue=issue};}
        }
        var archived=new Dictionary<string,QuantInputBundle>(StringComparer.Ordinal);
        foreach(var d in (snapshot?.Decisions??[]).Where(d=>d.Mode=="Forward"&&d.InputArchived&&d.Status=="PriceCandidate").Concat(state.Pending.Where(i=>i.QuantIntent is not null).Select(i=>i.QuantIntent!.Decision)))
        {
            try{if(!archived.ContainsKey(d.InputHash)&&signals.ReadInput(d.RulesHash,d.InputHash) is {} bundle)archived[d.InputHash]=bundle;}
            catch(Exception ex)when(ex is IOException or System.Text.Json.JsonException){issues.Add(d.Symbol+"原始信号档案读取失败："+ex.GetType().Name);}
        }
        ExecutionPlanningInputs Planning(string name){try{return execution.PlanningInputs(name);}catch(Exception ex)when(ex is IOException or System.Text.Json.JsonException){issues.Add(name+"执行资料读取失败："+ex.GetType().Name);return new(null,null,null);}}
        FundingRateWindow? Funding(string name){try{return funding.Funding(name);}catch(Exception ex)when(ex is IOException or System.Text.Json.JsonException){issues.Add(name+"资金费资料读取失败："+ex.GetType().Name);return null;}}
        return new(time.GetUtcNow(),market.GetAll(),snapshot,frames,names.ToDictionary(n=>n,Planning,StringComparer.Ordinal),names.ToDictionary(n=>n,Funding,StringComparer.Ordinal),archived){Issues=issues};
    }
}
