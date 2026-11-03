using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;
using PulletCryptoRadar.Providers.Binance;

namespace PulletCryptoRadar.IntegrationTests;

public sealed class BinanceTests
{
    private const string Tick = """{"e":"24hrTicker","E":1793246400000,"s":"BTCUSDT","c":"123.123456789","q":"12345.67","P":"2.3","st":1}""";
    [Fact]
    public void CombinedTickerPreservesDecimalsAndTime()
    {
        using var json = JsonDocument.Parse("{\"stream\":\"!ticker@arr\",\"data\":[" + Tick + "]}");
        var received = DateTimeOffset.UtcNow; var tick = Assert.IsType<TickerEvent>(Assert.Single(BinanceParser.Stream(json.RootElement, received)));
        Assert.Equal(123.123456789m, tick.Price); Assert.Equal(12345.67m, tick.QuoteVolume24h);
        Assert.Equal(1793246400000, tick.SourceTime.ToUnixTimeMilliseconds()); Assert.Equal(received, tick.ReceivedTime);
    }
    [Fact]
    public void MergedCoinMarginedTickerIsIgnored()
    {
        using var json = JsonDocument.Parse(Tick.Replace("\"st\":1", "\"st\":2"));
        Assert.Empty(BinanceParser.Stream(json.RootElement, DateTimeOffset.UtcNow));
    }
    [Theory] [InlineData(false, 0)] [InlineData(true, 1)]
    public void IncompleteKlinesNeverEnterFeatureWindows(bool closed, int expected)
    {
        var raw = """{"e":"kline","E":1793246460000,"s":"BTCUSDT","k":{"t":1793246400000,"T":1793246459999,"i":"1m","o":"100","h":"110","l":"90","c":"105","q":"5000","x":CLOSED}}""".Replace("CLOSED", closed ? "true" : "false");
        using var json = JsonDocument.Parse(raw);
        Assert.Equal(expected, BinanceParser.Stream(json.RootElement, DateTimeOffset.UtcNow).Count);
    }
    [Fact]
    public void ExchangeInfoFiltersContractStatusAndQuoteAsset()
    {
        var raw = """{"symbols":[{"symbol":"BTCUSDT","baseAsset":"BTC","quoteAsset":"USDT","contractType":"PERPETUAL","status":"TRADING","onboardDate":1793246400000},{"symbol":"ETHUSDT","quoteAsset":"USDT","contractType":"PERPETUAL","status":"SETTLING"},{"symbol":"BTCUSDC","quoteAsset":"USDC","contractType":"PERPETUAL","status":"TRADING"},{"symbol":"BTC_QUARTER","quoteAsset":"USDT","contractType":"CURRENT_QUARTER","status":"TRADING"}]}""";
        using var json = JsonDocument.Parse(raw); Assert.Equal("BTCUSDT", Assert.Single(BinanceParser.Symbols(json.RootElement)).Symbol);
    }
    [Fact]
    public async Task RateLimitPausesProviderAndPermanentErrorsAreNotRetried()
    {
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new(TimeSpan.FromSeconds(7)) } }, new HttpResponseMessage(HttpStatusCode.Forbidden));
        var limiter = new FakeLimiter(); var health = new ProviderHealthStore(TimeProvider.System);
        var client = new BinanceRestClient(new HttpClient(handler) { BaseAddress = new("https://example.test") }, limiter, health, TimeProvider.System);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetSymbolsAsync(CancellationToken.None));
        Assert.Equal(TimeSpan.FromSeconds(7), limiter.Paused); Assert.Equal(2, handler.Calls);
    }
    [Fact]
    public async Task WebSocketAutomaticallyResubscribesAfterRemoteDisconnect()
    {
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build(); app.UseWebSockets(); var connections = 0;
        app.Map("/market/stream", async context =>
        {
            using var ws = await context.WebSockets.AcceptWebSocketAsync();
            Interlocked.Increment(ref connections);
            var buffer = new byte[4096]; await ws.ReceiveAsync(buffer.AsMemory(), context.RequestAborted);
            await ws.SendAsync(Encoding.UTF8.GetBytes("[" + Tick + "]").AsMemory(), WebSocketMessageType.Text, true, context.RequestAborted);
            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Reconnect test", context.RequestAborted);
        });
        await app.StartAsync();
        var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single().Replace("http://", "ws://") + "/market/stream";
        var bus = new MarketEventBus(); var health = new ProviderHealthStore(TimeProvider.System);
        var provider = new BinanceWebSocketClient(Options.Create(new BinanceOptions { WebSocketUrl = url }), bus, health, TimeProvider.System, NullLogger<BinanceWebSocketClient>.Instance);
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var stream = provider.RunAsync([], ct.Token); var count = 0;
        await foreach (var e in bus.ReadAllAsync(ct.Token)) { if (e is TickerEvent && ++count == 2) break; }
        await ct.CancelAsync(); try { await stream; } catch (OperationCanceledException) { }
        Assert.True(connections >= 2); Assert.Equal(2, count); Assert.True(Assert.Single(health.GetAll()).ErrorCount >= 1);
        await app.StopAsync();
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task EconomyPriorityQuotesPreserveIdentityDecimalAndObservedTimes(bool wrongSymbol)
    {
        var ticker="""{"symbol":"BTCUSDT","lastPrice":"123.123456789","quoteVolume":"12345.67","priceChangePercent":"2.3","closeTime":1793246400000}""";
        var premium="""{"symbol":"BTCUSDT","markPrice":"123.111111111","lastFundingRate":"-0.0003","nextFundingTime":1793275200000,"time":1793246401000}""";
        if(wrongSymbol)premium=premium.Replace("BTCUSDT","ETHUSDT");
        using var http=new HttpClient(new StubHandler(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(ticker)},
            new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(premium)})){BaseAddress=new("https://example.invalid")};
        var provider=new BinanceRestClient(http,new FakeLimiter(),new ProviderHealthStore(TimeProvider.System),TimeProvider.System);
        if(wrongSymbol){await Assert.ThrowsAsync<InvalidDataException>(()=>provider.GetPriorityQuotesAsync("BTCUSDT",default));return;}
        var events=await provider.GetPriorityQuotesAsync("BTCUSDT",default);
        var t=Assert.IsType<TickerEvent>(events[0]);var m=Assert.IsType<MarkPriceEvent>(events[1]);
        Assert.Equal(123.123456789m,t.Price);Assert.Equal(1793246400000,t.SourceTime.ToUnixTimeMilliseconds());
        Assert.Equal(123.111111111m,m.MarkPrice);Assert.Equal(-.0003m,m.FundingRate);
        Assert.Equal(1793246401000,m.SourceTime.ToUnixTimeMilliseconds());
    }
    private sealed class HourlyHandler:HttpMessageHandler
    {
        public string? Query;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Query=request.RequestUri!.Query;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("""[[1793246400000,"100","101","99","100","10",1793249999999,"1000"]]""")});
        }
    }
    [Fact]public async Task PaperWarmupRequestsNativeOneHourCandlesNotInvalidSixtyMinuteToken()
    {
        var handler=new HourlyHandler();using var http=new HttpClient(handler){BaseAddress=new("https://example.invalid")};
        var provider=new BinanceRestClient(http,new FakeLimiter(),new ProviderHealthStore(TimeProvider.System),TimeProvider.System);
        var rows=await provider.GetCandlesAsync("BTCUSDT",60,400,null,default);
        Assert.Contains("interval=1h",handler.Query);Assert.Equal(60,Assert.Single(rows).IntervalMinutes);
    }
    [Fact]public async Task MonthlyBaselineRequestsEightHundredHoursAndChargesCorrectWeight()
    {
        var handler=new HourlyHandler();var limiter=new FakeLimiter();using var http=new HttpClient(handler){BaseAddress=new("https://example.invalid")};
        var provider=new BinanceRestClient(http,limiter,new ProviderHealthStore(TimeProvider.System),TimeProvider.System);
        await provider.GetCandlesAsync("LABUSDT",60,800,null,default);
        Assert.Contains("limit=800",handler.Query);Assert.Equal(5,Assert.Single(limiter.Weights));
    }
    private sealed class FakeLimiter : IRequestLimiter
    {
        public TimeSpan Paused;
        public List<string> Waits = [];
        public List<int> Weights=[];
        public Task WaitAsync(string provider, int weight, CancellationToken ct) { Waits.Add(provider);Weights.Add(weight); return Task.CompletedTask; }
        public void Pause(string provider, TimeSpan duration) => Paused = duration;
    }
    private sealed class StubHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(responses[Calls++]);
    }
    [Fact]
    public async Task BodyOnlyBanDeadlineIsHonoredInsteadOfBlindOneMinuteRetry()
    {
        var now = DateTimeOffset.UtcNow; var until = now.AddMinutes(20);
        var handler = new StubHandler(new HttpResponseMessage((HttpStatusCode)418)
            { Content = new StringContent($$"""{"code":-1003,"msg":"IP banned until {{until.ToUnixTimeMilliseconds()}}."}""") },
            new HttpResponseMessage(HttpStatusCode.Forbidden));
        var limiter = new FakeLimiter();
        var client = new BinanceRestClient(new HttpClient(handler) { BaseAddress = new("https://example.test") }, limiter, new ProviderHealthStore(TimeProvider.System), TimeProvider.System);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetSymbolsAsync(CancellationToken.None));
        Assert.InRange(limiter.Paused.TotalMinutes, 19.9, 20.1);
    }
    [Fact]
    public async Task DeepEndpointsUseBothStatisticsAndGlobalWeightLanes()
    {
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
        var limiter = new FakeLimiter();
        var client = new BinanceRestClient(new HttpClient(handler) { BaseAddress = new("https://example.test") }, limiter, new ProviderHealthStore(TimeProvider.System), TimeProvider.System);
        Assert.Empty(await client.GetSeriesAsync("TESTUSDT", DerivativeKind.TopPosition5m, 100, CancellationToken.None));
        Assert.Equal(new[] { "BinanceStatistics", "Binance" }, limiter.Waits);
    }
    [Fact]
    public async Task CooldownHealthRemainsDegradedUntilWaitingActuallyCompletes()
    {
        var health = new ProviderHealthStore(TimeProvider.System);
        var limiter = new RequestLimiter(Options.Create(new BinanceOptions()), TimeProvider.System, health);
        limiter.Pause("Binance", TimeSpan.FromMilliseconds(50));
        health.Success("Binance REST");
        Assert.NotNull(health.GetAll().Single(x => x.Name == "Binance Cooldown").Error);
        await limiter.WaitAsync("Binance", 1, CancellationToken.None);
        Assert.Null(health.GetAll().Single(x => x.Name == "Binance Cooldown").Error);
    }
}
