using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;
using PulletCryptoRadar.Providers.Binance;
namespace PulletCryptoRadar.IntegrationTests;

public sealed class ExecutionFoundationTests
{
    private static readonly DateTimeOffset Now=new(2026,10,31,15,0,0,TimeSpan.Zero);
    private static readonly Guid PositionId=Guid.Parse("34de797a-ab02-4d13-bf09-1902a4c53f47");
    private static ContractTradingRules Rules=>new("TESTUSDT","TRADING",.01m,.01m,100000,.01m,.01m,1000,.03m,0,1000,5,.3m,Now,null);
    private static ExecutionOrderBook Book=>new("TESTUSDT",1,Now.AddMilliseconds(-10),Now,Now,[new(99.9m,.5m),new(99.8m,10)],[new(100,.7m),new(100.2m,10)]);
    private static FundingExposure Exposure(string direction="Long")=>new(PositionId,"TESTUSDT",direction,Now.AddHours(-3),10,null,[]);
    private static FundingRateRecord Rate(decimal value=.001m,int hours=-2)=>new("TESTUSDT",Now.AddHours(hours),value,100,"Regular",Now);
    private static FundingRateWindow Window(params FundingRateRecord[] records)=>new("TESTUSDT",Now.AddHours(-3),Now.AddMinutes(-2),Now,true,records);
    [Fact]public void QuantitySatisfiesBothGridsAndBuyCostConsumesDepthRatherThanLastPrice()
    {
        var q=ExecutionCostEngine.Quote(new("TESTUSDT","Buy",1.234m,95.037m),Rules,Book,Now);
        Assert.Equal("Estimated",q.Status);Assert.Equal(1.23m,q.Quantity);Assert.Equal(.004m,q.Remainder);
        Assert.Equal((.7m*100+.53m*100.2m)/1.23m,q.Vwap);Assert.True(q.EstimatedFill>q.Vwap);
        Assert.Equal(q.Quantity*q.EstimatedFill*.0005m,q.Fee);Assert.Equal(95.03m,q.RoundedStop);Assert.True(q.PlannedLossIncludingCosts>q.Quantity*(q.EstimatedFill-q.RoundedStop));
        Assert.Equal(0,q.Quantity!.Value%.01m);Assert.Equal(0,q.Quantity.Value%.03m);
    }
    [Fact]public void SellConsumesBidsAndRoundsShortStopAwayFromEntry()
    {
        var q=ExecutionCostEngine.Quote(new("TESTUSDT","Sell",1.234m,105.127m),Rules,Book,Now);
        Assert.Equal("Estimated",q.Status);Assert.Equal((.5m*99.9m+.73m*99.8m)/1.23m,q.Vwap);Assert.True(q.EstimatedFill<q.Vwap);Assert.Equal(105.13m,q.RoundedStop);
    }
    [Fact]public void NonDividingQuantityGridsUseLeastCommonMultipleWithoutRoundingRiskUp()
    {Assert.Equal(.006m,ExecutionCostEngine.CommonStep(.003m,.002m));Assert.Equal(.003m,ExecutionCostEngine.CommonStep(.003m,0));Assert.Equal(0,ExecutionCostEngine.Floor(.005m,.006m));}
    [Fact]public void TinyQuantityAndMinimumNotionalAreBlockedButReduceOnlyNotionalExemptionIsExplicit()
    {
        Assert.Equal("Blocked",ExecutionCostEngine.Quote(new("TESTUSDT","Buy",.005m),Rules,Book,Now).Status);
        var r=Rules with{MinimumNotional=500};Assert.Equal("Blocked",ExecutionCostEngine.Quote(new("TESTUSDT","Buy",1),r,Book,Now).Status);
        Assert.Equal("Estimated",ExecutionCostEngine.Quote(new("TESTUSDT","Buy",1,ReduceOnly:true),r,Book,Now).Status);
    }
    [Fact]public void MissingStaleFutureForeignOrInsufficientBookDoesNotManufactureAFullFill()
    {
        Assert.Equal("Unknown",ExecutionCostEngine.Quote(new("TESTUSDT","Buy",1),Rules,null,Now).Status);
        Assert.Equal("Unknown",ExecutionCostEngine.Quote(new("TESTUSDT","Buy",1),Rules,Book with{TransactionAt=Now.AddMinutes(-1)},Now).Status);
        Assert.Equal("Unknown",ExecutionCostEngine.Quote(new("TESTUSDT","Buy",1),Rules,Book with{ReceivedAt=Now.AddSeconds(1)},Now).Status);
        Assert.Equal("Unknown",ExecutionCostEngine.Quote(new("TESTUSDT","Buy",1),Rules,Book with{Symbol="OTHERUSDT"},Now).Status);
        var large=ExecutionCostEngine.Quote(new("TESTUSDT","Buy",100),Rules,Book,Now);Assert.Equal("Unknown",large.Status);Assert.Null(large.EstimatedFill);Assert.Null(large.Fee);
        Assert.Equal("Blocked",ExecutionCostEngine.Quote(new("TESTUSDT","Buy",1),Rules with{ObservedAt=Now.AddSeconds(1)},Book,Now).Status);
    }
    [Fact]public void SpreadAndImpactLimitsRejectExpensiveMarketsAndCrossedOrUnorderedBooksAreInvalid()
    {
        Assert.Equal("Blocked",ExecutionCostEngine.Quote(new("TESTUSDT","Buy",1),Rules,Book with{Asks=[new(110,10)]},Now).Status);
        Assert.Equal("Blocked",ExecutionCostEngine.Quote(new("TESTUSDT","Buy",5),Rules,Book with{Asks=[new(100,.1m),new(105,10)]},Now).Status);
        Assert.Equal("Invalid",ExecutionCostEngine.Quote(new("TESTUSDT","Buy",1),Rules,Book with{Bids=[new(101,10)]},Now).Status);
        Assert.Equal("Invalid",ExecutionCostEngine.Quote(new("TESTUSDT","Buy",1),Rules,Book with{Asks=[new(100,1),new(99,1)]},Now).Status);
    }
    [Theory][InlineData("Long",.001,1)][InlineData("Short",.001,-1)][InlineData("Long",-.001,-1)][InlineData("Short",-.001,1)]
    public void FundingUsesSettlementMarkAndDirectionInsteadOfChargingBothSides(string direction,decimal rate,decimal expected)
    {
        var r=ExecutionFundingEngine.Evaluate(Exposure(direction),Window(Rate(rate)),Now,0);
        Assert.Equal("ObservedWindow",r.Status);Assert.Equal(expected,r.CompleteSignedCost);Assert.Equal(100,Assert.Single(r.Payments).MarkPrice);
    }
    [Fact]public void LateFundingCalculationReconstructsQuantityBeforeAndAfterRecordedPartialExit()
    {
        var exposure=Exposure() with{Reductions=[new(Now.AddMinutes(-90),3)]};var a=Rate();var b=Rate(hours:-1);
        var result=ExecutionFundingEngine.Evaluate(exposure,Window(a,b,a),Now,0);
        Assert.Equal(2,result.Payments.Count);Assert.Equal(10,result.Payments[0].Quantity);Assert.Equal(7,result.Payments[1].Quantity);Assert.Equal(1.7m,result.CompleteSignedCost);
    }
    [Fact]public void MissingTruncatedOrShortCoverageRemainsUnknownRatherThanZeroTotal()
    {
        Assert.Null(ExecutionFundingEngine.Evaluate(Exposure(),null,Now,0).CompleteSignedCost);
        var r=ExecutionFundingEngine.Evaluate(Exposure(),Window(Rate()) with{Complete=false},Now,0);Assert.Equal("Partial",r.Status);Assert.Equal(1,r.ObservedSignedCost);Assert.Null(r.CompleteSignedCost);
        Assert.Null(ExecutionFundingEngine.Evaluate(Exposure(),Window() with{From=Now.AddHours(-1)},Now,0).CompleteSignedCost);
        Assert.Equal("Unknown",ExecutionFundingEngine.Evaluate(Exposure() with{InitialQuantity=0},Window(),Now,0).Status);
    }
    [Fact]public void FutureWrongIdentityMissingMarkAndConflictingFundingFailClosed()
    {
        Assert.Equal("Unknown",ExecutionFundingEngine.Evaluate(Exposure(),Window(Rate() with{FundingAt=Now.AddSeconds(1)}),Now,0).Status);
        Assert.Equal("Unknown",ExecutionFundingEngine.Evaluate(Exposure(),Window(Rate() with{Symbol="OTHERUSDT"}),Now,0).Status);
        Assert.Equal("Unknown",ExecutionFundingEngine.Evaluate(Exposure(),Window(Rate() with{MarkPrice=0}),Now,0).Status);
        Assert.Equal("Unknown",ExecutionFundingEngine.Evaluate(Exposure(),Window(Rate(),Rate(.002m)),Now,0).Status);
    }
    [Fact]public void ClosedExposureIncludesFundingAtExactCloseButExcludesExactEntryAndLaterEvents()
    {
        var close=Now.AddHours(-1);var e=Exposure() with{ClosedAt=close,Reductions=[new(close,10)]};
        var result=ExecutionFundingEngine.Evaluate(e,Window(Rate() with{FundingAt=e.OpenedAt},Rate(hours:-1),Rate() with{FundingAt=Now.AddMinutes(-30)}),Now,0);
        Assert.Equal(1,Assert.Single(result.Payments).SignedCost);Assert.Equal(1,result.CompleteSignedCost);
    }
    [Fact]public void RealAuditRepositoryIsIdempotentAndConflictDoesNotChangeOriginalAccountOrEarlierEvidence()
    {
        var root=Path.Combine(Path.GetTempPath(),"PulletExecution-"+Guid.NewGuid());
        try
        {
            var options=Options.Create(new StorageOptions{Directory=root});var db=new SqliteDatabase(options);db.Initialize();
            var state=new PaperState{Revision=1,StartedAt=Now,UpdatedAt=Now};db.SavePaper(new(state,0,[]));var before=JsonSerializer.Serialize(new PaperRepository(db).Restore());
            var repo=new ExecutionEvidenceRepository(options);var payments=ExecutionFundingEngine.Evaluate(Exposure("Short"),Window(Rate()),Now,0).Payments;
            Assert.Equal(1,repo.AddFundingPayments(payments));Assert.Equal(0,repo.AddFundingPayments(payments.Select(p=>p with{ObservedAt=Now.AddMinutes(1)}).ToArray()));Assert.Equal(1,repo.FundingPaymentCount());
            var conflict=payments[0] with{Rate=.002m,SignedCost=-2};Assert.Throws<InvalidDataException>(()=>repo.AddFundingPayments([conflict]));Assert.Equal(1,repo.FundingPaymentCount());
            Assert.Equal(before,JsonSerializer.Serialize(new PaperRepository(db).Restore()));
        }
        finally{SqliteConnection.ClearAllPools();if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    private const string RulesJson="""{"symbols":[{"symbol":"TESTUSDT","quoteAsset":"USDT","contractType":"PERPETUAL","status":"TRADING","pricePrecision":0,"quantityPrecision":0,"filters":[{"filterType":"PRICE_FILTER","minPrice":"0.05","maxPrice":"100000","tickSize":"0.05"},{"filterType":"LOT_SIZE","minQty":"0.003","maxQty":"1000","stepSize":"0.003"},{"filterType":"MARKET_LOT_SIZE","minQty":"0","maxQty":"500","stepSize":"0"},{"filterType":"MIN_NOTIONAL","notional":"5"}]}]}""";
    [Fact]public void ExchangeFiltersUseActualTickAndLotNotPrecisionAndKeepIncompleteSymbolsBlocked()
    {
        using var json=JsonDocument.Parse(RulesJson);var r=Assert.Single(ExecutionParser.Rules(json.RootElement,Now).Rules);
        Assert.Equal(.05m,r.TickSize);Assert.Equal(.003m,r.StepSize);Assert.Equal(0,r.MarketStepSize);Assert.Null(r.Issue);
        using var bad=JsonDocument.Parse(RulesJson.Replace("MIN_NOTIONAL","OTHER"));Assert.NotNull(Assert.Single(ExecutionParser.Rules(bad.RootElement,Now).Rules).Issue);
    }
    [Fact]public void PricesOffTickGridAreRejectedInsteadOfAcceptingAFalseExecutableQuote()
    {Assert.Equal("Invalid",ExecutionCostEngine.Quote(new("TESTUSDT","Buy",1),Rules,Book with{Asks=[new(100.001m,10)]},Now).Status);}
    [Fact]public void SmallExchangeClockLeadIsPreservedAndQuarantinedUntilItsRawTimestampArrives()
    {
        var raw=JsonSerializer.Serialize(new{lastUpdateId=1,T=Now.AddMilliseconds(500).ToUnixTimeMilliseconds(),E=Now.AddMilliseconds(550).ToUnixTimeMilliseconds(),
            bids=new[]{new[]{"99.9","10"}},asks=new[]{new[]{"100","10"}}});using var doc=JsonDocument.Parse(raw);
        var book=ExecutionParser.Book(doc.RootElement,"TESTUSDT",Now);Assert.Equal(Now,book.ReceivedAt);Assert.True(book.OutputAt>book.ReceivedAt);
        Assert.Equal("Unknown",ExecutionCostEngine.Quote(new("TESTUSDT","Buy",1),Rules,book,Now).Status);
        Assert.Equal("Estimated",ExecutionCostEngine.Quote(new("TESTUSDT","Buy",1),Rules,book,Now.AddMilliseconds(600)).Status);
        using var future=JsonDocument.Parse(raw.Replace(Now.AddMilliseconds(550).ToUnixTimeMilliseconds().ToString(),Now.AddSeconds(5).ToUnixTimeMilliseconds().ToString()));
        Assert.Throws<InvalidDataException>(()=>ExecutionParser.Book(future.RootElement,"TESTUSDT",Now));
    }
    private sealed class Provider: IExecutionEvidenceProvider
    {
        public int Calls;public bool FailBook;
        public Task<ContractRulesSnapshot> GetTradingRulesAsync(CancellationToken ct){Calls++;return Task.FromResult(new ContractRulesSnapshot(Now,[Rules]));}
        public Task<ExecutionOrderBook> GetOrderBookAsync(string symbol,CancellationToken ct){Calls++;if(FailBook)throw new HttpRequestException("fixture");return Task.FromResult(Book);}
        public Task<FundingRateWindow> GetFundingHistoryAsync(string symbol,DateTimeOffset from,DateTimeOffset through,CancellationToken ct){Calls++;return Task.FromResult(new FundingRateWindow(symbol,from,through,Now,true,[]));}
        public Task<FundingIntervalSnapshot> GetFundingIntervalsAsync(CancellationToken ct){Calls++;return Task.FromResult(new FundingIntervalSnapshot(Now,[]));}
    }
    [Fact]public async Task EvidenceServiceMetersRefreshesAndStatusOrPreviewNeverTriggerExternalRequests()
    {
        var root=Path.Combine(Path.GetTempPath(),"PulletExecutionService-"+Guid.NewGuid());
        try
        {
            var provider=new Provider();var repo=new ExecutionEvidenceRepository(Options.Create(new StorageOptions{Directory=root}));
            var service=new ExecutionEvidenceService(provider,repo,new Clock(),new(){PrioritySymbols=["TESTUSDT"]});var state=new PaperState{Revision=1,StartedAt=Now,UpdatedAt=Now};
            var before=JsonSerializer.Serialize(state);await service.RefreshAsync(state,[],[],default);Assert.Equal(4,provider.Calls);Assert.True(service.CollectionHealthy);
            await service.RefreshAsync(state,[],[],default);Assert.Equal(4,provider.Calls);
            var report=service.Status(state,[]);Assert.Equal("ShadowAudit",report.Mode);Assert.Equal(1,report.RulesCount);Assert.Null(report.Symbols[0].AdjustedFundingIntervalHours);
            service.Preview(new("TESTUSDT","Buy",1));service.Status(state,[]);Assert.Equal(4,provider.Calls);Assert.Equal(before,JsonSerializer.Serialize(state));
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    [Fact]public async Task AFailedBookRetainsUnknownAndDoesNotAdvertiseCompleteCollection()
    {
        var root=Path.Combine(Path.GetTempPath(),"PulletExecutionFail-"+Guid.NewGuid());
        try
        {
            var provider=new Provider{FailBook=true};var service=new ExecutionEvidenceService(provider,new ExecutionEvidenceRepository(Options.Create(new StorageOptions{Directory=root})),new Clock(),new(){PrioritySymbols=["TESTUSDT"]});
            var state=new PaperState{Revision=1,StartedAt=Now,UpdatedAt=Now};await service.RefreshAsync(state,[],[],default);
            Assert.False(service.CollectionHealthy);Assert.Equal("Unknown",service.Preview(new("TESTUSDT","Buy",1)).Status);Assert.NotNull(service.Status(state,[]).Symbols[0].Error);
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    [Fact]public void FundingParserDeduplicatesIdenticalRecordsAndMarksFullPageTruncated()
    {
        var item=new{symbol="TESTUSDT",fundingTime=Now.AddHours(-1).ToUnixTimeMilliseconds(),fundingRate="0.001",markPrice="100"};
        using var doc=JsonDocument.Parse(JsonSerializer.Serialize(new[]{item,item}));Assert.Single(ExecutionParser.Funding(doc.RootElement,"TESTUSDT",Now.AddHours(-2),Now,Now).Records);
        using var large=JsonDocument.Parse(JsonSerializer.Serialize(Enumerable.Repeat(item,1000)));Assert.False(ExecutionParser.Funding(large.RootElement,"TESTUSDT",Now.AddHours(-2),Now,Now).Complete);
    }
    private sealed class Clock:TimeProvider{public override DateTimeOffset GetUtcNow()=>Now;}
    private sealed class Limiter:IRequestLimiter
    {public List<string> Keys {get;}=[];public Task WaitAsync(string provider,int weight,CancellationToken ct){Keys.Add(provider);return Task.CompletedTask;}public void Pause(string provider,TimeSpan duration){}}
    private sealed class Handler(string json,DateTimeOffset date):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(json,Encoding.UTF8,"application/json"),Headers={Date=date}});}
    [Fact]public async Task ExecutionHttpRejectsStaleSourceDateAndFundingUsesSharedQuota()
    {
        var limiter=new Limiter();var health=new ProviderHealthStore(new Clock());
        var stale=new BinanceRestClient(new HttpClient(new Handler(RulesJson,Now.AddHours(-1))){BaseAddress=new("https://example.test")},limiter,health,new Clock());
        await Assert.ThrowsAsync<InvalidDataException>(()=>stale.GetTradingRulesAsync(default));
        var client=new BinanceRestClient(new HttpClient(new Handler("[]",Now)){BaseAddress=new("https://example.test")},limiter,health,new Clock());
        var window=await client.GetFundingHistoryAsync("TESTUSDT",Now.AddHours(-1),Now,default);Assert.True(window.Complete);Assert.Empty(window.Records);Assert.Contains("BinanceFunding",limiter.Keys);
    }
}
