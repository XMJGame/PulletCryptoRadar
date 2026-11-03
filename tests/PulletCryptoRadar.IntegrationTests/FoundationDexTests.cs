using System.Text.Json;
using PulletCryptoRadar.Host;

namespace PulletCryptoRadar.IntegrationTests;

public sealed class FoundationDexTests
{
    [Fact]
    public void RetainsQuotePoolsWithoutAttributingBaseTokenMetrics()
    {
        using var data = JsonDocument.Parse("""
            [
              {"chainId":"bsc","pairAddress":"0xA","baseToken":{"address":"0xToken"},"priceUsd":"2"},
              {"chainId":"bsc","pairAddress":"0xa","baseToken":{"address":"0xtoken"},"priceUsd":"2","liquidity":{"usd":100}},
              {"chainId":"bsc","pairAddress":"0xB","quoteToken":{"address":"0xtoken"},"priceUsd":"99","marketCap":500,"liquidity":{"usd":200}},
              {"chainId":"ethereum","pairAddress":"0xC","baseToken":{"address":"0xtoken"}}
            ]
            """);
        var pairs = MarketFoundation.ReadPairs(data.RootElement, "binance-smart-chain", "bsc", "0xToken");
        Assert.Collection(pairs, main =>
        {
            Assert.True(main.TargetIsBase);
            Assert.Equal(100m, main.LiquidityUsd);
            Assert.Equal(2m, main.PriceUsd);
        }, quote =>
        {
            Assert.False(quote.TargetIsBase);
            Assert.Equal(200m, quote.LiquidityUsd);
            Assert.Null(quote.PriceUsd);
            Assert.Null(quote.MarketCap);
        });
    }
}
