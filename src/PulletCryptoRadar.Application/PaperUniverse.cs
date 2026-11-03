using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Application;

// Use the same bounded universe for analysis and its prerequisite collection.
public static class PaperUniverse
{
    public static IReadOnlyList<MarketView> Select(IReadOnlyList<MarketView> markets,DateTimeOffset now,int limit=60)=>
        OpportunityEngine.Select(markets,now,limit).Select(r=>r.Market).ToArray();

    public static IReadOnlyList<MarketView> Warmup(IReadOnlyList<MarketView> markets,PaperState state,DateTimeOffset now,int limit=60)
    {
        var required=state.Positions.Select(p=>p.Symbol).Concat(state.Pending.Select(p=>p.Symbol)).Concat(state.AnalysisSymbols).ToHashSet(StringComparer.Ordinal);
        return markets.Where(m=>required.Contains(m.Instrument.Symbol)).Concat(Select(markets,now,limit))
            .DistinctBy(m=>m.Instrument.Symbol).ToArray();
    }
}
