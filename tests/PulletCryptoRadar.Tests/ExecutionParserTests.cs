using System.Text.Json;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;

namespace PulletCryptoRadar.Tests;

public sealed class ExecutionParserTests
{
    private static readonly DateTimeOffset Now = new(2026,11,3,8,0,0,TimeSpan.Zero);
    private static readonly AssetIdentity Asset = new(Guid.NewGuid(),"TEST","Test","TESTUSDT",null,null,[],Now);
    private static string Book(string[][]? bids = null,string[][]? asks = null,DateTimeOffset? at = null) => JsonSerializer.Serialize(new
    {
        lastUpdateId = 1, E = Now.ToUnixTimeMilliseconds(), T = (at ?? Now).ToUnixTimeMilliseconds(),
        bids = bids ?? [["99","2"],["98","3"]],asks = asks ?? [["100","2"],["101","3"]]
    });
    [Fact]
    public void BookWithoutSymbolIsBoundToRequestedContractAndTimeIsPreserved()
    {
        var book = BinanceMarketProvider.ParseOrderBook(Asset,Book(),Now);
        Assert.Equal(Asset.AssetId,book.AssetId);
        Assert.Equal(Now,book.SourceTime);
        Assert.Equal(100,book.Asks[0].Price);
    }
    [Theory]
    [InlineData("crossed")]
    [InlineData("unordered")]
    [InlineData("zero")]
    [InlineData("empty")]
    [InlineData("future")]
    [InlineData("old")]
    public void BookParserRejectsInvalidDepthOrTime(string condition)
    {
        var body = condition switch
        {
            "crossed" => Book(bids:[["100","1"]]),
            "unordered" => Book(asks:[["101","1"],["100","1"]]),
            "zero" => Book(asks:[["100","0"]]),
            "empty" => Book(asks:[]),
            "future" => Book(at:Now.AddSeconds(1)),
            _ => Book(at:Now.AddSeconds(-6))
        };
        Assert.Throws<InvalidDataException>(() => BinanceMarketProvider.ParseOrderBook(Asset,body,Now));
    }
    [Fact]
    public void FundingParserKeepsActualTimesSignsTypesAndMissingMark()
    {
        object Row(DateTimeOffset at,string rate,string? mark,string type = "Regular") => new
        { symbol = "TESTUSDT",fundingTime = at.ToUnixTimeMilliseconds(),fundingRate = rate,markPrice = mark,rateType = type };
        var values = BinanceMarketProvider.ParseFundingHistory(Asset,JsonSerializer.Serialize(new[] {
            Row(Now.AddHours(-8),"-0.001","100"),Row(Now,"0.001",null,"Special") }),Now);
        Assert.Equal(-.001m,values[0].Rate);
        Assert.Equal(Now.AddHours(-8),values[0].SettledAt);
        Assert.Null(values[1].MarkPrice);
        Assert.Equal("Special",values[1].RateType);
        Assert.Throws<InvalidDataException>(() => BinanceMarketProvider.ParseFundingHistory(Asset,
            JsonSerializer.Serialize(new[] { Row(Now.AddSeconds(1),"0.01","100") }),Now));
        Assert.Throws<InvalidDataException>(() => BinanceMarketProvider.ParseFundingHistory(Asset,
            JsonSerializer.Serialize(new[] { Row(Now,"0.01","100"),Row(Now,"0.01","100") }),Now));
    }
    [Fact]
    public void ContractRulesUseMarketStepNotPrecisionAndBadRulesDoNotDiscardOtherSymbols()
    {
        object Row(string symbol,string step) => new
        {
            symbol,status = "TRADING",contractType = "PERPETUAL",quoteAsset = "USDT",marginAsset = "USDT",
            quantityPrecision = 9,filters = new object[] {
                new { filterType = "MARKET_LOT_SIZE",minQty = "0.01",maxQty = "1000",stepSize = step },
                new { filterType = "MIN_NOTIONAL",notional = "5" },new { filterType = "PRICE_FILTER",tickSize = "0.1" } }
        };
        var rules = BinanceMarketProvider.ParseContractRules(JsonSerializer.Serialize(new { symbols = new[] { Row("TESTUSDT","0.01"),Row("BADUSDT","bad") } }));
        Assert.Equal(.01m,Assert.Single(rules).StepSize);
        Assert.Equal(.1m,rules[0].PriceTick);
    }
}
