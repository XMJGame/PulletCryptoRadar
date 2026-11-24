using System.Security.Cryptography;
using System.Text;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.DataFoundation;
using PulletCryptoRadar.Infrastructure;

namespace PulletCryptoRadar.Host;

public sealed class BinanceCollectionWorker(FoundationStore store, CollectionPipeline pipeline,
    IConfiguration configuration, ILogger<BinanceCollectionWorker> logger, TimeProvider clock,
    IMarketDataProvider marketProvider) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        if (!configuration.GetValue<bool>("Collection:Enabled")) return;
        var symbols = (configuration.GetSection("Collection:Symbols").Get<string[]>() ?? ["BTCUSDT", "ARIAUSDT"])
            .Concat(["BTCUSDT","ETHUSDT"]).Distinct().ToArray();
        if (symbols.Length is < 1 or > 5 || symbols.Any(symbol =>
            !System.Text.RegularExpressions.Regex.IsMatch(symbol, @"\A[\p{L}\p{N}]{1,40}USDT\z")))
            throw new InvalidOperationException("Invalid Binance collection symbols.");
        foreach (var symbol in symbols.Distinct())
        {
            var assetId = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("Binance:USDT:Perpetual:" + symbol)).AsSpan(0,16));
            if (store.Assets().All(asset => asset.AssetId != assetId))
                store.Register(symbol == "ARIAUSDT"
                    ? new(assetId, "ARIA", "AriaAI", symbol, "aria-ai", 38102,
                        [new(ChainId.Bsc, "0x5d3a12c42e5372b2cc3264ab3cdcf660a1555238", 18)], clock.GetUtcNow())
                    : new(assetId, symbol[..^4], symbol[..^4], symbol, null, null, [], clock.GetUtcNow()));
        }
        // Existing failed metadata leases may predate detailed retry scheduling.
        foreach(var source in store.SourceHealth(clock.GetUtcNow()).Where(item=>item.Provider!="Binance" && item.Failure!=CollectionFailure.None))
        {
            var asset=store.Assets().SingleOrDefault(item=>item.BinanceSymbol==source.Symbol);
            if(asset is null)continue;
            var retry=source.RetryAt>clock.GetUtcNow()?source.RetryAt.Value:clock.GetUtcNow();
            store.RetryJobEarlier("Metadata:"+asset.AssetId,retry);
            if(source.Provider=="InsightX")store.RetryJobEarlier("Ownership:"+asset.AssetId,retry);
        }
        var researchMarket=new ResearchMarketCollector(store,pipeline,clock);
        while (!token.IsCancellationRequested)
        {
            var focused = new HashSet<Guid>();
            foreach (var asset in store.Assets().Where(asset => symbols.Contains(asset.BinanceSymbol)).OrderBy(asset => asset.BinanceSymbol is "BTCUSDT" or "ETHUSDT" ? 0 : 1))
            {
                var attemptAt=clock.GetUtcNow();
                try
                {
                    await pipeline.CollectAsync(asset, token);
                    if (store.ReserveJob("Candles:" + asset.AssetId, clock.GetUtcNow(), TimeSpan.FromMinutes(5)))
                        await pipeline.CollectCandlesAsync(asset, token);
                    if (store.ReserveJob("OiHistory:" + asset.AssetId, clock.GetUtcNow(), TimeSpan.FromMinutes(5)))
                        await pipeline.CollectOpenInterestHistoryAsync(asset, token);
                    Assess(asset);
                    foreach(var failed in store.SourceHealth(clock.GetUtcNow()).Where(item=>item.Provider=="Binance" &&
                        item.Symbol==asset.BinanceSymbol && item.CheckedAt>=attemptAt.AddMilliseconds(-1) && item.Failure!=CollectionFailure.None))
                    {
                        var retry=failed.RetryAt>clock.GetUtcNow().AddMinutes(1)?failed.RetryAt.Value:clock.GetUtcNow().AddMinutes(1);
                        if(failed.Endpoint.Contains("/klines?",StringComparison.Ordinal))store.RetryJobEarlier("Candles:"+asset.AssetId,retry);
                        if(failed.Endpoint.Contains("/openInterestHist?",StringComparison.Ordinal))store.RetryJobEarlier("OiHistory:"+asset.AssetId,retry);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception error)
                {
                    logger.LogWarning("Collection failed for {Symbol} ({ErrorType})", asset.BinanceSymbol, error.GetType().Name);
                }
            }
            if (store.ReserveJob("MarketContext", clock.GetUtcNow(), TimeSpan.FromMinutes(2)))
            {
                try
                {
                    var assets = store.Assets();
                    var btc = assets.Single(asset => asset.BinanceSymbol == "BTCUSDT");
                    var eth = assets.Single(asset => asset.BinanceSymbol == "ETHUSDT");
                    var reads = new List<ProviderRead>();
                    var universe = store.Universe();
                    if (universe is null || clock.GetUtcNow()-universe.Evidence.ReceivedTime > TimeSpan.FromHours(6))
                    {
                        var refreshed = await marketProvider.ReadAsync(btc,"Universe",token);
                        reads.Add(refreshed);
                        if (refreshed.Universe is not null) universe = refreshed;
                    }
                    var tickers = await marketProvider.ReadAsync(btc,"AllTickers",token);
                    reads.Add(tickers);
                    var context = new PulletCryptoRadar.Features.MarketStateEngine().Calculate(universe?.Universe ?? [],
                        universe?.Evidence.ReceivedTime ?? DateTimeOffset.MinValue, tickers.Tickers ?? [],
                        store.Latest(btc.AssetId),store.Latest(eth.AssetId),clock.GetUtcNow());
                    store.SaveMarketContext(context,reads);
                    if (tickers.Evidence.Failure == CollectionFailure.None && universe?.Universe is { } eligible &&
                        universe.Evidence.ReceivedTime <= clock.GetUtcNow() &&
                        clock.GetUtcNow()-universe.Evidence.ReceivedTime <= TimeSpan.FromHours(8))
                        store.RefreshPriority(new PulletCryptoRadar.Features.PrioritySelection().Select(
                            eligible.Where(item => !symbols.Contains(item.Symbol)).ToArray(),
                            tickers.Tickers ?? [],clock.GetUtcNow()),clock.GetUtcNow());
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception error)
                {
                    logger.LogWarning("Market context collection failed ({ErrorType})",error.GetType().Name);
                }
            }
            // Give verified samples a dedicated collection cadence.
            try
            {
                var universe=store.Universe();
                var eligible=universe?.Evidence.ReceivedTime<=clock.GetUtcNow()&&clock.GetUtcNow()-universe.Evidence.ReceivedTime<=TimeSpan.FromHours(8)
                    ?universe.Universe?.Select(item=>item.Symbol).ToHashSet()??[]:[];
                var samples=store.Assets().Where(asset=>eligible.Contains(asset.BinanceSymbol)&&!symbols.Contains(asset.BinanceSymbol)&&
                    ResearchNetworks.Single(asset) is not null&&(asset.CoinGeckoId is not null||asset.CoinMarketCapId is not null))
                    .OrderBy(asset=>asset.BinanceSymbol,StringComparer.Ordinal).Take(4).Where(asset=>!focused.Contains(asset.AssetId)).ToArray();
                if(await researchMarket.CollectNextAsync(samples,token) is {} collected)focused.Add(collected);
            }
            catch(OperationCanceledException) when(token.IsCancellationRequested){return;}
            catch(Exception error){logger.LogWarning("Research market collection failed ({ErrorType})",error.GetType().Name);}
            try
            {
                var cachedUniverse = store.Universe();
                if (cachedUniverse?.Universe is { } instruments && cachedUniverse.Evidence.ReceivedTime <= clock.GetUtcNow() &&
                    clock.GetUtcNow()-cachedUniverse.Evidence.ReceivedTime <= TimeSpan.FromHours(8) &&
                    store.BackgroundRequestHeadroom(clock.GetUtcNow()) >= 2 &&
                    store.ReserveJob("DepthRotation",clock.GetUtcNow(),TimeSpan.FromMinutes(1)))
                {
                    var priority = store.ReservePriority(clock.GetUtcNow());
                    var focusSymbols = store.Assets().Where(item => focused.Contains(item.AssetId)).Select(item => item.BinanceSymbol).ToHashSet();
                    var priorityInstrument = instruments.SingleOrDefault(item => item.Symbol == priority?.Symbol && !symbols.Contains(item.Symbol) && !focusSymbols.Contains(item.Symbol));
                    var slots = Math.Min(4,store.BackgroundRequestHeadroom(clock.GetUtcNow())/2);
                    var batch = slots == 1 && priorityInstrument is not null ? [] : store.ReserveDepthBatch(instruments.Where(item => !symbols.Contains(item.Symbol) &&
                        !focusSymbols.Contains(item.Symbol) && item.Symbol != priorityInstrument?.Symbol).ToArray(),
                        Math.Max(1,slots-(priorityInstrument is null ? 0 : 1)),clock.GetUtcNow());
                    if (priorityInstrument is not null) batch = new[] { priorityInstrument }.Concat(batch).ToArray();
                    foreach (var instrument in batch)
                    {
                        if(store.BackgroundRequestHeadroom(clock.GetUtcNow())<2)break;
                        try
                        {
                            using var priorityScope=CollectionRequestScope.Enter(RequestPriority.Background);
                            var assetId = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("Binance:USDT:Perpetual:" + instrument.Symbol)).AsSpan(0,16));
                            var asset = store.Assets().SingleOrDefault(item => item.AssetId == assetId);
                            if (asset is null)
                            {
                                asset = new(assetId,instrument.BaseAsset,instrument.BaseAsset,instrument.Symbol,null,null,[],clock.GetUtcNow());
                                asset = store.EnsureRegistered(asset);
                            }
                            await pipeline.CollectCandlesAsync(asset,token);
                            await pipeline.CollectOpenInterestHistoryAsync(asset,token);
                            Assess(asset);
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                        catch (Exception error)
                        {
                            logger.LogWarning("Depth collection failed for {Symbol} ({ErrorType})",instrument.Symbol,error.GetType().Name);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                logger.LogWarning("Depth rotation failed ({ErrorType})",error.GetType().Name);
            }
            // Research metadata must not be restricted to the fixed BTC/ETH collection list.
            var research = store.Assets().Where(asset => asset.CoinGeckoId is not null ||
                asset.CoinMarketCapId is not null || ResearchNetworks.Single(asset) is not null);
            foreach (var asset in research.OrderBy(asset => asset.AssetId))
            {
                if (!store.ReserveJob("Metadata:"+asset.AssetId,clock.GetUtcNow(),TimeSpan.FromHours(6))) continue;
                var attemptAt=clock.GetUtcNow();
                try
                {
                    var ownership=store.ReserveJob("Ownership:"+asset.AssetId,attemptAt,TimeSpan.FromHours(12));
                    await pipeline.CollectSupplementaryAsync(asset,token,ownership);
                    var failed=store.SourceHealth(clock.GetUtcNow()).Where(item=>item.Symbol==asset.BinanceSymbol &&
                        item.Provider!="Binance" && item.CheckedAt>=attemptAt.AddMilliseconds(-1) && item.Failure!=CollectionFailure.None).ToArray();
                    if(failed.Length>0)
                    {
                        var retry=failed.Max(item=>item.RetryAt??clock.GetUtcNow().AddMinutes(5));
                        if(retry<clock.GetUtcNow().AddMinutes(1))retry=clock.GetUtcNow().AddMinutes(1);
                        store.RetryJobEarlier("Metadata:"+asset.AssetId,retry);
                        if(ownership && failed.Any(item=>item.Provider=="InsightX"))store.RetryJobEarlier("Ownership:"+asset.AssetId,retry);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception error)
                {
                    logger.LogWarning("Research metadata failed for {Symbol} ({ErrorType})",asset.BinanceSymbol,error.GetType().Name);
                }
                break; // Bound research requests and allow the persistent schedule to rotate.
            }
            await Task.Delay(TimeSpan.FromMinutes(1), clock, token);
        }
    }

    private void Assess(AssetIdentity asset) =>
        store.SaveRadar(new PulletCryptoRadar.Features.RadarEngine()
            .Calculate(asset,store.Latest(asset.AssetId),clock.GetUtcNow()));
}
