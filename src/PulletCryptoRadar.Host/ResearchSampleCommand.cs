using System.Text.Json;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;
using PulletCryptoRadar.DataFoundation;

namespace PulletCryptoRadar.Host;

public static class ResearchSampleCommand
{
    // Curated cross-source samples, never recommendations or automatic symbol matches.
    public static readonly ResearchIdentitySample[] Samples =
    [
        new("MYXUSDT","MYX Finance","myx-finance",new(ChainId.Bsc,"0xd82544bf0dfe8385ef8fa34d67e6e4940cc63e16",18),
            "https://myxfinance.gitbook.io/myx/protocol/tokenomics"),
        new("WIFUSDT","dogwifhat","dogwifcoin",new(ChainId.Solana,"EKpQGSJtjMFqKZ9KQanSqYXRcF8fBopzLHYxdM65zcjm",6),
            "https://hashex.org/audits/dogwifhat/"),
        new("TRBUSDT","Tellor Tributes","tellor",new(ChainId.Ethereum,"0x88df592f8eb5d7bd38bfef7deb0fbc02cf3778a0",18),
            "https://etherscan.io/token/0x88df592f8eb5d7bd38bfef7deb0fbc02cf3778a0")
    ];
    public static async Task<int> RunAsync(string directory,string? refreshSymbol = null)
    {
        var store=new FoundationStore(directory); store.Initialize(DateTimeOffset.UtcNow);
        using var http=new HttpClient(HttpTransport.Create(false)) {Timeout=TimeSpan.FromSeconds(15)};
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PulletCryptoRadar/1.0");
        var provider=new SupplementaryProvider(http,store,TimeProvider.System);
        using var marketHttp=new HttpClient(HttpTransport.Create(false)) {Timeout=TimeSpan.FromSeconds(12)};
        var pipeline=new CollectionPipeline(new BinanceMarketProvider(marketHttp,store,TimeProvider.System),store,TimeProvider.System,provider);
        var results=new List<object>();
        if(refreshSymbol is not null && !Samples.Any(item=>item.Symbol==refreshSymbol))
            throw new ArgumentException("Refresh requires an exact curated sample symbol.");
        foreach(var sample in Samples)
        {
            var asset=store.Assets().SingleOrDefault(item=>item.BinanceSymbol==sample.Symbol);
            try
            {
                var universe=store.Universe();
                if(asset is null || universe?.Universe?.Any(item=>item.Symbol==sample.Symbol)!=true ||
                    DateTimeOffset.UtcNow-universe.Evidence.ReceivedTime>TimeSpan.FromHours(8))
                    throw new InvalidDataException("Current exact Binance instrument is unavailable.");
                var existing=store.ResearchProofs().FirstOrDefault(proof=>proof.AssetId==asset.AssetId &&
                    proof.After.CoinGeckoId==sample.CoinGeckoId && proof.VerifiedAt<=DateTimeOffset.UtcNow &&
                    DateTimeOffset.UtcNow-proof.VerifiedAt<TimeSpan.FromHours(24));
                var status="CachedProof";
                if(existing is null)
                {
                    var read=await provider.ReadAsync(asset with {CoinGeckoId=sample.CoinGeckoId},"GeckoIdentity",default);
                    if(read.Evidence.Failure!=CollectionFailure.None)
                    {
                        store.SaveResearchProbe(read);
                        throw new InvalidDataException("Identity lookup: "+read.Evidence.Failure);
                    }
                    try {store.ApplyResearchIdentity(sample,read,DateTimeOffset.UtcNow);}
                    catch {store.SaveResearchProbe(read);throw;}
                    status="CrossSourceVerified";
                }
                asset=store.Assets().Single(item=>item.AssetId==asset.AssetId);
                if(refreshSymbol==sample.Symbol ? store.ReserveJob("ResearchProbe:"+asset.AssetId,DateTimeOffset.UtcNow,TimeSpan.FromMinutes(1)) :
                    store.ReserveJob("Metadata:"+asset.AssetId,DateTimeOffset.UtcNow,TimeSpan.FromHours(6)))
                    await pipeline.CollectSupplementaryAsync(asset,default);
                var snapshot=store.Latest(asset.AssetId);
                results.Add(new {symbol=sample.Symbol,chain=sample.Token.Chain.ToString(),status,
                    metrics=snapshot?.Metrics.Where(item=>item.Metric is Metric.MarketCap or Metric.DexLiquidity or Metric.Top10 or Metric.TotalClusterPercent)
                        .Select(item=>new {metric=item.Metric.ToString(),status=item.Status.ToString(),item.Value,item.Provider})});
            }
            catch(Exception error) when(error is InvalidDataException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
            {results.Add(new {symbol=sample.Symbol,status="Unresolved",reason=error.Message});}
        }
        Console.WriteLine(JsonSerializer.Serialize(results,new JsonSerializerOptions {WriteIndented=true}));
        return store.ResearchProofs().Select(item=>item.AssetId).Distinct().Count()>=Samples.Length?0:2;
    }
}
