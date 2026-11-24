using System.Net;
using System.Text.Json;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.DataFoundation;
using PulletCryptoRadar.Infrastructure;

namespace PulletCryptoRadar.Tests;

public sealed class CollectionTests
{
    private static readonly DateTimeOffset Now = new(2026,11,3,8,0,0,TimeSpan.Zero);
    private static readonly AssetIdentity Asset = new(Guid.NewGuid(), "BTC", "BTC", "BTCUSDT", null, null, [], Now);

    [Fact]
    public void NewTradingLimitsAcceptSixAndTenAndRejectHigherValues()
    {
        new TradingLimits { MaximumPositions = 6, MaximumLeverage = 10 }.Validate();
        Assert.Throws<ArgumentException>(() => new TradingLimits { MaximumPositions = 7 }.Validate());
        Assert.Throws<ArgumentException>(() => new TradingLimits { MaximumLeverage = 11 }.Validate());
    }

    [Fact]
    public void NegativeFundingStaysSignedAndDoesNotBecomeMissing()
    {
        var body = $$"""{"symbol":"BTCUSDT","markPrice":"100","lastFundingRate":"-0.001","time":{{Now.ToUnixTimeMilliseconds()}}}""";
        var rows = BinanceMarketProvider.Parse(Asset, "Mark", body, Now);
        var funding = Assert.Single(rows, row => row.Metric == Metric.FundingRaw);
        var metric = new CanonicalResolver().Resolve(Asset.AssetId, Metric.FundingRaw, [funding],
            Now, TimeSpan.FromSeconds(90), "Binance");
        Assert.True(metric.UsableForScoring);
        Assert.Equal(-.001m, metric.Value);
    }

    [Fact]
    public void WrongInstrumentAndFutureSourcesAreRejected()
    {
        var wrong = $$"""{"symbol":"OTHERUSDT","lastPrice":"100","quoteVolume":"10","closeTime":{{Now.ToUnixTimeMilliseconds()}}}""";
        Assert.Throws<InvalidDataException>(() => BinanceMarketProvider.Parse(Asset, "Ticker", wrong, Now));
        var future = wrong.Replace("OTHERUSDT","BTCUSDT").Replace(Now.ToUnixTimeMilliseconds().ToString(),
            Now.AddSeconds(1).ToUnixTimeMilliseconds().ToString());
        Assert.Throws<InvalidDataException>(() => BinanceMarketProvider.Parse(Asset, "Ticker", future, Now));
    }

    [Fact]
    public async Task RateLimitCooldownPersistsAfterRestart()
    {
        var folder = Path.Combine(Path.GetTempPath(), "v2-limits-" + Guid.NewGuid());
        try
        {
            var store = new FoundationStore(folder);
            store.Initialize(Now);
            using var http = new HttpClient(new LimitedHandler());
            var read = await new BinanceMarketProvider(http, store, new FixedClock()).ReadAsync(Asset, "Ticker", default);
            Assert.Equal(CollectionFailure.RateLimited, read.Evidence.Failure);
            Assert.Empty(read.Observations);
            Assert.False(new FoundationStore(folder).Reserve(Guid.NewGuid(), "Binance", Now, 100));
        }
        finally { Clean(folder); }
    }

    [Fact]
    public async Task AtomicMinuteReservationsSurviveConcurrentRequests()
    {
        var folder = Path.Combine(Path.GetTempPath(), "v2-reserve-" + Guid.NewGuid());
        try
        {
            var store = new FoundationStore(folder);
            store.Initialize(Now);
            var results = await Task.WhenAll(Enumerable.Range(0,40).Select(_ => Task.Run(() =>
                new FoundationStore(folder).Reserve(Guid.NewGuid(), "Binance", Now, 100))));
            Assert.Equal(30, results.Count(allowed => allowed));
            Assert.False(new FoundationStore(folder).Reserve(Guid.NewGuid(), "Binance", Now.AddSeconds(59), 100));
            Assert.True(new FoundationStore(folder).Reserve(Guid.NewGuid(), "Binance", Now.AddSeconds(61), 100));
        }
        finally { Clean(folder); }
    }

    [Fact]
    public async Task MalformedEndpointIsStoredWithoutLosingOtherMetrics()
    {
        var folder = Path.Combine(Path.GetTempPath(), "v2-evidence-" + Guid.NewGuid());
        try
        {
            var store = new FoundationStore(folder);
            store.Initialize(Now);
            store.Register(Asset);
            using var http = new HttpClient(new FixtureHandler());
            var provider = new BinanceMarketProvider(http, store, new FixedClock());
            var snapshot = await new CollectionPipeline(provider, store, new FixedClock()).CollectAsync(Asset, default);
            Assert.Null(Assert.Single(snapshot.Metrics, metric => metric.Metric == Metric.Price).Value);
            Assert.Equal(100m, Assert.Single(snapshot.Metrics, metric => metric.Metric == Metric.MarkPrice).Value);
            Assert.Equal(12m, Assert.Single(snapshot.Metrics, metric => metric.Metric == Metric.OpenInterestQuantity).Value);
            Assert.Equal(1200m, Assert.Single(snapshot.Metrics, metric => metric.Metric == Metric.OpenInterestUsd).Value);
            using var status = JsonDocument.Parse(JsonSerializer.Serialize(store.CollectionStatus()));
            Assert.Equal(4, status.RootElement.GetProperty("rawResponses").GetInt32());
            Assert.Equal(snapshot.AsOf, store.Latest(Asset.AssetId)!.AsOf);
        }
        finally { Clean(folder); }
    }

    private sealed class LimitedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new(TimeSpan.FromMinutes(5));
            return Task.FromResult(response);
        }
    }

    private sealed class FixtureHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = request.RequestUri!.AbsolutePath.EndsWith("premiumIndex", StringComparison.Ordinal)
                ? $$"""{"symbol":"BTCUSDT","markPrice":"100","lastFundingRate":"-0.001","time":{{Now.ToUnixTimeMilliseconds()}}}"""
                : request.RequestUri.AbsolutePath.EndsWith("openInterest", StringComparison.Ordinal)
                    ? $$"""{"symbol":"BTCUSDT","openInterest":"12","time":{{Now.ToUnixTimeMilliseconds()}}}"""
                    : "{}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private sealed class FixedClock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private static void Clean(string folder)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var file in Directory.GetFiles(folder)) File.Delete(file);
        Directory.Delete(folder);
    }
}
