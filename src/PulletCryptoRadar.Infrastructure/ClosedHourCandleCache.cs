using System.Collections.Concurrent;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Infrastructure;

public sealed class ClosedHourCandleCache(FoundationStore store,TimeProvider clock)
{
    private readonly ConcurrentDictionary<Guid,SemaphoreSlim> gates=new();
    public async Task<ProviderRead> ReadAsync(AssetIdentity asset,Func<Task<ProviderRead>> fetch,CancellationToken token)
    {
        var gate=gates.GetOrAdd(asset.AssetId,_=>new(1,1));await gate.WaitAsync(token);
        try
        {
            var now=clock.GetUtcNow();var saved=store.LatestCandles(asset.AssetId);
            if(IsCurrent(saved,asset,now))return saved!;
            var read=await fetch();
            if(IsCurrent(read,asset,clock.GetUtcNow()))store.SaveResearchProbe(read with {Observations=[]});
            return read;
        }
        finally{gate.Release();}
    }
    public static bool IsCurrent(ProviderRead? read,AssetIdentity asset,DateTimeOffset now)
    {
        if(read?.Evidence is not {Provider:"Binance",Failure:CollectionFailure.None} evidence||evidence.Symbol!=asset.BinanceSymbol||
            evidence.ReceivedTime>now||!evidence.Endpoint.StartsWith("/fapi/v1/klines?",StringComparison.Ordinal)||
            read.Candles is not {Count:>=244} candles)return false;
        var boundary=DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds()/3600000*3600000);
        return candles[^1].ClosedAt==boundary&&candles.All(row=>row.AssetId==asset.AssetId&&row.ReceivedAt<=evidence.ReceivedTime&&
            row.ClosedAt<=row.ReceivedAt&&row.OpenAt.ToUnixTimeMilliseconds()%3600000==0&&row.ClosedAt-row.OpenAt==TimeSpan.FromHours(1))&&
            candles.Zip(candles.Skip(1)).All(pair=>pair.First.ClosedAt==pair.Second.OpenAt);
    }
}
