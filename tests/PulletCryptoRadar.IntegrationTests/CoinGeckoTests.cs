using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PulletCryptoRadar.Providers.Market;

namespace PulletCryptoRadar.IntegrationTests;

public sealed class CoinGeckoTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 4, 0, 0, TimeSpan.Zero);
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();
    [Fact]
    public void CatalogKeepsDuplicateSymbolsAsSeparateIdentitiesAndOptionalChains()
    {
        var coins = CoinGeckoParser.Catalog(Json("""[{"id":"a","symbol":"x","name":"Alpha","platforms":{"ethereum":"0xabc","solana":null}},{"id":"b","symbol":"x","name":"Beta"}]"""));
        Assert.Equal(2, coins.Count); Assert.Equal("x", coins[0].Symbol); Assert.Single(coins[0].Platforms); Assert.Empty(coins[1].Platforms);
    }
    [Fact]
    public void ParserRetainsDecimalPrecisionAndNullSupplyWithoutFakeZero()
    {
        var s = Assert.Single(CoinGeckoParser.Markets(Json("""[{"id":"a","market_cap":123456.1234567890123456789,"fully_diluted_valuation":null,"circulating_supply":0,"max_supply":null,"total_volume":-1,"last_updated":"2026-10-29T03:59:00Z"}]"""), Now));
        Assert.Equal(123456.1234567890123456789m, s.MarketCapUsd); Assert.Null(s.FdvUsd); Assert.Null(s.MaxSupply); Assert.Equal(0, s.CirculatingSupply);
        Assert.Null(s.Volume24hUsd); Assert.Equal(Now.AddMinutes(-1), s.SourceTime); Assert.Equal(Now, s.ReceivedTime);
    }
    [Theory] [InlineData("null")] [InlineData("\"2026-10-29T04:01:00Z\"")] [InlineData("\"bad timestamp\"")]
    public void MissingInvalidOrFutureSourceTimeDoesNotBecomeCurrentTime(string source)
    {
        Assert.Empty(CoinGeckoParser.Markets(Json("[{\"id\":\"a\",\"market_cap\":100,\"last_updated\":" + source + "}]"), Now));
    }
    [Theory]
    [InlineData("http://api.coingecko.com/api/v3/")]
    [InlineData("https://evil.example/api/v3/")]
    [InlineData("https://api.coingecko.com.evil.example/api/v3/")]
    [InlineData("https://api.coingecko.com/api/v3/?key=example/")]
    public void ProviderConfigurationOnlyAllowsOfficialHttpsDestinations(string url)
        => Assert.False(new CoinGeckoOptions { BaseUrl = url }.IsValid());
    [Fact]
    public async Task MarketsUseIdsAndUsdAndIgnoreUnrequestedIdentities()
    {
        var handler = new Handler(new(HttpStatusCode.OK) { Content = new StringContent("""[{"id":"wanted","market_cap":100,"last_updated":"2026-10-29T03:59:00Z"},{"id":"other","market_cap":200,"last_updated":"2026-10-29T03:59:00Z"}]""") });
        var provider = new CoinGeckoProvider(new(handler) { BaseAddress = new("https://api.coingecko.com/api/v3/") }, new(TimeProvider.System, Options.Create(new CoinGeckoOptions())), TimeProvider.System);
        var s = Assert.Single(await provider.GetMarketsAsync(["wanted"], CancellationToken.None)); Assert.Equal("wanted", s.CoinGeckoId);
        Assert.Contains("vs_currency=usd", handler.RequestUri); Assert.Contains("ids=wanted", handler.RequestUri);
    }
    [Fact]
    public async Task RateLimitIsExplicitAndDoesNotRetryInATightLoop()
    {
        var handler = new Handler(new(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new(TimeSpan.FromMinutes(3)) } });
        var provider = new CoinGeckoProvider(new(handler) { BaseAddress = new("https://api.coingecko.com/api/v3/") }, new(TimeProvider.System, Options.Create(new CoinGeckoOptions())), TimeProvider.System);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.GetCatalogAsync(CancellationToken.None));
        Assert.Contains("429", error.Message); Assert.Contains("冷却", error.Message); Assert.Equal(1, handler.Calls);
    }
    [Fact]
    public async Task AuthenticationFailureIsNotConvertedIntoAnEmptySuccessfulCatalog()
    {
        var handler = new Handler(new(HttpStatusCode.Unauthorized));
        var provider = new CoinGeckoProvider(new(handler) { BaseAddress = new("https://api.coingecko.com/api/v3/") }, new(TimeProvider.System, Options.Create(new CoinGeckoOptions())), TimeProvider.System);
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.GetCatalogAsync(CancellationToken.None)); Assert.Equal(1, handler.Calls);
    }
    private sealed class Handler(HttpResponseMessage response) : HttpMessageHandler
    {
        public int Calls; public string RequestUri = "";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Calls++; RequestUri = request.RequestUri!.AbsoluteUri; return Task.FromResult(response); }
    }
}
