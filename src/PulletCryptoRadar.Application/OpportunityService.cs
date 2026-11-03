using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Application;

public sealed class OpportunityService(IMarketRuntimeStore market,IDerivativeRuntimeStore derivatives,IAssetRepository history,
    IOpportunityRepository repository,NetworkOptions network,TimeProvider time)
{
    private OpportunitySnapshot? latest;
    private DateTimeOffset? lastSaved;
    private string? lastSavedHash;
    private readonly SemaphoreSlim gate=new(1,1);
    public OpportunitySnapshot? Latest()=>Volatile.Read(ref latest)??(repository.Latest() is {} saved&&saved.Version==OpportunityEngine.Version?saved:null);
    public IReadOnlyList<OpportunitySnapshot> History(int limit)=>repository.History(limit);
    public async Task RefreshAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var now=time.GetUtcNow();var markets=market.GetAll();if(markets.Count==0)return;
            var selected=OpportunityEngine.Select(markets,now,network.HistorySymbols);
            var yesterday=repository.AtOrBefore(now.AddDays(-1));
            var rows=new List<OpportunityRow>();
            foreach(var (m,tracks) in selected)
            {
                var saved=await history.ReadHourlyHistoryAsync(m.Instrument.AssetId,800,ct);
                var candles=ForwardEngine.Hourly(saved,market.GetCandles(m.Instrument.AssetId,5),now);
                var row=OpportunityEngine.Evaluate(m,tracks,candles,derivatives.Get(m.Instrument.Symbol),now);
                if(tracks.Contains("Early"))
                {
                    var profile=TenfoldEngine.Evaluate(m,candles,now,yesterday?.Rows.FirstOrDefault(r=>r.Symbol==m.Instrument.Symbol)?.Tenfold);
                    row=row with{Tenfold=profile};
                }
                rows.Add(row);
            }
            var snapshot=new OpportunitySnapshot(Guid.NewGuid(),now,OpportunityEngine.Version,OpportunityEngine.Hash(network.HistorySymbols),network.HistorySymbols,
                markets.Select(m=>new OpportunityUniverseRow(m.Instrument.AssetId,m.Instrument.Symbol,m.Price,m.ExchangeReturn24h,m.Volume24h,m.PriceSourceTime,m.PriceReceivedTime,OpportunityEngine.Eligibility(m,now))).ToArray(),rows);
            lastSaved??=repository.Latest()?.AsOf;
            if(lastSaved is null||lastSavedHash!=snapshot.RulesHash||now-lastSaved>=TimeSpan.FromMinutes(5))
            {repository.Save(snapshot);lastSaved=now;lastSavedHash=snapshot.RulesHash;}
            Volatile.Write(ref latest,snapshot);
        }
        finally{gate.Release();}
    }
}
