using System.Text.Json;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;
using PulletCryptoRadar.Trading;

namespace PulletCryptoRadar.Tests;

public sealed class ExecutionTests
{
    private static readonly DateTimeOffset Now = new(2026,11,3,8,0,0,TimeSpan.Zero);
    private static readonly AssetIdentity Asset = new(Guid.NewGuid(),"TEST","Test","TESTUSDT",null,null,[],Now);
    private static readonly ContractRules Rules = new("TESTUSDT",.001m,10000,.001m,5,.01m);
    private static EntrySetup Setup(TradeSide side = TradeSide.Long) => new(Guid.NewGuid(),Asset.AssetId,EntryStrategy.TrendPullback,side,
        SetupPhase.PatternConfirmed,Now.AddHours(-1),Now.AddHours(-1),Now.AddHours(3),100,101,side == TradeSide.Long ? 95 : 105,2,
        Now.AddSeconds(-1),Now.AddHours(-1));
    private static ExecutionEvidence Evidence()
    {
        var book = new OrderBook(Asset.AssetId,1,Now,Now,Now,[new(99.99m,.1m),new(99.98m,100)],[new(100.01m,.1m),new(100.02m,100)]);
        ProviderRead Read(string path) => new(new(Guid.NewGuid(),"Binance","TESTUSDT",path,Now,200,CollectionFailure.None,"{}"),[]);
        return new(Asset.AssetId,Now,Read("/fapi/v1/depth") with { OrderBook = book },Read("/fapi/v1/fundingRate") with
        {
            FundingSettlements = [new(Asset.AssetId,Now.AddHours(-8),Now,.0001m,100,"Regular")]
        });
    }
    private static SizingProposal Size(ExecutionEvidence? evidence = null,EntrySetup? setup = null,ContractRules? rules = null,
        decimal cash = 1000,SizingAssumptions? assumptions = null) => new SizingEngine().Evaluate(Asset,setup ?? Setup(),
            evidence ?? Evidence(),rules ?? Rules,1000,cash,new(),assumptions ?? new("test-costs"),Now);

    [Theory]
    [InlineData(TradeSide.Long)]
    [InlineData(TradeSide.Short)]
    public void SizesFromStopAndCostsWithinRiskCashAndExchangeStep(TradeSide side)
    {
        var result = Size(setup:Setup(side));
        Assert.Contains("MarketAndSingleTradeSizingChecksPassed",result.Reasons);
        Assert.True(result.Quantity > 0);
        Assert.Equal(0,result.Quantity % Rules.StepSize);
        Assert.True(result.PlannedLossIncludingCosts <= 2.5m);
        Assert.True(result.Margin <= 100);
        Assert.True(result.EntrySlippageFraction > 0);
        Assert.False(result.ExecutionAllowed);
        Assert.Contains("LiquidationModelUnverified",result.Reasons);
        Assert.Contains("FeeScheduleAssumed",result.Reasons);
    }

    [Fact]
    public void WiderStopOrHigherFeesReduceQuantityAndCashIncludesFeeAndFundingReserve()
    {
        var setup = Setup();
        var normal = Size(setup:setup);
        var wide = Size(setup:setup with { StructuralStop = 90 });
        var expensive = Size(setup:setup,assumptions:new("more-costs",FeePerSide:.003m));
        Assert.True(wide.Quantity < normal.Quantity);
        Assert.True(expensive.Quantity < normal.Quantity);
        var lowCash = Size(setup:setup with { StructuralStop = 99.9m },cash:1);
        Assert.True(lowCash.Margin+lowCash.Notional*(.0006m+.001m) <= 1);
        Assert.Null(Size(cash:0).Quantity);
        Assert.Null(Size(rules:Rules with { MinimumNotional = 1000 }).Quantity);
    }

    [Theory]
    [InlineData("old")]
    [InlineData("beforeConfirmation")]
    [InlineData("future")]
    [InlineData("foreign")]
    [InlineData("crossed")]
    public void UnsafeBooksNeverProduceSizing(string condition)
    {
        var evidence = Evidence();
        var book = evidence.Book.OrderBook!;
        book = condition switch
        {
            "old" => book with { SourceTime = Now.AddSeconds(-3) },
            "beforeConfirmation" => book with { SourceTime = Now.AddSeconds(-2) },
            "future" => book with { SourceTime = Now.AddSeconds(1) },
            "foreign" => book with { AssetId = Guid.NewGuid() },
            _ => book with { Bids = [new(101,100)] }
        };
        var result = Size(evidence with { Book = evidence.Book with { OrderBook = book } });
        Assert.Null(result.Quantity);
        Assert.Contains("FreshPostConfirmationBookUnavailable",result.Reasons);
    }

    [Fact]
    public void RejectsThinBookSpreadAndExcessSlippage()
    {
        var evidence = Evidence();
        var book = evidence.Book.OrderBook!;
        SizingProposal WithAsks(params BookLevel[] levels) => Size(evidence with { Book = evidence.Book with { OrderBook = book with { Asks = levels } } });
        Assert.Contains("DisplayedDepthInsufficient",WithAsks(new BookLevel(100.01m,.001m)).Reasons);
        Assert.Contains("SpreadTooWide",WithAsks(new BookLevel(102,100)).Reasons);
        Assert.Contains("EntrySlippageTooLarge",WithAsks(new BookLevel(100.01m,.001m),new BookLevel(101,100)).Reasons);
    }

    [Fact]
    public void FundingFailureOrExpiredConfirmationOrWrongStopBlocksProposal()
    {
        var evidence = Evidence();
        Assert.Null(Size(evidence with { Funding = evidence.Funding with { FundingSettlements = [] } }).Quantity);
        Assert.Null(Size(setup:Setup() with { ConfirmedAt = Now.AddMinutes(-2) }).Quantity);
        Assert.Null(Size(setup:Setup() with { StructuralStop = 110 }).Quantity);
        var adverse = evidence.Funding.FundingSettlements!.Select(item => item with { Rate = .01m }).ToArray();
        Assert.Contains("AdverseSettledFundingAboveResearchLimit",Size(evidence with { Funding = evidence.Funding with { FundingSettlements = adverse } }).Reasons);
    }

    [Theory]
    [InlineData(TradeSide.Long,.001,-1)]
    [InlineData(TradeSide.Short,.001,1)]
    [InlineData(TradeSide.Long,-.001,1)]
    [InlineData(TradeSide.Short,-.001,-1)]
    public void FundingCashflowUsesActualSettlementSignAndNotional(TradeSide side,double rate,double expected)
    {
        var settlement = new FundingSettlement(Asset.AssetId,Now,Now,(decimal)rate,100,"Regular");
        var result = SizingEngine.SettledCashflow(Asset.AssetId,side,10,Now.AddMinutes(-1),null,settlement,Now);
        Assert.Equal("Calculated",result.Status);
        Assert.Equal((decimal)expected,result.Cashflow);
    }

    [Fact]
    public void FundingDoesNotChargeOutsideHoldingWindowAndRejectsBoundaryAmbiguityOrFutureReceipt()
    {
        var settled = new FundingSettlement(Asset.AssetId,Now,Now,.001m,100,"Regular");
        FundingCashflow Charge(DateTimeOffset entered,DateTimeOffset? exit = null,FundingSettlement? item = null) =>
            SizingEngine.SettledCashflow(Asset.AssetId,TradeSide.Long,10,entered,exit,item ?? settled,Now.AddMinutes(2));
        Assert.Equal(0,Charge(Now.AddMinutes(1)).Cashflow);
        Assert.Equal(0,Charge(Now.AddMinutes(-2),Now.AddMinutes(-1)).Cashflow);
        Assert.Equal("BoundaryAmbiguous",Charge(Now).Status);
        Assert.Equal("BoundaryAmbiguous",Charge(Now.AddMinutes(-1),Now).Status);
        Assert.Equal("BoundaryAmbiguous",Charge(Now.AddSeconds(5)).Status);
        Assert.Equal("InvalidEvidence",Charge(Now.AddMinutes(-1),item:settled with { ReceivedTime = Now.AddMinutes(3) }).Status);
        Assert.Null(Charge(Now.AddMinutes(-1),item:settled with { MarkPrice = null }).Cashflow);
    }

    [Fact]
    public async Task ConfirmedPatternRefreshRecordsEvidenceAndSizingWithoutChangingAccount()
    {
        var folder = Path.Combine(Path.GetTempPath(),"v2-execution-service-"+Guid.NewGuid());
        try
        {
            var store = new FoundationStore(folder);
            store.Initialize(Now); store.Register(Asset);
            var universe = new ProviderRead(new(Guid.NewGuid(),"Binance","*USDT-PERPETUAL*","/fapi/v1/exchangeInfo",Now,
                200,CollectionFailure.None,"{}"),[],Universe:[new("TESTUSDT","TEST",Now)],ContractRules:[Rules]);
            store.SaveMarketContext(new("test",Now,MarketState.Mixed,100,100,1,.5m,0,[]),[universe]);
            var setup = Setup();
            store.SaveEntry(new(EntryEngine.Version,Asset.AssetId,setup.Strategy,setup.Side,Now.AddSeconds(-1),
                "PatternConfirmed",false,setup,[],null,null,null));
            var provider = new EvidenceProvider();
            var service = new ExecutionService(provider,store,store,store,new(),new FixedClock());
            Assert.Equal("EvidenceAndSizingRecorded",await service.RefreshAsync(Asset.AssetId,default));
            Assert.Equal(2,provider.Calls);
            var proposal = Assert.Single(store.SizingHistory(Asset.AssetId));
            Assert.True(proposal.Quantity > 0);
            Assert.Equal(setup.SetupId,proposal.SetupId);
            Assert.False(proposal.ExecutionAllowed);
            Assert.Equal(1000,store.Account().Cash);
            Assert.Equal("CoolingDown",await service.RefreshAsync(Asset.AssetId,default));
            Assert.Equal(2,provider.Calls);
            Assert.Equal("UnknownAsset",await service.RefreshAsync(Guid.NewGuid(),default));
        }
        finally
        {
            foreach (var file in Directory.GetFiles(folder)) File.Delete(file);
            Directory.Delete(folder);
        }
    }

    private sealed class FixedClock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class EvidenceProvider : IMarketDataProvider
    {
        public int Calls;
        public Task<ProviderRead> ReadAsync(AssetIdentity asset,string capability,CancellationToken token)
        {
            Calls++;
            var evidence = Evidence();
            return Task.FromResult(capability == "OrderBook" ? evidence.Book : evidence.Funding);
        }
    }

    [Fact]
    public void ExecutionEvidenceAndProposalSurviveRestartAndReplayExactly()
    {
        var folder = Path.Combine(Path.GetTempPath(),"v2-execution-"+Guid.NewGuid());
        try
        {
            var store = new FoundationStore(folder);
            store.Initialize(Now); store.Register(Asset);
            var evidence = Evidence(); var setup = Setup(); var proposal = Size(evidence,setup);
            store.SaveExecution(evidence); store.SaveSizing(proposal);
            store = new FoundationStore(folder);
            var restored = store.LatestExecution(Asset.AssetId)!;
            Assert.Equal(JsonSerializer.Serialize(proposal),JsonSerializer.Serialize(Size(restored,setup)));
            Assert.Single(store.SizingHistory(Asset.AssetId));
            Assert.Throws<ArgumentException>(() => store.SaveSizing(proposal with { ExecutionAllowed = true }));
        }
        finally
        {
            foreach (var file in Directory.GetFiles(folder)) File.Delete(file);
            Directory.Delete(folder);
        }
    }
}
