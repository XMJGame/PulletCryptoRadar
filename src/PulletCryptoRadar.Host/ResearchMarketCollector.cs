using PulletCryptoRadar.Core;
using PulletCryptoRadar.DataFoundation;
using PulletCryptoRadar.Infrastructure;

namespace PulletCryptoRadar.Host;

public sealed class ResearchMarketCollector(FoundationStore store,CollectionPipeline pipeline,TimeProvider clock)
{
    public async Task<Guid?> CollectNextAsync(IReadOnlyList<AssetIdentity> assets,CancellationToken token)
    {
        var work=store.ReserveResearchMarket(assets.Select(asset=>asset.AssetId).ToArray(),clock.GetUtcNow(),store.BackgroundRequestHeadroom(clock.GetUtcNow()));
        if(work is null)return null;
        var asset=assets.Single(asset=>asset.AssetId==work.AssetId);
        using var scope=CollectionRequestScope.Enter(RequestPriority.Background);
        try
        {
            // OI loses freshness fastest; do it first. Each call still reserves through the shared ledger.
            foreach(var oi in new[]{true,false})
            {
                if(oi?!work.OpenInterest:!work.Candles)continue;
                if(store.BackgroundRequestHeadroom(clock.GetUtcNow())<1)break;
                var attempt=clock.GetUtcNow();
                if(oi)await pipeline.CollectOpenInterestHistoryAsync(asset,token);
                else await pipeline.CollectCandlesAsync(asset,token);
                var source=store.SourceHealth(clock.GetUtcNow()).Where(row=>row.Provider=="Binance"&&row.Symbol==asset.BinanceSymbol&&
                    row.CheckedAt>=attempt.AddMilliseconds(-1)&&row.Endpoint.Contains(oi?"/openInterestHist":"/klines",StringComparison.Ordinal))
                    .OrderByDescending(row=>row.CheckedAt).FirstOrDefault();
                var needed=oi?new[]{Metric.OiQuantityChange1h,Metric.OiQuantityChange4h}:
                    new[]{Metric.Return24hClosed,Metric.VolumeRatioH1,Metric.TakerBuyShareH1};
                var saved=store.Latest(asset.AssetId);
                var metrics=needed.Select(metric=>saved?.Metrics.SingleOrDefault(row=>row.Metric==metric)).ToArray();
                var observations=metrics.Select(metric=>metric?.Evidence.Where(row=>row.Provider=="Binance"&&row.Status==ObservationStatus.Valid&&
                    row.ReceivedTime>=attempt.AddMilliseconds(-1)).OrderByDescending(row=>row.SourceTime).FirstOrDefault()).ToArray();
                var success=source?.Failure==CollectionFailure.None&&metrics.All(metric=>metric?.UsableForScoring==true)&&
                    observations.All(row=>row?.SourceTime is {} time&&time<=clock.GetUtcNow()&&
                        clock.GetUtcNow()-time<=TimeSpan.FromMinutes(oi?10:75));
                var retry=source?.RetryAt??(source?.Failure==CollectionFailure.None?clock.GetUtcNow().AddMinutes(oi?1:15):null);
                store.CompleteResearchMarket(work,oi,success,clock.GetUtcNow(),retry,
                    success?observations.Min(row=>row!.SourceTime):null);
            }
            store.SaveRadar(new PulletCryptoRadar.Features.RadarEngine().Calculate(asset,store.Latest(asset.AssetId),clock.GetUtcNow()));
            return asset.AssetId;
        }
        finally{store.ReleaseResearchMarket(work);}
    }
}
