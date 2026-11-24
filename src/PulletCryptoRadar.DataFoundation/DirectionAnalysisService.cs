using PulletCryptoRadar.Core;
using PulletCryptoRadar.Features;

namespace PulletCryptoRadar.DataFoundation;

public enum ChaseDirection { Long, Short }

public sealed record DirectionHistory(int ClosedHours, DateTimeOffset? From, DateTimeOffset? Through,
    decimal? Low7d, decimal? High7d, decimal? Return7d, string? Trend4h, DateTimeOffset? LastClosed4h);

public sealed record DirectionAnalysis(string Symbol, ChaseDirection Direction, string Verdict, DateTimeOffset CheckedAt,
    DateTimeOffset? PriceAt, DateTimeOffset? LastClosedHour, decimal? Price, decimal? Return24h,
    decimal? VolumeRatio, decimal? OpenInterestChange1h, decimal? FundingPercent, decimal? SpreadPercent,
    decimal? Support, decimal? Resistance, decimal? Invalidation, IReadOnlyList<string> Reasons, DateTimeOffset? RetryAt,
    DirectionHistory History);

public sealed class DirectionAnalysisService(IMarketDataProvider market, TimeProvider clock)
{
    public async Task<DirectionAnalysis> AnalyzeAsync(string symbol, ChaseDirection direction, CancellationToken token)
    {
        var startedAt = clock.GetUtcNow();
        var asset = new AssetIdentity(BinanceIdentity.AssetId(symbol), symbol[..^4], symbol[..^4], symbol,
            null, null, [], startedAt);
        var retryUntil = startedAt.AddSeconds(25);
        using var requestScope = CollectionRequestScope.Enter(RequestPriority.Critical);
        var reads = await Task.WhenAll(
            ReadWhenSlotAvailable("Ticker"), ReadWhenSlotAvailable("Candles"), ReadWhenSlotAvailable("OpenInterestHistory"),
            ReadWhenSlotAvailable("Mark"), ReadWhenSlotAvailable("OrderBook"));
        return Evaluate(symbol, direction, clock.GetUtcNow(), reads[0], reads[1], reads[2], reads[3], reads[4]);

        async Task<ProviderRead> ReadWhenSlotAvailable(string capability)
        {
            var read = await market.ReadAsync(asset, capability, token);
            for (var retry = 0; retry < 3 && read.Evidence is
                { Failure: CollectionFailure.Quota, FailureDetail: "ProviderMinuteLimit", RetryAt: { } retryAt }; retry++)
            {
                var now = clock.GetUtcNow();
                var resumeAt = retryAt > now ? retryAt : now;
                if (resumeAt >= retryUntil) break;
                if (resumeAt > now) await Task.Delay(resumeAt - now, clock, token);
                if (clock.GetUtcNow() >= retryUntil) break;
                token.ThrowIfCancellationRequested();
                read = await market.ReadAsync(asset, capability, token);
            }
            return read;
        }
    }

    public static DirectionAnalysis Evaluate(string symbol, ChaseDirection direction, DateTimeOffset now,
        ProviderRead ticker, ProviderRead candleRead, ProviderRead interestRead, ProviderRead mark, ProviderRead bookRead)
    {
        var priceObservation = ticker.Observations.SingleOrDefault(item => item.Metric == Metric.Price &&
            item.Status == ObservationStatus.Valid && item.SourceTime >= now.AddMinutes(-2) && item.SourceTime <= now);
        var available = (candleRead.Candles ?? []).Where(item => item.ClosedAt <= now && item.ReceivedAt <= now)
            .OrderBy(item => item.OpenAt).ToArray();
        var first = available.Length;
        while (first > 0 && (first == available.Length || available[first - 1].ClosedAt == available[first].OpenAt))
            first--;
        var contiguous = available[first..];
        var candles = contiguous.TakeLast(25).ToArray();
        var lastHour = candles.LastOrDefault();
        var history = SummarizeHistory(contiguous, now);
        var funding = mark.Observations.SingleOrDefault(item => item.Metric == Metric.FundingRaw &&
            item.Status == ObservationStatus.Valid && item.SourceTime >= now.AddMinutes(-2) && item.SourceTime <= now)?.Value * 100;
        var points = (interestRead.OpenInterestHistory ?? []).Where(item => item.SourceTime <= now)
            .OrderBy(item => item.SourceTime).ToArray();
        var latestInterest = points.LastOrDefault();
        var hourAgo = latestInterest is null ? null : points.LastOrDefault(item => item.SourceTime <= latestInterest.SourceTime.AddHours(-1));
        decimal? oiChange = latestInterest is not null && latestInterest.SourceTime >= now.AddMinutes(-12) &&
            hourAgo is { Quantity: > 0 } && hourAgo.SourceTime >= latestInterest.SourceTime.AddMinutes(-65)
            ? PercentChange(latestInterest.Quantity, hourAgo.Quantity) : null;
        var book = bookRead.OrderBook;
        decimal? spread = book is not null && book.SourceTime <= now && book.SourceTime >= now.AddSeconds(-30)
            ? (book.Asks[0].Price - book.Bids[0].Price) / ((book.Asks[0].Price + book.Bids[0].Price) / 2) * 100 : null;
        var price = priceObservation?.Value;
        var retryAt = new[] {ticker, candleRead, interestRead, mark, bookRead}
            .Where(read => read.Evidence.Failure != CollectionFailure.None && read.Evidence.RetryAt > now)
            .Max(read => read.Evidence.RetryAt);
        DirectionAnalysis Report(string verdict, IReadOnlyList<string> reasons, decimal? change = null,
            decimal? volume = null, decimal? support = null, decimal? resistance = null, decimal? invalidation = null) =>
            new(symbol, direction, verdict, now, priceObservation?.SourceTime, lastHour?.ClosedAt, price, change, volume,
                oiChange, funding, spread, support, resistance, invalidation, reasons, retryAt, history);

        var missing = new List<string>();
        if (price is not > 0) missing.Add(Unavailable("最新成交价", ticker));
        if (candles.Length < 25 || lastHour!.ClosedAt < now.AddHours(-1))
            missing.Add(Unavailable("连续的已收盘小时 K 线", candleRead));
        if (missing.Count > 0) return Report("数据不足", missing);

        var latest = lastHour!;
        var preceding = candles[..^1];
        var range = preceding.TakeLast(12).ToArray();
        var support = range.Min(item => item.Low);
        var resistance = range.Max(item => item.High);
        var averageVolume = preceding.TakeLast(20).Average(item => item.QuoteVolume);
        decimal? volume = averageVolume > 0 ? latest.QuoteVolume / averageVolume : null;
        var change = PercentChange(latest.Close, preceding[0].Close);
        var averageRange = preceding.TakeLast(14).Average(item => item.High - item.Low);
        var bullish = direction == ChaseDirection.Long;
        var level = bullish ? resistance : support;
        var side = bullish ? 1 : -1;
        var reasons = new List<string>();
        if (side * change <= 0) reasons.Add("过去 24 小时收盘走势未支持所选方向。");
        if (side * (latest.Close - level) <= 0) reasons.Add("最近一根小时线尚未收盘突破此前 12 小时区间。");
        else if (side * (price!.Value - level) <= 0) reasons.Add("最新成交价已回到突破前的区间。");
        else if (averageRange <= 0 || side * (price!.Value - level) > 1.5m * averageRange)
            reasons.Add("最新价离突破位过远，追单容易吃到回撤。");
        if (volume is null or < 1.5m) reasons.Add("最近一小时成交额未达到前 20 小时均值的 1.5 倍。");
        if (oiChange is <= 0) reasons.Add("近 1 小时未平仓量没有增加，延续证据偏弱。");
        if (spread is > .20m) reasons.Add("买卖价差超过 0.20%，追单成本偏高。");
        if (funding is { } rate && side * rate >= .10m) reasons.Add("所选方向的单期资金费率达到 0.10%，同向拥挤风险偏高。");
        var invalidation = side * (latest.Close - level) > 0 ? level : (decimal?)null;
        if (spread is null) missing.Add(Unavailable("盘口", bookRead));
        if (oiChange is null) missing.Add(Unavailable("近 1 小时未平仓量变化", interestRead));
        if (funding is null) missing.Add(Unavailable("资金费率", mark));
        if (reasons.Count > 0) return Report("暂不宜追", reasons.Concat(missing).ToArray(), change, volume, support, resistance, invalidation);
        if (missing.Count > 0) return Report("数据不足", missing, change, volume, support, resistance, invalidation);
        reasons.Add("小时收盘已放量突破，未平仓量同步增加。");
        if (history.Trend4h == (bullish ? "偏空" : "偏多")) reasons.Add("4 小时趋势与所选方向相反，短线突破仍需确认。");
        if (history.High7d is { } high && history.Low7d is { } low && price >= low && price <= high)
            reasons.Add("最新价仍在此前 7 天区间内，历史高低位仍需留意。");
        reasons.Add("观察下一根小时 K 线能否守住突破位。");
        return Report("有延续迹象，等回踩确认", reasons, change, volume, support, resistance, invalidation);
    }

    private static DirectionHistory SummarizeHistory(HourCandle[] hours, DateTimeOffset now)
    {
        var last = hours.LastOrDefault();
        decimal? low = null, high = null, change = null;
        if (hours.Length >= 169)
        {
            var beforeLatest = hours[^169..^1];
            low = beforeLatest.Min(item => item.Low);
            high = beforeLatest.Max(item => item.High);
            change = PercentChange(last!.Close, beforeLatest[0].Close);
        }

        string? trend = null;
        DateTimeOffset? closed4h = null;
        if (hours.Length >= 244)
        {
            var features = new FourHourFeatureEngine().Calculate(last!.AssetId, hours, now);
            if (features.Count > 0)
            {
                decimal Value(Metric metric) => features.Single(item => item.Metric == metric).Value!.Value;
                var close = Value(Metric.CloseH4);
                var fast = Value(Metric.Ema20H4);
                var slow = Value(Metric.Ema60H4);
                trend = close > fast && fast > slow ? "偏多" : close < fast && fast < slow ? "偏空" : "震荡或分歧";
                closed4h = features[0].SourceTime;
            }
        }
        return new(hours.Length, hours.FirstOrDefault()?.OpenAt, last?.ClosedAt, low, high, change, trend, closed4h);
    }

    private static decimal PercentChange(decimal current, decimal baseline) => (current / baseline - 1) * 100;

    private static string Unavailable(string name, ProviderRead read) => read.Evidence.Failure switch
    {
        CollectionFailure.Quota when read.Evidence.FailureDetail?.StartsWith("Cooldown", StringComparison.Ordinal) == true ||
            read.Evidence.FailureDetail == "ProviderCooldown" => $"{name}暂未获取，币安连接处于冷却期。",
        CollectionFailure.Quota when read.Evidence.FailureDetail is "ProviderMinuteLimit" or "ReservedCriticalMinuteCapacity" =>
            $"{name}暂未获取，本机短时请求配额已满。",
        CollectionFailure.Quota => $"{name}暂未获取，已达到本机采集预算。",
        CollectionFailure.RateLimited => $"{name}暂未获取，币安接口限流。",
        CollectionFailure.Timeout or CollectionFailure.Transport or CollectionFailure.Forbidden => $"{name}暂未获取，币安接口未成功响应。",
        _ => $"{name}缺失或已失效。"
    };
}
