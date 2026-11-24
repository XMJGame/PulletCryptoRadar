using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Infrastructure;

public sealed class SharedFundingInfoCache(FoundationStore store,TimeProvider clock)
{
    private readonly SemaphoreSlim gate = new(1,1);
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    public async Task<ProviderRead> ReadAsync(AssetIdentity asset,Func<Task<ProviderRead>> fetch,CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var now = clock.GetUtcNow();
            var saved = store.SharedFundingInfo();
            if (saved is null || saved.Evidence.ReceivedTime > now || now-saved.Evidence.ReceivedTime >= Lifetime)
            {
                var read = await fetch();
                if (read.Evidence.Failure != CollectionFailure.None) return read;
                saved = read with { Observations = [] };
                store.SaveResearchProbe(saved);
            }
            return saved with { Observations = BinanceMarketProvider.ParseFundingInfo(asset,saved.Evidence.Body!,
                saved.Evidence.ReceivedTime) };
        }
        finally { gate.Release(); }
    }
}
