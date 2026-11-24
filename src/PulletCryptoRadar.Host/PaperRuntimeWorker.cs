using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;
using PulletCryptoRadar.Trading;

namespace PulletCryptoRadar.Host;

public sealed class PaperRuntimeWorker(FoundationStore store,IPositionMarketStream stream,IMarketDataProvider market,
    PaperRuntimeCoordinator runtime,PaperRuntimePolicy policy,TimeProvider clock,ILogger<PaperRuntimeWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token) => await Task.WhenAll(WatchAsync(token),FundingAsync(token));
    private AssetIdentity[] WatchList()
    {
        var now = clock.GetUtcNow(); var assets = store.Assets();
        var held = store.Portfolio().Positions.Where(item => item.RemainingQuantity > 0).Select(item => item.AssetId);
        var candidates = policy.Enabled ? store.LatestEntries().Where(item => item.Status == "PatternConfirmed" &&
            item.Setup?.ConfirmedAt <= now && now-item.Setup.ConfirmedAt <= TimeSpan.FromMinutes(1)).Select(item => item.AssetId) : [];
        var probes = assets.Where(item => policy.ObserveSymbols.Contains(item.BinanceSymbol)).Select(item => item.AssetId);
        return held.Concat(candidates).Concat(probes).Distinct().Take(6).Select(id => assets.Single(item => item.AssetId == id)).ToArray();
    }
    private async Task WatchAsync(CancellationToken token)
    {
        if (!policy.Enabled && policy.ObserveSymbols.Length == 0 && store.Portfolio().Positions.All(item => item.RemainingQuantity == 0)) return;
        while (!token.IsCancellationRequested)
        {
            var assets = WatchList();
            if (assets.Length == 0) { runtime.Session([]); await Task.Delay(TimeSpan.FromSeconds(1),clock,token); continue; }
            using var session = CancellationTokenSource.CreateLinkedTokenSource(token);
            runtime.Session(assets);
            try
            {
                await foreach (var read in stream.ReadAsync(assets,session.Token))
                {
                    runtime.Ingest(read);
                    if (!assets.Select(item => item.AssetId).SequenceEqual(WatchList().Select(item => item.AssetId))) break;
                }
                runtime.Disconnected("StreamSessionRenewing");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                runtime.Disconnected(error.GetType().Name);
                logger.LogWarning("Paper position stream reconnecting ({ErrorType})",error.GetType().Name);
            }
            await Task.Delay(TimeSpan.FromSeconds(1),clock,token);
        }
    }
    private async Task FundingAsync(CancellationToken token)
    {
        if (!policy.Enabled && store.Portfolio().Positions.Count == 0) return;
        var engine = new PaperPortfolioEngine();
        while (!token.IsCancellationRequested)
        {
            try
            {
                var now = clock.GetUtcNow();
                var positions = store.Portfolio().Positions.Where(item => item.ClosedAt is null || now-item.ClosedAt < TimeSpan.FromDays(1)).ToArray();
                if (positions.Length > 0 && store.ReserveJob("PaperFundingGlobal",now,TimeSpan.FromSeconds(30)))
                foreach (var group in positions.GroupBy(item => item.AssetId).OrderBy(item => store.FundingCursor(item.Key) ?? DateTimeOffset.MinValue))
                {
                    if (!store.ReserveJob("PaperFunding:"+group.Key,now,TimeSpan.FromMinutes(5))) continue;
                    var asset = store.Assets().Single(item => item.AssetId == group.Key);
                    var earliest = group.Min(item => item.OpenedAt).AddSeconds(-30);
                    var since = store.FundingCursor(group.Key) ?? earliest;
                    var read = await market.ReadAsync(asset,"PositionFunding:"+since.ToUnixTimeMilliseconds(),token);
                    store.SavePaperMarket(read);
                    if (read.Evidence.Failure != CollectionFailure.None || read.FundingSettlements is null) break;
                    var unresolved = false;
                    foreach (var settlement in read.FundingSettlements)
                    foreach (var position in group)
                    {
                        var key = $"funding:{position.PositionId}:{settlement.SettledAt.ToUnixTimeMilliseconds()}:{settlement.RateType}";
                        var current = store.Portfolio(); var history = store.PaperEvents(position.PositionId);
                        var preview = engine.ConservativeFundingBoundary(current,position.PositionId,settlement,history,read.Evidence.Id,clock.GetUtcNow());
                        if (preview.Status is "FundingApplied" or "FundingEstimateApplied" or "FundingAlreadyApplied" or "NotHeldAtSettlement")
                        {
                            store.CommitPaper(PaperRuntimeCoordinator.CommandId(key),portfolio => engine.ConservativeFundingBoundary(portfolio,position.PositionId,
                                settlement,store.PaperEvents(position.PositionId),read.Evidence.Id,clock.GetUtcNow()));
                            store.SetPaperIssue(key,"Resolved",clock.GetUtcNow(),false);
                        }
                        else { unresolved = true; store.SetPaperIssue(key,preview.Status,clock.GetUtcNow()); }
                    }
                    if (!unresolved)
                    {
                        var cursor = read.FundingSettlements.Count >= 1000 ? read.FundingSettlements[^1].SettledAt.AddMilliseconds(1) :
                            clock.GetUtcNow().AddHours(-1) > earliest ? clock.GetUtcNow().AddHours(-1) : earliest;
                        store.SaveFundingCursor(group.Key,cursor);
                        if (read.FundingSettlements.Count < 1000) runtime.FundingVerified(group.Key,clock.GetUtcNow());
                    }
                    break;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                runtime.Disconnected("Funding:"+error.GetType().Name);
                logger.LogWarning("Paper funding reconciliation failed ({ErrorType})",error.GetType().Name);
            }
            await Task.Delay(TimeSpan.FromSeconds(1),clock,token);
        }
    }
}
