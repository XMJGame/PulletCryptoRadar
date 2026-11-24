using PulletCryptoRadar.Core;
using PulletCryptoRadar.DataFoundation;
using PulletCryptoRadar.Infrastructure;

namespace PulletCryptoRadar.Tests;

public sealed class QuickCheckTests
{
    private static readonly DateTimeOffset Now = new(2026,11,3,8,0,0,TimeSpan.Zero);
    private static AssetIdentity Asset(string symbol) => new(BinanceIdentity.AssetId(symbol),symbol[..^4],symbol[..^4],
        symbol,null,null,[],Now);
    private static void Catalog(FoundationStore store, params string[] symbols)
    {
        var instruments = symbols.Select(symbol => new MarketInstrument(symbol,symbol[..^4],Now)).ToArray();
        var read = new ProviderRead(new(Guid.NewGuid(),"Binance","*USDT-PERPETUAL*","/fapi/v1/exchangeInfo",Now,
            200,CollectionFailure.None,"{}"),[],Universe:instruments);
        store.SaveMarketContext(new("test",Now,MarketState.Unknown,instruments.Length,0,0,null,null,[]),[read]);
    }

    [Fact]
    public void SearchResolvesExactCodesAndSeparatesAmbiguousNames()
    {
        InStore((store,_) =>
        {
            Catalog(store,"AIOUSDT","ZBTUSDT","AIOXUSDT");
            store.Register(Asset("AIOUSDT") with { Name = "OlaXBT" });
            var clock = new Clock();
            var search = new TokenSearchService(store,clock);
            Assert.Equal("AIOUSDT",Assert.Single(search.Search(" aio ").Candidates).BinanceSymbol);
            Assert.Equal("Exact",search.Search("OlaXBT").Status);
            Assert.Equal("NotFoundInVerifiedCatalog",search.Search("AOI").Status);
            Assert.Equal("SelectCandidate",search.Search("X").Status);
            Assert.Throws<ArgumentException>(() => search.Search(" "));
            Assert.Throws<ArgumentException>(() => search.Search(new string('x',129)));
            store.Register(Asset("OTHERUSDT") with { Symbol = "AIO" });
            Assert.Equal("Ambiguous",search.Search("AIO").Status);
            Assert.Equal(2,search.Search("AIO").Candidates.Count);
            clock.Advance(TimeSpan.FromHours(9));
            Assert.False(search.Search("ZBT").CatalogFresh);
            Assert.Equal("CatalogUnavailable",search.Search("ZBT").Status);
        });
    }

    [Fact]
    public void ContractResolutionRespectsChainAndSolanaCase()
    {
        InStore((store,_) =>
        {
            const string address = "0xabcdefabcdefabcdefabcdefabcdefabcdefabcd";
            const string mint = "So11111111111111111111111111111111111111112";
            store.Register(Asset("AIOUSDT") with { Chains = [new(ChainId.Bsc,address,18)] });
            store.Register(Asset("BIOUSDT") with { Chains = [new(ChainId.Ethereum,address,18)] });
            store.Register(Asset("SOLUSDT") with { Chains = [new(ChainId.Solana,mint,9)] });
            var search = new TokenSearchService(store,new Clock());
            Assert.Equal("Ambiguous",search.Search(address).Status);
            Assert.Equal("AIOUSDT",Assert.Single(search.Search(address.ToUpperInvariant(),ChainId.Bsc).Candidates).BinanceSymbol);
            Assert.Equal("SOLUSDT",Assert.Single(search.Search(mint,ChainId.Solana).Candidates).BinanceSymbol);
            Assert.Empty(search.Search(mint.ToLowerInvariant(),ChainId.Solana).Candidates);
            Assert.Empty(search.Search("0x1111111111111111111111111111111111111111").Candidates);
        });
    }

    [Fact]
    public async Task AnalysisCollectsOnceReusesCacheAndDoesNotModifyRadarOrPriority()
    {
        await InStoreAsync(async (store,folder) =>
        {
            Catalog(store,"AIOUSDT");
            var clock = new Clock();
            var provider = new Provider(clock);
            var checks = new QuickCheckService(store,store,new CollectionPipeline(provider,store,clock),clock);
            var id = Asset("AIOUSDT").AssetId;
            var first = await checks.AnalyzeAsync(id,default);
            Assert.NotNull(first);
            Assert.Equal(6,provider.Calls);
            Assert.Equal("NotAssessed",first.TradeDecision);
            Assert.False(first.Research.LowCapResearchQualified);
            Assert.Single(store.QuickCheckHistory());
            Assert.Empty(store.Radar());
            Assert.Empty(store.Priority(clock.GetUtcNow()));
            var cached = await checks.AnalyzeAsync(id,default);
            Assert.Equal("Cached",cached!.CollectionState);
            Assert.Equal(6,provider.Calls);
            Assert.Equal(first.SnapshotAsOf,cached.SnapshotAsOf);
            store = new FoundationStore(folder);
            Assert.Single(store.QuickCheckHistory());
            Assert.NotNull(store.QuickCheckCompleted(id));
            clock.Advance(TimeSpan.FromHours(2));
            Assert.Equal(RadarStage.InsufficientData,checks.Cached(id)!.Research.Stage);
            Assert.Contains(checks.Cached(id)!.Findings,text => text.Contains("失鲜",StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task ConcurrentRequestsShareRefreshLeaseAndUnknownAssetsNeverTriggerHttp()
    {
        await InStoreAsync(async (store,_) =>
        {
            Catalog(store,"AIOUSDT");
            store.Register(Asset("AIOUSDT"));
            var clock = new Clock();
            var provider = new Provider(clock);
            var checks = new QuickCheckService(store,store,new CollectionPipeline(provider,store,clock),clock);
            var results = await Task.WhenAll(Enumerable.Range(0,4).Select(_ => checks.AnalyzeAsync(Asset("AIOUSDT").AssetId,default)));
            Assert.Equal(6,provider.Calls);
            Assert.Contains(results,item => item!.CollectionState == "RefreshBusyOrCoolingDown");
            Assert.Null(await checks.AnalyzeAsync(Guid.NewGuid(),default));
            Assert.Equal(6,provider.Calls);
        });
    }

    [Fact]
    public async Task InactiveContractReturnsEvidenceWithoutNewRequestsAndFailureStaysPartial()
    {
        await InStoreAsync(async (store,_) =>
        {
            Catalog(store,"ZBTUSDT");
            store.Register(Asset("AIOUSDT"));
            var clock = new Clock();
            var provider = new Provider(clock) { Fail = true };
            var checks = new QuickCheckService(store,store,new CollectionPipeline(provider,store,clock),clock);
            Assert.Equal("ContractUnverifiedOrInactive",(await checks.AnalyzeAsync(Asset("AIOUSDT").AssetId,default))!.CollectionState);
            Assert.Equal(0,provider.Calls);
            var result = await checks.AnalyzeAsync(Asset("ZBTUSDT").AssetId,default);
            Assert.Equal("Partial",result!.CollectionState);
            Assert.Equal(RadarStage.InsufficientData,result.Research.Stage);
            Assert.Equal("NotAssessed",result.TradeDecision);
        });
    }

    [Fact]
    public async Task BackgroundAndQuickCollectionSerializePerAssetAndPreserveBothFeatureSets()
    {
        await InStoreAsync(async (store,_) =>
        {
            var asset = Asset("AIOUSDT");
            store.Register(asset);
            var clock = new Clock();
            var provider = new Provider(clock);
            var pipeline = new CollectionPipeline(provider,store,clock);
            await Task.WhenAll(pipeline.CollectAsync(asset,default),pipeline.CollectCandlesAsync(asset,default),
                pipeline.CollectOpenInterestHistoryAsync(asset,default));
            Assert.Equal(1,provider.MaximumConcurrent);
            var snapshot = store.Latest(asset.AssetId)!;
            Assert.NotNull(Assert.Single(snapshot.Metrics,item => item.Metric == Metric.Price).Value);
            Assert.NotNull(Assert.Single(snapshot.Metrics,item => item.Metric == Metric.Ema60H1).Value);
            Assert.NotNull(Assert.Single(snapshot.Metrics,item => item.Metric == Metric.OiQuantityChange24h).Value);
        });
    }

    [Fact]
    public void RecentHistoryIsBoundedDeduplicatedAndDeletionDoesNotEraseFeaturesOrIdentity()
    {
        InStore((store,folder) =>
        {
            var assets = Enumerable.Range(0,10).Select(i => Asset("T"+i+"USDT")).ToArray();
            for (var i = 0; i < assets.Length; i++)
            {
                store.Register(assets[i]);
                store.TouchQuickCheck(assets[i].AssetId,Now.AddMinutes(i));
            }
            store = new FoundationStore(folder);
            Assert.Equal(8,store.QuickCheckHistory().Count);
            Assert.DoesNotContain(store.QuickCheckHistory(),item => item.AssetId == assets[0].AssetId);
            store.TouchQuickCheck(assets[2].AssetId,Now.AddMinutes(11));
            Assert.Equal(assets[2].AssetId,store.QuickCheckHistory()[0].AssetId);
            store.Save(new("test",assets[2].AssetId,Now,MarketState.Unknown,[]),[]);
            store.DeleteQuickCheckHistory(assets[2].AssetId);
            Assert.Equal(7,store.QuickCheckHistory().Count);
            Assert.NotNull(store.Latest(assets[2].AssetId));
            Assert.Equal(10,store.Assets().Count);
            store.DeleteQuickCheckHistory(null);
            Assert.Empty(store.QuickCheckHistory());
        });
    }

    [Fact]
    public void QuickRefreshDefersWhenBenchmarkRequestHeadroomIsInsufficient()
    {
        InStore((store,_) =>
        {
            var asset = Asset("AIOUSDT");
            store.Register(asset);
            for (var i = 0; i < 25; i++)
            {
                var request = Guid.NewGuid();
                Assert.True(store.Reserve(request,"Binance",Now,100));
                store.Complete(request,1);
            }
            Assert.False(store.TryBeginQuickCheck(asset.AssetId,Now));
            Assert.Null(store.QuickCheckCompleted(asset.AssetId));
            Assert.True(store.TryBeginQuickCheck(asset.AssetId,Now.AddSeconds(61)));
        });
    }

    [Fact]
    public void InterruptedRefreshLeaseAndGlobalCooldownPersistAcrossRestart()
    {
        InStore((store,folder) =>
        {
            var first = Asset("AIOUSDT");
            var second = Asset("ZBTUSDT");
            store.Register(first); store.Register(second);
            Assert.True(store.TryBeginQuickCheck(first.AssetId,Now));
            store = new FoundationStore(folder);
            Assert.False(store.TryBeginQuickCheck(first.AssetId,Now.AddMinutes(1)));
            Assert.False(store.TryBeginQuickCheck(second.AssetId,Now.AddSeconds(5)));
            Assert.False(store.TryBeginQuickCheck(second.AssetId,Now.AddSeconds(11)));
            Assert.True(store.TryBeginQuickCheck(second.AssetId,Now.AddSeconds(61)));
            store.FinishQuickCheck(second.AssetId,Now.AddSeconds(61),Now.AddSeconds(62));
            Assert.False(store.TryBeginQuickCheck(second.AssetId,Now.AddSeconds(100)));
            Assert.True(store.TryBeginQuickCheck(first.AssetId,Now.AddMinutes(5)));
            store.FinishQuickCheck(first.AssetId,Now,Now.AddMinutes(5).AddSeconds(1));
            Assert.Null(store.QuickCheckCompleted(first.AssetId));
        });
    }

    private sealed class Clock : TimeProvider
    {
        private long milliseconds;
        public override DateTimeOffset GetUtcNow() => Now.AddMilliseconds(Interlocked.Increment(ref milliseconds));
        public void Advance(TimeSpan amount) => Interlocked.Add(ref milliseconds,(long)amount.TotalMilliseconds);
    }
    private sealed class Provider(Clock clock) : IMarketDataProvider
    {
        public int Calls;
        private int activeRequests;
        public int MaximumConcurrent;
        public bool Fail;
        public async Task<ProviderRead> ReadAsync(AssetIdentity asset,string capability,CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            var active = Interlocked.Increment(ref activeRequests);
            int prior;
            do { prior = MaximumConcurrent; }
            while (prior < active && Interlocked.CompareExchange(ref MaximumConcurrent,active,prior) != prior);
            try
            {
            await Task.Delay(5,token);
            var received = clock.GetUtcNow();
            var evidence = new RawEvidence(Guid.NewGuid(),"Binance",asset.BinanceSymbol,"/"+capability,received,
                200,Fail ? CollectionFailure.Timeout : CollectionFailure.None,"{}");
            if (Fail) return new(evidence,[]);
            Observation Value(Metric metric,decimal value,string method) =>
                new(asset.AssetId,metric,value,"Binance",method,Now,received,ObservationStatus.Valid,TimeQuality.ProviderTime);
            return capability switch
            {
                "Ticker" => new(evidence,[Value(Metric.Price,100,"USDT-perpetual-last"),Value(Metric.Volume24h,10_000_000,"volume")]),
                "Mark" => new(evidence,[Value(Metric.MarkPrice,100,"USDT-perpetual-mark"),Value(Metric.FundingRaw,.0001m,"USDT-perpetual-raw-current-interval")]),
                "OpenInterest" => new(evidence,[Value(Metric.OpenInterestQuantity,100,"USDT-perpetual-base-quantity")]),
                "FundingInfo" => new(evidence,[new(asset.AssetId,Metric.FundingIntervalHours,8,"Binance","fundingInfo-absent-default-8h",
                    null,received,ObservationStatus.Valid,TimeQuality.ReceivedOnly)]),
                "Candles" => new(evidence,[],Candles:Enumerable.Range(0,300).Select(i => new HourCandle(asset.AssetId,
                    Now.AddHours(i-300),Now.AddHours(i-299),received,100,101,99,100,100,60)).ToArray()),
                "OpenInterestHistory" => new(evidence,[],OpenInterestHistory:Enumerable.Range(0,289).Select(i =>
                    new OpenInterestPoint(asset.AssetId,Now.AddMinutes((i-288)*5),received,100+i,10000+i*100)).ToArray()),
                _ => throw new ArgumentException("Unsupported fixture capability.")
            };
            }
            finally { Interlocked.Decrement(ref activeRequests); }
        }
    }
    private static void InStore(Action<FoundationStore,string> action) =>
        InStoreAsync((store,folder) => { action(store,folder); return Task.CompletedTask; }).GetAwaiter().GetResult();
    private static async Task InStoreAsync(Func<FoundationStore,string,Task> action)
    {
        var folder = Path.Combine(Path.GetTempPath(),"v2-quick-"+Guid.NewGuid());
        try
        {
            var store = new FoundationStore(folder);
            store.Initialize(Now);
            await action(store,folder);
        }
        finally
        {
            foreach (var file in Directory.GetFiles(folder)) File.Delete(file);
            Directory.Delete(folder);
        }
    }
}
