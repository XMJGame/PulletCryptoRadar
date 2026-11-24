using PulletCryptoRadar.Core;
using PulletCryptoRadar.DataFoundation;

namespace PulletCryptoRadar.Tests;

public sealed class DirectionAnalysisTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 10, 10, 0, TimeSpan.Zero);
    private static readonly Guid AssetId = Guid.NewGuid();

    [Fact]
    public async Task ManualAnalysisUsesReservedCapacityAndReportsWhenTheHardLimitStillApplies()
    {
        var market = new LimitedMarket();
        var report = await new DirectionAnalysisService(market, new FixedClock())
            .AnalyzeAsync("ARIAUSDT", ChaseDirection.Long, CancellationToken.None);

        Assert.Equal(5, market.Priorities.Count);
        Assert.All(market.Priorities, priority => Assert.Equal(RequestPriority.Critical, priority));
        Assert.Equal(RequestPriority.Core, CollectionRequestScope.Current);
        Assert.Equal("数据不足", report.Verdict);
        Assert.Equal(Now.AddSeconds(45), report.RetryAt);
        Assert.All(report.Reasons, reason => Assert.Contains("本机短时请求配额", reason));
    }

    [Fact]
    public async Task RetriesOnlyTheReadDeniedByTheShortMinuteLimit()
    {
        var market = new TemporaryMinuteLimitMarket();
        var report = await new DirectionAnalysisService(market, TimeProvider.System)
            .AnalyzeAsync("ZILUSDT", ChaseDirection.Long, CancellationToken.None);

        Assert.Equal(5, market.Attempts.Count);
        Assert.All(market.Attempts, pair => Assert.Equal(pair.Key == "Ticker" ? 2 : 1, pair.Value));
        Assert.Equal(.001m, report.Price);
        Assert.Null(report.RetryAt);
    }

    [Theory]
    [InlineData(ChaseDirection.Long, 101)]
    [InlineData(ChaseDirection.Short, 99)]
    public void ConfirmsBothDirectionsAndReturnsTheCorrespondingInvalidation(ChaseDirection direction, int level)
    {
        var report = Evaluate(direction);
        Assert.Equal("有延续迹象，等回踩确认", report.Verdict);
        Assert.Equal((decimal)level, report.Invalidation);
        Assert.Null(report.History.High7d);
    }

    [Fact]
    public void LongHistoryDistinguishesTheLocalBreakoutFromThePreviousWeekRange()
    {
        var report = Evaluate(ChaseDirection.Long, hours: 270);
        Assert.Equal(270, report.History.ClosedHours);
        Assert.Equal(110m, report.History.High7d);
        Assert.Equal(95m, report.History.Low7d);
        Assert.NotNull(report.History.LastClosed4h);
        Assert.Contains(report.Reasons, item => item.Contains("仍在此前 7 天区间内"));
    }

    [Fact]
    public void StalePriceCannotConfirmABreakout()
    {
        var report = Evaluate(ChaseDirection.Long, stalePrice: true);
        Assert.Equal("数据不足", report.Verdict);
        Assert.Null(report.Price);
    }

    [Fact]
    public void ACurrentPriceBackInsideTheRangePreventsChasing()
    {
        var report = Evaluate(ChaseDirection.Long, insideRange: true);
        Assert.Equal("暂不宜追", report.Verdict);
        Assert.Contains(report.Reasons, item => item.Contains("已回到突破前的区间"));
    }

    private static DirectionAnalysis Evaluate(ChaseDirection direction, bool stalePrice = false, bool insideRange = false, int hours = 25)
    {
        var close = direction == ChaseDirection.Long ? 102 : 98;
        var price = insideRange ? 100 : close;
        var lastHour = Now.AddMinutes(-10);
        var evidence = new RawEvidence(Guid.NewGuid(), "Binance", "TESTUSDT", "/test", Now, 200, CollectionFailure.None, null);
        Observation Observation(Metric metric, decimal value, DateTimeOffset at) =>
            new(AssetId, metric, value, "Binance", "test", at, Now, ObservationStatus.Valid, TimeQuality.ProviderTime);
        var history = Enumerable.Range(0, hours).Select(index =>
        {
            var end = lastHour.AddHours(index - (hours - 1));
            return index == hours - 1 ? new HourCandle(AssetId, end.AddHours(-1), end, Now, 100,
                Math.Max(101, close), Math.Min(99, close), close, 200) :
                new HourCandle(AssetId, end.AddHours(-1), end, Now, 100,
                    index == hours - 80 ? 110 : 101, index == hours - 80 ? 95 : 99, 100, 100);
        }).ToArray();
        var interest = new[] { new OpenInterestPoint(AssetId, Now.AddHours(-1), Now, 100, 10000),
            new OpenInterestPoint(AssetId, Now, Now, 105, 10500) };
        var book = new OrderBook(AssetId, 1, Now, Now, Now, [new(price - .01m, 100)], [new(price + .01m, 100)]);
        return DirectionAnalysisService.Evaluate("TESTUSDT", direction, Now,
            new(evidence, [Observation(Metric.Price, price, stalePrice ? Now.AddMinutes(-4) : Now)]),
            new(evidence, [], Candles: history), new(evidence, [], OpenInterestHistory: interest),
            new(evidence, [Observation(Metric.FundingRaw, 0, Now)]), new(evidence, [], OrderBook: book));
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class LimitedMarket : IMarketDataProvider
    {
        public List<RequestPriority> Priorities { get; } = [];

        public Task<ProviderRead> ReadAsync(AssetIdentity asset, string capability, CancellationToken token)
        {
            Priorities.Add(CollectionRequestScope.Current);
            var evidence = new RawEvidence(Guid.NewGuid(), "Binance", asset.BinanceSymbol, capability, Now,
                null, CollectionFailure.Quota, null, FailureDetail: "ProviderMinuteLimit", RetryAt: Now.AddSeconds(45));
            return Task.FromResult(new ProviderRead(evidence, []));
        }
    }

    private sealed class TemporaryMinuteLimitMarket : IMarketDataProvider
    {
        public System.Collections.Concurrent.ConcurrentDictionary<string,int> Attempts { get; } = new();

        public Task<ProviderRead> ReadAsync(AssetIdentity asset, string capability, CancellationToken token)
        {
            var attempt = Attempts.AddOrUpdate(capability, 1, (_, previous) => previous + 1);
            var now = DateTimeOffset.UtcNow;
            var evidence = new RawEvidence(Guid.NewGuid(), "Binance", asset.BinanceSymbol, capability, now,
                200, CollectionFailure.None, null);
            if (capability != "Ticker") return Task.FromResult(new ProviderRead(evidence, []));
            if (attempt == 1) return Task.FromResult(new ProviderRead(evidence with
            {
                Failure = CollectionFailure.Quota, FailureDetail = "ProviderMinuteLimit", RetryAt = now.AddMilliseconds(25)
            }, []));
            var price = new Observation(asset.AssetId, Metric.Price, .001m, "Binance", "test", now, now,
                ObservationStatus.Valid, TimeQuality.ProviderTime);
            return Task.FromResult(new ProviderRead(evidence, [price]));
        }
    }
}
