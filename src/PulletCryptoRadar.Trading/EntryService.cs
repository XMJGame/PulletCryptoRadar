using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Trading;

public sealed class EntryService(IEntryStore entries,IFoundationEvidenceStore evidence,TimeProvider clock)
{
    private readonly EntryEngine engine = new();
    public void Evaluate(Guid assetId)
    {
        var now = clock.GetUtcNow();
        var snapshot = evidence.Latest(assetId);
        var candles = entries.LatestCandles(assetId);
        var context = evidence.MarketContext();
        foreach (var strategy in Enum.GetValues<EntryStrategy>())
        foreach (var side in Enum.GetValues<TradeSide>())
        {
            var previous = entries.LatestEntry(assetId,strategy,side);
            if (previous is not null && previous.AsOf >= now) continue;
            entries.SaveEntry(engine.Evaluate(assetId,strategy,side,snapshot,candles,context,
                previous?.Version == EntryEngine.Version ? previous.Setup : null,now));
        }
    }
}
