using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Trading;

public sealed class ExecutionService(IMarketDataProvider provider,IExecutionStore store,IEntryStore entries,
    IQuickCheckStore catalog,TradingLimits limits,TimeProvider clock)
{
    public async Task<string> RefreshAsync(Guid assetId,CancellationToken token)
    {
        using var priority=CollectionRequestScope.Enter(RequestPriority.Critical);
        var asset = catalog.Assets().SingleOrDefault(item => item.AssetId == assetId);
        if (asset is null) return "UnknownAsset";
        var now = clock.GetUtcNow();
        var universe = catalog.Universe();
        if (universe?.Universe?.Any(item => item.Symbol == asset.BinanceSymbol) != true ||
            universe.Evidence.ReceivedTime > now || now-universe.Evidence.ReceivedTime > TimeSpan.FromHours(8))
            return "FreshActiveContractCatalogRequired";
        if (!catalog.ReserveJob("ExecutionEvidence:"+assetId,now,TimeSpan.FromSeconds(15))) return "CoolingDown";
        // Funding can take longer. Fetch the executable quote last so it can still be checked within two seconds.
        var funding = await provider.ReadAsync(asset,"FundingHistory",token);
        var book = await provider.ReadAsync(asset,"OrderBook",token);
        var evidence = new ExecutionEvidence(assetId,clock.GetUtcNow(),book,funding);
        store.SaveExecution(evidence);
        var rules = universe.ContractRules?.SingleOrDefault(item => item.Symbol == asset.BinanceSymbol);
        var setups = Enum.GetValues<EntryStrategy>().SelectMany(strategy => Enum.GetValues<TradeSide>()
                .Select(side => entries.LatestEntry(assetId,strategy,side)))
            .Where(item => item?.Version == EntryEngine.Version && item.Status == "PatternConfirmed" &&
                item.AsOf <= clock.GetUtcNow() && item.Setup?.Phase == SetupPhase.PatternConfirmed)
            .GroupBy(item => item!.Side).Select(group => group.OrderByDescending(item => item!.Setup!.ConfirmedAt).First()!).ToArray();
        var account = store.Account();
        foreach (var item in setups)
            store.SaveSizing(new SizingEngine().Evaluate(asset,item.Setup!,evidence,rules,account.Cash,account.Cash,
                limits,new("ConservativeCosts-2.0.0"),clock.GetUtcNow()));
        return book.Evidence.Failure == CollectionFailure.None && funding.Evidence.Failure == CollectionFailure.None
            ? setups.Length > 0 ? "EvidenceAndSizingRecorded" : "EvidenceRecordedNoConfirmedSetup" : "PartialEvidence";
    }
}
