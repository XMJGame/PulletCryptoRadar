using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Infrastructure;

public sealed class BinanceMarketProvider(HttpClient http, IRequestLedger ledger, TimeProvider clock,
    SharedFundingInfoCache? fundingCache = null,ClosedHourCandleCache? candleCache=null) : IMarketDataProvider
{
    public const int MaximumResponseBytes = 65536;
    public Task<ProviderRead> ReadAsync(AssetIdentity asset,string capability,CancellationToken token) =>
        capability == "FundingInfo" && fundingCache is not null
            ? fundingCache.ReadAsync(asset,() => ReadUncachedAsync(asset,capability,token),token)
            : capability=="Candles"&&candleCache is not null
            ? candleCache.ReadAsync(asset,()=>ReadUncachedAsync(asset,capability,token),token)
            : ReadUncachedAsync(asset,capability,token);

    private async Task<ProviderRead> ReadUncachedAsync(AssetIdentity asset, string capability, CancellationToken token)
    {
        long? fundingSince = null;
        if (capability.StartsWith("PositionFunding:",StringComparison.Ordinal))
        {
            if (!long.TryParse(capability[16..],out var since) || since < 1577836800000 || since > clock.GetUtcNow().ToUnixTimeMilliseconds())
                throw new ArgumentException("Invalid funding history cursor.");
            fundingSince = since;
            capability = "FundingHistory";
        }
        var path = capability switch
        {
            "Ticker" => "/fapi/v1/ticker/24hr",
            "Mark" => "/fapi/v1/premiumIndex",
            "OpenInterest" => "/fapi/v1/openInterest",
            "Candles" => "/fapi/v1/klines",
            "Universe" => "/fapi/v1/exchangeInfo",
            "AllTickers" => "/fapi/v1/ticker/24hr",
            "FundingInfo" => "/fapi/v1/fundingInfo",
            "OpenInterestHistory" => "/futures/data/openInterestHist",
            "OrderBook" => "/fapi/v1/depth",
            "FundingHistory" => "/fapi/v1/fundingRate",
            _ => throw new ArgumentException("Unsupported Binance capability.")
        };
        var requestId = Guid.NewGuid();
        var priority=capability is "FundingHistory" or "OrderBook" ? RequestPriority.Critical : CollectionRequestScope.Current;
        RequestDecision? decision=null;
        DateTimeOffset? cooldown = null;
        var endpoint = path + "?symbol=" + Uri.EscapeDataString(asset.BinanceSymbol);
        if (capability == "Candles") endpoint += "&interval=1h&limit=300";
        if (capability == "OpenInterestHistory") endpoint += "&period=5m&limit=289";
        if (capability == "OrderBook") endpoint += "&limit=50";
        if (capability == "FundingHistory") endpoint += fundingSince is { } since ? "&limit=1000&startTime="+since : "&limit=8";
        if (capability is "Universe" or "AllTickers" or "FundingInfo") endpoint = path;
        var maximumBytes = capability is "Universe" or "AllTickers" or "FundingInfo" ? 4_000_000 :
            capability is "Candles" or "OpenInterestHistory" || fundingSince is not null ? 262144 : MaximumResponseBytes;
        var now = clock.GetUtcNow();
        var evidenceSymbol = capability is "Universe" or "AllTickers" or "FundingInfo" ? "*USDT-PERPETUAL*" : asset.BinanceSymbol;
        RawEvidence Evidence(CollectionFailure failure, int? status = null, string? body = null) =>
            new(requestId,"Binance",evidenceSymbol,endpoint,clock.GetUtcNow(),status,failure,body,
                FailureDetail:failure==CollectionFailure.Quota?decision?.Reason:failure==CollectionFailure.None?null:failure.ToString(),
                RetryAt:decision is {Allowed:false}?decision.RetryAt:cooldown,Priority:priority);
        decision=ledger.TryReserve(requestId,"Binance",now,maximumBytes,priority);
        if (!decision.Allowed)
            return new(Evidence(CollectionFailure.Quota), []);
        int? httpStatus = null;
        var bytes = new MemoryStream();
        var oversized = false;
        string? cooldownReason=null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://fapi.binance.com" + endpoint);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            httpStatus = (int)response.StatusCode;
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden ||
                httpStatus == 418)
            {
                var retryAt = response.Headers.RetryAfter?.Date ??
                    clock.GetUtcNow().Add(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(15));
                cooldown = retryAt > clock.GetUtcNow() ? retryAt : clock.GetUtcNow().AddMinutes(1);
                cooldownReason=httpStatus==429?"CooldownAfterRateLimit":"CooldownAfterForbidden";
                return new(Evidence(httpStatus == 429 ? CollectionFailure.RateLimited : CollectionFailure.Forbidden, httpStatus), []);
            }
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            var buffer = new byte[4096];
            int read;
            while ((read = await stream.ReadAsync(buffer, token)) > 0)
            {
                if (bytes.Length + read > maximumBytes)
                {
                    oversized = true;
                    return new(Evidence(CollectionFailure.Invalid, httpStatus), []);
                }
                await bytes.WriteAsync(buffer.AsMemory(0, read), token);
            }
            var body = Encoding.UTF8.GetString(bytes.GetBuffer(), 0, (int)bytes.Length);
            if (!response.IsSuccessStatusCode)
                return new(Evidence(CollectionFailure.Transport, httpStatus, body), []);
            var received = clock.GetUtcNow();
            try
            {
                // Public market timestamps can lead the local clock by a fraction of a second.
                // Wait for real time to catch up; never clamp or invent the provider timestamp.
                var sourceField = capability switch
                {
                    "Mark" => "time",
                    "Ticker" => "closeTime",
                    "OrderBook" => "E",
                    _ => null
                };
                if (sourceField is not null)
                {
                    using var document = JsonDocument.Parse(body);
                    var source = DateTimeOffset.FromUnixTimeMilliseconds(document.RootElement.GetProperty(sourceField).GetInt64());
                    var lead = source-received;
                    if (lead > TimeSpan.Zero && lead <= TimeSpan.FromSeconds(1))
                    {
                        await Task.Delay(lead+TimeSpan.FromMilliseconds(10),token);
                        received = clock.GetUtcNow();
                    }
                }
                var parsed = capability is "Candles" or "Universe" or "AllTickers" or "OpenInterestHistory" or "OrderBook" or "FundingHistory"
                    ? [] : Parse(asset, capability, body, received);
                var candles = capability == "Candles" ? ParseCandles(asset, body, received) : null;
                var universe = capability == "Universe" ? ParseUniverse(body) : null;
                var tickers = capability == "AllTickers" ? ParseTickers(body) : null;
                return new(new(requestId, "Binance", evidenceSymbol, endpoint, received, httpStatus,
                    CollectionFailure.None, body,Priority:priority), parsed, Candles: candles, Universe: universe, Tickers: tickers,
                    OpenInterestHistory: capability == "OpenInterestHistory" ? ParseOpenInterestHistory(asset, body, received) : null,
                    OrderBook: capability == "OrderBook" ? ParseOrderBook(asset,body,received) : null,
                    FundingSettlements: capability == "FundingHistory" ? ParseFundingHistory(asset,body,received) : null,
                    ContractRules: capability == "Universe" ? ParseContractRules(body) : null);
            }
            catch (Exception error) when (error is JsonException or InvalidDataException or InvalidOperationException or
                FormatException or OverflowException or ArgumentOutOfRangeException or KeyNotFoundException)
            {
                return new(Evidence(CollectionFailure.Invalid, httpStatus, body), []);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            cooldown = clock.GetUtcNow().AddMinutes(1);
            cooldownReason="CooldownAfterTimeout";
            return new(Evidence(CollectionFailure.Timeout, httpStatus), []);
        }
        catch (Exception error) when (error is HttpRequestException or IOException)
        {
            cooldown = clock.GetUtcNow().AddMinutes(1);
            cooldownReason="CooldownAfterTransport";
            return new(Evidence(CollectionFailure.Transport, httpStatus), []);
        }
        finally
        {
            // The reservation is charged in full when the request may have been interrupted or oversized.
            ledger.CompleteWithReason(requestId, oversized || bytes.Length >= maximumBytes ? maximumBytes :
                token.IsCancellationRequested ? maximumBytes : (int)bytes.Length,cooldown,cooldownReason);
            bytes.Dispose();
        }
    }

    public static IReadOnlyList<Observation> Parse(AssetIdentity asset, string capability, string body, DateTimeOffset received)
    {
        if (capability == "FundingInfo") return ParseFundingInfo(asset, body, received);
        using var document = JsonDocument.Parse(body);
        var row = document.RootElement;
        if (row.GetProperty("symbol").GetString() != asset.BinanceSymbol)
            throw new InvalidDataException("Binance instrument identity mismatch.");
        decimal Number(string name) => decimal.Parse(row.GetProperty(name).GetString()!,
            NumberStyles.Float, CultureInfo.InvariantCulture);
        var timestamp = row.GetProperty(capability == "Ticker" ? "closeTime" : "time").GetInt64();
        var source = DateTimeOffset.FromUnixTimeMilliseconds(timestamp);
        if (source > received || source < received.AddMinutes(-5))
            throw new InvalidDataException("Binance source timestamp is outside the accepted window.");
        Observation Value(Metric metric, decimal value, string methodology, bool signed = false)
        {
            if (!signed && value < 0 || metric is Metric.Price or Metric.MarkPrice && value <= 0)
                throw new InvalidDataException("Invalid Binance numeric value.");
            return new(asset.AssetId, metric, value, "Binance", methodology, source, received,
                ObservationStatus.Valid, TimeQuality.ProviderTime);
        }
        return capability switch
        {
            "Ticker" => [Value(Metric.Price, Number("lastPrice"), "USDT-perpetual-last"),
                Value(Metric.Volume24h, Number("quoteVolume"), "USDT-perpetual-rolling-24h-quote-volume")],
            "Mark" => [Value(Metric.MarkPrice, Number("markPrice"), "USDT-perpetual-mark"),
                Value(Metric.FundingRaw, Number("lastFundingRate"), "USDT-perpetual-raw-current-interval", true)],
            "OpenInterest" => [Value(Metric.OpenInterestQuantity, Number("openInterest"), "USDT-perpetual-base-quantity")],
            _ => throw new ArgumentException("Unsupported capability.")
        };
    }

    public static IReadOnlyList<Observation> ParseFundingInfo(AssetIdentity asset, string body, DateTimeOffset received)
    {
        using var document = JsonDocument.Parse(body);
        var intervals = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in document.RootElement.EnumerateArray())
        {
            var symbol = row.GetProperty("symbol").GetString();
            var hours = row.GetProperty("fundingIntervalHours").GetInt32();
            if (string.IsNullOrWhiteSpace(symbol) || hours is < 1 or > 24 || !intervals.TryAdd(symbol, hours))
                throw new InvalidDataException("Invalid or duplicated funding schedule.");
        }
        var adjusted = intervals.TryGetValue(asset.BinanceSymbol, out var interval);
        // This endpoint lists adjustments only, and supplies no effective/source timestamp.
        return [new(asset.AssetId, Metric.FundingIntervalHours, adjusted ? interval : 8, "Binance",
            adjusted ? "fundingInfo-adjusted-hours" : "fundingInfo-absent-default-8h", null, received,
            ObservationStatus.Valid, TimeQuality.ReceivedOnly)];
    }

    public static OrderBook ParseOrderBook(AssetIdentity asset,string body,DateTimeOffset received)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.TryGetProperty("symbol",out var symbol) && symbol.GetString() != asset.BinanceSymbol)
            throw new InvalidDataException("Order book instrument mismatch.");
        var source = DateTimeOffset.FromUnixTimeMilliseconds(root.GetProperty("T").GetInt64());
        var eventTime = DateTimeOffset.FromUnixTimeMilliseconds(root.GetProperty("E").GetInt64());
        var updateId = root.GetProperty("lastUpdateId").GetInt64();
        if (source > eventTime || eventTime > received || received-source > TimeSpan.FromSeconds(5) || updateId <= 0)
            throw new InvalidDataException("Invalid order book time or sequence.");
        BookLevel[] Levels(string name,bool ascending)
        {
            var levels = root.GetProperty(name).EnumerateArray().Select(row =>
            {
                if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != 2)
                    throw new InvalidDataException("Incomplete order book level.");
                return new BookLevel(decimal.Parse(row[0].GetString()!,NumberStyles.Float,CultureInfo.InvariantCulture),
                    decimal.Parse(row[1].GetString()!,NumberStyles.Float,CultureInfo.InvariantCulture));
            }).ToArray();
            if (levels.Length is < 1 or > 50 || levels.Any(level => level.Price <= 0 || level.Quantity <= 0) ||
                levels.Zip(levels.Skip(1),(a,b) => ascending ? a.Price < b.Price : a.Price > b.Price).Any(valid => !valid))
                throw new InvalidDataException("Invalid order book levels.");
            return levels;
        }
        var bids = Levels("bids",false);
        var asks = Levels("asks",true);
        if (bids[0].Price >= asks[0].Price) throw new InvalidDataException("Crossed order book.");
        return new(asset.AssetId,updateId,source,eventTime,received,bids,asks);
    }

    public static IReadOnlyList<FundingSettlement> ParseFundingHistory(AssetIdentity asset,string body,DateTimeOffset received)
    {
        using var document = JsonDocument.Parse(body);
        var settlements = new List<FundingSettlement>();
        foreach (var row in document.RootElement.EnumerateArray())
        {
            if (row.GetProperty("symbol").GetString() != asset.BinanceSymbol)
                throw new InvalidDataException("Funding instrument mismatch.");
            var at = DateTimeOffset.FromUnixTimeMilliseconds(row.GetProperty("fundingTime").GetInt64());
            var rate = decimal.Parse(row.GetProperty("fundingRate").GetString()!,NumberStyles.Float,CultureInfo.InvariantCulture);
            decimal? mark = row.TryGetProperty("markPrice",out var value) && !string.IsNullOrWhiteSpace(value.GetString())
                ? decimal.Parse(value.GetString()!,NumberStyles.Float,CultureInfo.InvariantCulture) : null;
            var type = row.TryGetProperty("rateType",out var rateType) ? rateType.GetString()! : "Regular";
            if (at > received || mark is <= 0 || Math.Abs(rate) > 1 || type is not ("Regular" or "Special") ||
                settlements.Any(item => item.SettledAt == at && item.RateType == type) ||
                settlements.Count > 0 && at < settlements[^1].SettledAt)
                throw new InvalidDataException("Invalid funding settlement.");
            settlements.Add(new(asset.AssetId,at,received,rate,mark,type));
        }
        return settlements;
    }

    public static IReadOnlyList<ContractRules> ParseContractRules(string body)
    {
        using var document = JsonDocument.Parse(body);
        var result = new List<ContractRules>();
        foreach (var row in document.RootElement.GetProperty("symbols").EnumerateArray())
        {
            if (row.GetProperty("status").GetString() != "TRADING" || row.GetProperty("contractType").GetString() != "PERPETUAL" ||
                row.GetProperty("quoteAsset").GetString() != "USDT" || row.GetProperty("marginAsset").GetString() != "USDT") continue;
            if (!row.TryGetProperty("filters",out var filters)) continue;
            var list = filters.EnumerateArray().ToArray();
            if (new[] { "MARKET_LOT_SIZE","MIN_NOTIONAL","PRICE_FILTER" }.Any(type =>
                list.Count(item => item.GetProperty("filterType").GetString() == type) > 1)) continue;
            JsonElement? Filter(string type) => list.Where(item => item.GetProperty("filterType").GetString() == type)
                .Select(item => (JsonElement?)item).SingleOrDefault();
            var lot = Filter("MARKET_LOT_SIZE");
            var notional = Filter("MIN_NOTIONAL");
            var price = Filter("PRICE_FILTER");
            if (lot is null || notional is null || price is null) continue;
            decimal Number(JsonElement element,string name) => element.TryGetProperty(name,out var value) && value.ValueKind == JsonValueKind.String &&
                decimal.TryParse(value.GetString(),NumberStyles.Float,CultureInfo.InvariantCulture,out var number) ? number : 0;
            var rules = new ContractRules(row.GetProperty("symbol").GetString()!,Number(lot.Value,"minQty"),Number(lot.Value,"maxQty"),
                Number(lot.Value,"stepSize"),Number(notional.Value,"notional"),Number(price.Value,"tickSize"));
            if (rules.MinimumQuantity <= 0 || rules.MaximumQuantity < rules.MinimumQuantity || rules.StepSize <= 0 ||
                rules.MinimumNotional <= 0 || rules.PriceTick <= 0) continue;
            result.Add(rules);
        }
        return result;
    }

    public static IReadOnlyList<OpenInterestPoint> ParseOpenInterestHistory(AssetIdentity asset, string body, DateTimeOffset received)
    {
        using var document = JsonDocument.Parse(body);
        var points = new List<OpenInterestPoint>();
        foreach (var row in document.RootElement.EnumerateArray())
        {
            if (row.GetProperty("symbol").GetString() != asset.BinanceSymbol)
                throw new InvalidDataException("OI history instrument mismatch.");
            decimal Number(string name) => decimal.Parse(row.GetProperty(name).GetString()!, NumberStyles.Float,
                CultureInfo.InvariantCulture);
            var at = DateTimeOffset.FromUnixTimeMilliseconds(row.GetProperty("timestamp").GetInt64());
            var quantity = Number("sumOpenInterest");
            var notional = Number("sumOpenInterestValue");
            if (at > received || at.ToUnixTimeMilliseconds() % 300000 != 0 || quantity < 0 || notional < 0 ||
                points.Count > 0 && at <= points[^1].SourceTime)
                throw new InvalidDataException("Invalid OI history sample.");
            // Binance omits periods with no trades. Preserve gaps; never synthesize points.
            points.Add(new(asset.AssetId, at, received, quantity, notional));
        }
        return points;
    }

    public static IReadOnlyList<MarketInstrument> ParseUniverse(string body)
    {
        using var document = JsonDocument.Parse(body);
        var stable = new HashSet<string>(StringComparer.Ordinal) { "USDT", "USDC", "BUSD", "TUSD", "FDUSD", "DAI", "USDE" };
        var result = new List<MarketInstrument>();
        foreach (var row in document.RootElement.GetProperty("symbols").EnumerateArray())
        {
            if (row.GetProperty("status").GetString() != "TRADING" ||
                row.GetProperty("contractType").GetString() != "PERPETUAL" ||
                row.GetProperty("quoteAsset").GetString() != "USDT" ||
                row.GetProperty("marginAsset").GetString() != "USDT") continue;
            var symbol = row.GetProperty("symbol").GetString()!;
            var baseAsset = row.GetProperty("baseAsset").GetString()!;
            if (stable.Contains(baseAsset)) continue;
            if (string.IsNullOrWhiteSpace(symbol) || string.IsNullOrWhiteSpace(baseAsset))
                throw new InvalidDataException("Invalid universe identity.");
            result.Add(new(symbol,baseAsset,DateTimeOffset.FromUnixTimeMilliseconds(row.GetProperty("onboardDate").GetInt64())));
        }
        if (result.Count == 0 || result.Select(item=>item.Symbol).Distinct().Count() != result.Count)
            throw new InvalidDataException("Empty or duplicated Binance universe.");
        return result;
    }

    public static IReadOnlyList<MarketTicker> ParseTickers(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.EnumerateArray().Select(row =>
        {
            var symbol = row.GetProperty("symbol").GetString() ?? throw new InvalidDataException("Missing symbol.");
            var change = decimal.Parse(row.GetProperty("priceChangePercent").GetString()!,NumberStyles.Float,CultureInfo.InvariantCulture);
            var volume = decimal.Parse(row.GetProperty("quoteVolume").GetString()!,NumberStyles.Float,CultureInfo.InvariantCulture);
            if (volume < 0 || change < -100) throw new InvalidDataException("Invalid ticker.");
            return new MarketTicker(symbol,change,volume,DateTimeOffset.FromUnixTimeMilliseconds(row.GetProperty("closeTime").GetInt64()));
        }).ToArray();
    }

    public static IReadOnlyList<HourCandle> ParseCandles(AssetIdentity asset, string body, DateTimeOffset received)
    {
        using var document = JsonDocument.Parse(body);
        var candles = new List<HourCandle>();
        foreach (var row in document.RootElement.EnumerateArray())
        {
            if (row.GetArrayLength() < 12) throw new InvalidDataException("Incomplete Binance candle.");
            var open = DateTimeOffset.FromUnixTimeMilliseconds(row[0].GetInt64());
            var close = DateTimeOffset.FromUnixTimeMilliseconds(row[6].GetInt64()).AddMilliseconds(1);
            if (close - open != TimeSpan.FromHours(1) || open.ToUnixTimeMilliseconds() % 3600000 != 0)
                throw new InvalidDataException("Invalid hourly boundary.");
            if (open > received) throw new InvalidDataException("Future candle open.");
            if (close > received) continue;
            decimal Value(int index) => decimal.Parse(row[index].GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture);
            var candle = new HourCandle(asset.AssetId, open, close, received, Value(1), Value(2),
                Value(3), Value(4), Value(7), Value(10));
            if (candle.Low <= 0 || candle.High < candle.Low || candle.Open < candle.Low || candle.Open > candle.High ||
                candle.Close < candle.Low || candle.Close > candle.High || candle.QuoteVolume < 0 ||
                candle.TakerBuyQuoteVolume < 0 || candle.TakerBuyQuoteVolume > candle.QuoteVolume)
                throw new InvalidDataException("Invalid candle OHLC or volume.");
            if (candles.Count > 0 && candle.OpenAt != candles[^1].ClosedAt)
                throw new InvalidDataException("Non-contiguous hourly candles.");
            candles.Add(candle);
        }
        return candles;
    }
}
