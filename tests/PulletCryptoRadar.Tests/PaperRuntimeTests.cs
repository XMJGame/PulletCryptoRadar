using System.Text.Json;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Host;
using PulletCryptoRadar.Infrastructure;
using PulletCryptoRadar.Trading;

namespace PulletCryptoRadar.Tests;

public sealed class PaperRuntimeTests
{
    private static readonly DateTimeOffset Now = new(2026,11,3,10,0,0,TimeSpan.Zero);
    private static readonly AssetIdentity Asset = new(Guid.NewGuid(),"TEST","Test","TESTUSDT",null,null,[],Now);
    private static readonly TradingLimits Limits = new();
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset At = Now;
        public override DateTimeOffset GetUtcNow() => At;
    }
    private static ProviderRead Book(DateTimeOffset at,long sequence = 1,decimal bid = 99.99m) =>
        new(new(Guid.NewGuid(),"Binance","TESTUSDT","wss:/public/testusdt@depth5@500ms",at,null,CollectionFailure.None,"{}"),[],
            OrderBook:new(Asset.AssetId,sequence,at,at,at,[new(bid,1000)],[new(bid+.01m,1000)]));
    private static ProviderRead Mark(DateTimeOffset at,decimal price = 100,DateTimeOffset? next = null) =>
        new(new(Guid.NewGuid(),"Binance","TESTUSDT","wss:/market/testusdt@markPrice@1s",at,null,CollectionFailure.None,"{}"),[],
            MarketMark:new(Asset.AssetId,price,.0001m,next ?? at.AddHours(2),at,at));
    private static void Prepare(FoundationStore store)
    {
        store.Register(Asset);
        var rules = new ContractRules("TESTUSDT",.01m,1000,.01m,1,.01m);
        var universe = new ProviderRead(new(Guid.NewGuid(),"Binance","*USDT-PERPETUAL*","/fapi/v1/exchangeInfo",Now,
            200,CollectionFailure.None,"{}"),[],Universe:[new("TESTUSDT","TEST",Now)],ContractRules:[rules]);
        store.SaveMarketContext(new("test",Now,MarketState.Bull,100,100,1,.8m,1,[]),[universe]);
        var metrics = new[] { Metric.Ema20H1,Metric.Ema60H1,Metric.Atr14H1,Metric.VolumeRatioH1,Metric.TakerBuyShareH1,
            Metric.OiQuantityChange1h,Metric.Ema20H4,Metric.Ema60H4 }.Select(metric =>
            new ResolvedMetric(metric,ResolutionStatus.SingleSource,1,"Binance",true,[new(Asset.AssetId,metric,1,"Binance","test",Now,Now,
                ObservationStatus.Valid,TimeQuality.ProviderTime)])).ToArray();
        store.Save(new("test",Asset.AssetId,Now,MarketState.Bull,metrics),[]);
        var setup = new EntrySetup(Guid.NewGuid(),Asset.AssetId,EntryStrategy.TrendPullback,TradeSide.Long,SetupPhase.PatternConfirmed,
            Now.AddHours(-1),Now.AddHours(-1),Now.AddHours(3),100,101,99,1,Now.AddSeconds(-1),Now.AddHours(-1));
        store.SaveEntry(new(EntryEngine.Version,Asset.AssetId,setup.Strategy,setup.Side,Now,"PatternConfirmed",false,setup,[],Now,Now,null));
        var funding = new ProviderRead(new(Guid.NewGuid(),"Binance","TESTUSDT","/fundingRate",Now,200,CollectionFailure.None,"{}"),[],
            FundingSettlements:[new(Asset.AssetId,Now.AddHours(-2),Now,.0001m,100,"Regular")]);
        store.SaveExecution(new(Asset.AssetId,Now,Book(Now),funding));
    }
    [Fact]
    public void HealthyStreamsAdmitOnePaperPositionThenRequireFundingReconciliation()
    {
        InStore((store,_) =>
        {
            Prepare(store); var clock = new Clock();
            var runtime = new PaperRuntimeCoordinator(store,new() { Enabled = true },Limits,clock);
            runtime.Session([Asset]); runtime.Ingest(Book(Now));
            Assert.Empty(store.Portfolio().Positions);
            runtime.Ingest(Mark(Now));
            Assert.Single(store.Portfolio().Positions);
            Assert.False(runtime.Snapshot().AdmissionEnabled);
            Assert.Equal("FundingReconciliationPending",runtime.Snapshot().Detail);
            runtime.FundingVerified(Asset.AssetId,Now);
            Assert.True(runtime.Snapshot().AdmissionEnabled);
            clock.At = Now.AddSeconds(1); runtime.Ingest(Book(clock.At,2)); runtime.Ingest(Mark(clock.At));
            Assert.Single(store.Portfolio().Positions);
            Assert.Single(store.PaperEvents(),item => item.Type == "Open");
            Assert.True(store.AuditPaper().CashReconciled);
        });
    }
    [Fact]
    public void StaleDisconnectedAndPersistentIssueStatesDisableNewAdmission()
    {
        InStore((store,folder) =>
        {
            Prepare(store); var clock = new Clock();
            var runtime = new PaperRuntimeCoordinator(store,new() { Enabled = true },Limits,clock);
            runtime.Session([Asset]); store.SetPaperIssue("funding-review","FundingBoundaryAmbiguous",Now);
            runtime.Ingest(Book(Now)); runtime.Ingest(Mark(Now));
            Assert.Empty(store.Portfolio().Positions);
            Assert.False(runtime.Snapshot().AdmissionEnabled);
            Assert.Single(new FoundationStore(folder).PaperIssues());
            clock.At = Now.AddSeconds(4);
            Assert.Equal(0,runtime.Snapshot().FreshBooks); Assert.Equal(0,runtime.Snapshot().FreshMarks);
            runtime.Disconnected("TestDisconnect");
            Assert.Equal("Disconnected",runtime.Snapshot().State);
            Assert.False(runtime.Snapshot().AdmissionEnabled);
        });
    }
    [Fact]
    public void DisabledAdmissionStillExitsExistingPositionAtNewQuote()
    {
        InStore((store,_) =>
        {
            Prepare(store); var clock = new Clock();
            var enabled = new PaperRuntimeCoordinator(store,new() { Enabled = true },Limits,clock);
            enabled.Session([Asset]); enabled.Ingest(Book(Now)); enabled.Ingest(Mark(Now));
            Assert.True(store.Portfolio().Positions[0].RemainingQuantity > 0);
            var disabled = new PaperRuntimeCoordinator(store,new() { Enabled = false },Limits,clock);
            disabled.Session([Asset]); clock.At = Now.AddSeconds(1);
            disabled.Ingest(Book(clock.At,2,98));
            Assert.Equal(0,store.Portfolio().Positions[0].RemainingQuantity);
            Assert.False(disabled.Snapshot().AdmissionEnabled);
            Assert.True(store.AuditPaper().CashReconciled);
        });
    }
    [Fact]
    public void MarkMarginSafetyExitUsesAvailableBookAndRecordsItsReason()
    {
        InStore((store,_) =>
        {
            Prepare(store); var clock = new Clock();
            var runtime = new PaperRuntimeCoordinator(store,new() { Enabled = true },Limits,clock);
            runtime.Session([Asset]); runtime.Ingest(Book(Now)); runtime.Ingest(Mark(Now));
            clock.At = Now.AddSeconds(1); runtime.Ingest(Mark(clock.At,96));
            Assert.Equal(0,store.Portfolio().Positions[0].RemainingQuantity);
            Assert.Contains(store.PaperEvents(),item => item.Type == "Reduce" && item.Reason == "PaperMarginSafetyExit");
            Assert.True(store.AuditPaper().CashReconciled);
        });
    }
    [Fact]
    public void FundingScheduleRolloverInvalidatesEarlierReconciliationProof()
    {
        InStore((store,_) =>
        {
            Prepare(store); var clock = new Clock(); var runtime = new PaperRuntimeCoordinator(store,new() { Enabled = true },Limits,clock);
            runtime.Session([Asset]); runtime.Ingest(Book(Now)); runtime.Ingest(Mark(Now));
            clock.At = Now.AddHours(2); runtime.Ingest(Book(clock.At,2)); runtime.FundingVerified(Asset.AssetId,clock.At);
            runtime.Ingest(Mark(clock.At));
            Assert.False(runtime.Snapshot().AdmissionEnabled);
            Assert.Equal("FundingReconciliationPending",runtime.Snapshot().Detail);
        });
    }

    [Fact]
    public void NearFundingRejectionCanRetryOnLaterEvidenceWithoutConsumingSetup()
    {
        InStore((store,_) =>
        {
            Prepare(store); var clock = new Clock();
            var runtime = new PaperRuntimeCoordinator(store,new() { Enabled = true },Limits,clock);
            runtime.Session([Asset]); runtime.Ingest(Book(Now)); runtime.Ingest(Mark(Now,next:Now.AddSeconds(30)));
            Assert.Empty(store.Portfolio().Positions);
            clock.At = Now.AddSeconds(1); runtime.Ingest(Mark(clock.At));
            Assert.Single(store.Portfolio().Positions);
        });
    }
    [Fact]
    public void MarginBufferRejectsWideStopsForBothDirections()
    {
        InStore((store,_) =>
        {
            Prepare(store); var evidence = store.LatestExecution(Asset.AssetId)!;
            foreach (var side in new[] { TradeSide.Long,TradeSide.Short })
            {
                var setup = store.LatestEntry(Asset.AssetId,EntryStrategy.TrendPullback,TradeSide.Long)!.Setup! with
                    { Side = side,StructuralStop = side == TradeSide.Long ? 99 : 101 };
                var rules = store.Universe()!.ContractRules![0];
                var narrow = new SizingEngine().Evaluate(Asset,setup,evidence,rules,1000,1000,Limits,new("costs"),Now);
                Assert.True(PaperMarginModel.StopHasBuffer(narrow,new()));
                var wide = new SizingEngine().Evaluate(Asset,setup with { StructuralStop = side == TradeSide.Long ? 95 : 105 },
                    evidence,rules,1000,1000,Limits,new("costs"),Now);
                Assert.False(PaperMarginModel.StopHasBuffer(wide,new()));
            }
        });
    }

    [Fact]
    public void ConservativeFundingBoundaryDebitsMaximumAndDoesNotInventUncertainCredits()
    {
        InStore((store,_) =>
        {
            Prepare(store); var clock = new Clock(); var runtime = new PaperRuntimeCoordinator(store,new() { Enabled = true },Limits,clock);
            runtime.Session([Asset]); runtime.Ingest(Book(Now)); runtime.Ingest(Mark(Now));
            var portfolio = store.Portfolio(); var position = portfolio.Positions[0]; var history = store.PaperEvents(position.PositionId);
            var engine = new PaperPortfolioEngine();
            var settlement = new FundingSettlement(Asset.AssetId,Now.AddSeconds(5),Now.AddSeconds(30),.001m,100,"Regular");
            var debit = engine.ConservativeFundingBoundary(portfolio,position.PositionId,settlement,history,Guid.NewGuid(),Now.AddSeconds(30));
            Assert.Equal("FundingEstimateApplied",debit.Status);
            var ledger = Assert.Single(debit.Events);
            Assert.Equal(-position.InitialQuantity*.1m,ledger.CashDelta);
            Assert.Equal("ConservativeBoundaryEstimate",ledger.Reason);
            Assert.NotNull(ledger.AssumptionVersion);
            var credit = engine.ConservativeFundingBoundary(portfolio,position.PositionId,settlement with { Rate = -.001m },
                history,Guid.NewGuid(),Now.AddSeconds(30));
            Assert.Equal(0,Assert.Single(credit.Events).CashDelta);
            Assert.Equal("FundingAlreadyApplied",engine.ConservativeFundingBoundary(debit.Portfolio,position.PositionId,settlement,
                history.Concat(debit.Events).ToArray(),Guid.NewGuid(),Now.AddSeconds(40)).Status);
        });
    }

    [Fact]
    public void StreamParserPreservesBoundIdentityAndRejectsWrongSymbolTypeOrFutureTime()
    {
        object Depth(string symbol = "TESTUSDT",int type = 1,long? at = null) => new
        {
            stream = "testusdt@depth5@500ms",data = new { e = "depthUpdate",s = symbol,st = type,u = 42,
                E = at ?? Now.ToUnixTimeMilliseconds(),T = at ?? Now.ToUnixTimeMilliseconds(),
                b = new[] { new[] { "99.99","10" } },a = new[] { new[] { "100","10" } } }
        };
        var parsed = PositionMarketStream.Parse([Asset],JsonSerializer.Serialize(Depth()),Now,"public");
        Assert.Equal(42,parsed.OrderBook!.UpdateId); Assert.Equal(Asset.AssetId,parsed.OrderBook.AssetId);
        Assert.Throws<InvalidDataException>(() => PositionMarketStream.Parse([Asset],JsonSerializer.Serialize(Depth("OTHERUSDT")),Now,"public"));
        Assert.Throws<InvalidDataException>(() => PositionMarketStream.Parse([Asset],JsonSerializer.Serialize(Depth(type:2)),Now,"public"));
        Assert.Throws<InvalidDataException>(() => PositionMarketStream.Parse([Asset],JsonSerializer.Serialize(Depth(at:Now.AddSeconds(1).ToUnixTimeMilliseconds())),Now,"public"));
        var mark = JsonSerializer.Serialize(new { stream = "testusdt@markPrice@1s",data = new { e = "markPriceUpdate",s = "TESTUSDT",
            E = Now.ToUnixTimeMilliseconds(),T = Now.AddHours(4).ToUnixTimeMilliseconds(),p = "100",r = "-0.001" } });
        Assert.Equal(-.001m,PositionMarketStream.Parse([Asset],mark,Now,"market").MarketMark!.FundingRate);
    }
    [Fact]
    public async Task PositionFundingRequestsStartFromCursorInsteadOfOnlyLatestEightRows()
    {
        var folder = Path.Combine(Path.GetTempPath(),"v2-funding-cursor-"+Guid.NewGuid());
        try
        {
            var store = new FoundationStore(folder); store.Initialize(Now);
            var handler = new FundingHandler(); using var http = new HttpClient(handler);
            var result = await new BinanceMarketProvider(http,store,new Clock()).ReadAsync(Asset,
                "PositionFunding:"+Now.AddDays(-3).ToUnixTimeMilliseconds(),default);
            Assert.Equal(CollectionFailure.None,result.Evidence.Failure);
            Assert.Contains("limit=1000",handler.Query);
            Assert.Contains("startTime="+Now.AddDays(-3).ToUnixTimeMilliseconds(),handler.Query);
        }
        finally { foreach (var file in Directory.GetFiles(folder)) File.Delete(file); Directory.Delete(folder); }
    }
    private sealed class FundingHandler : HttpMessageHandler
    {
        public string? Query;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            Query = request.RequestUri!.Query;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("[]") });
        }
    }
    [Fact]
    public void StreamReservationsSharePersistedBodyBudgetAndFrameSavingIsIdempotent()
    {
        InStore((store,folder) =>
        {
            Prepare(store); var id = Guid.NewGuid();
            Assert.True(store.Reserve(id,"BinanceStream",Now,2_000_000)); store.Complete(id,1234);
            var restarted = new FoundationStore(folder); var read = Book(Now);
            restarted.SavePaperMarket(read); restarted.SavePaperMarket(read);
            var status = JsonSerializer.Serialize(restarted.CollectionStatus());
            Assert.Contains("rawResponses",status);
            Assert.True(restarted.Reserve(Guid.NewGuid(),"BinanceStream",Now.AddSeconds(1),2_000_000));
            restarted.SaveFundingCursor(Asset.AssetId,Now); restarted.SaveFundingCursor(Asset.AssetId,Now.AddHours(-1));
            Assert.Equal(Now,restarted.FundingCursor(Asset.AssetId));
        });
    }
    private static void InStore(Action<FoundationStore,string> action)
    {
        var folder = Path.Combine(Path.GetTempPath(),"v2-runtime-"+Guid.NewGuid());
        try { var store = new FoundationStore(folder); store.Initialize(Now); action(store,folder); }
        finally { foreach (var file in Directory.GetFiles(folder)) File.Delete(file); Directory.Delete(folder); }
    }
}
