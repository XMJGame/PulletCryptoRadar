using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;

namespace PulletCryptoRadar.Host;

public sealed class HolderCollectionWorker(FoundationStore store,GoPlusHolderProvider provider,TimeProvider clock,
    IConfiguration configuration,ILogger<HolderCollectionWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        if(!configuration.GetValue<bool>("Collection:Enabled"))return;
        // Resume interrupted/failed leases without fetching a second baseline just because the process restarted.
        var health=store.SourceHealth(clock.GetUtcNow()).Where(row=>row.Provider=="GoPlus").ToArray();
        foreach(var asset in store.Assets().Where(asset=>ResearchNetworks.Single(asset) is not null))
        {
            var latest=store.HolderHistory(asset.AssetId,1).FirstOrDefault();
            var failed=health.FirstOrDefault(row=>row.Symbol==asset.BinanceSymbol && row.Failure!=CollectionFailure.None);
            if(failed is not null || latest is null || clock.GetUtcNow()-latest.ReceivedAt>=TimeSpan.FromHours(6))
                store.RetryJobEarlier("HolderResearch:"+asset.AssetId,failed?.RetryAt>clock.GetUtcNow()?failed.RetryAt.Value:clock.GetUtcNow());
        }
        while(!token.IsCancellationRequested)
        {
            foreach(var asset in store.Assets().Where(asset=>ResearchNetworks.Single(asset) is not null))
            {
                var job="HolderResearch:"+asset.AssetId;
                if(!store.ReserveJob(job,clock.GetUtcNow(),TimeSpan.FromHours(6)))continue;
                try
                {
                    var result=await provider.ReadAsync(asset,token);store.SaveHolderRead(result.Read);
                    if(result.Read.Evidence.Failure!=CollectionFailure.None)
                        store.RetryJobEarlier(job,result.Read.Evidence.RetryAt>clock.GetUtcNow()?result.Read.Evidence.RetryAt.Value:clock.GetUtcNow().AddMinutes(15));
                }
                catch(OperationCanceledException) when(token.IsCancellationRequested){return;}
                catch(Exception error)
                {
                    store.RetryJobEarlier(job,clock.GetUtcNow().AddMinutes(15));
                    logger.LogWarning("Holder research failed for {Symbol} ({ErrorType})",asset.BinanceSymbol,error.GetType().Name);
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(30),token);
        }
    }
}
