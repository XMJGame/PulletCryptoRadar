using System.Diagnostics;
using System.Net;
using System.Text.Json;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Providers.Binance;

public sealed partial class BinanceRestClient(HttpClient http, IRequestLimiter limiter, IProviderHealthStore health, TimeProvider time) : IMarketRestProvider, IDerivativeRestProvider, IPriorityQuoteProvider
{
    public async Task<IReadOnlyList<MarketEvent>> GetPriorityQuotesAsync(string symbol,CancellationToken ct)
    {
        var query=Uri.EscapeDataString(symbol);
        using var ticker=await GetAsync($"/fapi/v1/ticker/24hr?symbol={query}",1,ct);
        using var premium=await GetAsync($"/fapi/v1/premiumIndex?symbol={query}",1,ct);
        var t=ticker.RootElement;var p=premium.RootElement;var now=time.GetUtcNow();
        if(t.GetProperty("symbol").GetString()!=symbol||p.GetProperty("symbol").GetString()!=symbol)throw new InvalidDataException("Priority quote symbol mismatch");
        return [new TickerEvent(symbol,BinanceParser.Decimal(t.GetProperty("lastPrice")),BinanceParser.Decimal(t.GetProperty("quoteVolume")),
            BinanceParser.Decimal(t.GetProperty("priceChangePercent")),BinanceParser.Time(t.GetProperty("closeTime")),now),
            new MarkPriceEvent(symbol,BinanceParser.Decimal(p.GetProperty("markPrice")),BinanceParser.Decimal(p.GetProperty("lastFundingRate")),
            BinanceParser.Time(p.GetProperty("nextFundingTime")),BinanceParser.Time(p.GetProperty("time")),now)];
    }
    public async Task<IReadOnlyList<SymbolDefinition>> GetSymbolsAsync(CancellationToken ct)
    {
        using var document = await GetAsync("/fapi/v1/exchangeInfo", 1, ct);
        return BinanceParser.Symbols(document.RootElement);
    }
    public async Task<IReadOnlyList<TickerEvent>> GetTickersAsync(CancellationToken ct)
    {
        using var document = await GetAsync("/fapi/v1/ticker/24hr", 40, ct);
        var now = time.GetUtcNow();
        return document.RootElement.EnumerateArray().Select(x => new TickerEvent(x.GetProperty("symbol").GetString()!,
            BinanceParser.Decimal(x.GetProperty("lastPrice")), BinanceParser.Decimal(x.GetProperty("quoteVolume")),
            BinanceParser.Decimal(x.GetProperty("priceChangePercent")), BinanceParser.Time(x.GetProperty("closeTime")), now)).ToArray();
    }
    public async Task<IReadOnlyList<Candle>> GetCandlesAsync(string symbol, int intervalMinutes, int limit, DateTimeOffset? endTime, CancellationToken ct)
    {
        if (intervalMinutes is not (1 or 5 or 15 or 60)) throw new ArgumentOutOfRangeException(nameof(intervalMinutes));
        limit = Math.Clamp(limit, 1, intervalMinutes==60?1000:499);
        var end = endTime is null ? "" : $"&endTime={endTime.Value.ToUnixTimeMilliseconds()}";
        var interval=intervalMinutes==60?"1h":$"{intervalMinutes}m";
        using var document = await GetAsync($"/fapi/v1/klines?symbol={Uri.EscapeDataString(symbol)}&interval={interval}&limit={limit}{end}", limit < 100 ? 1 : limit<500?2:5, ct);
        return BinanceParser.Candles(document.RootElement, symbol, intervalMinutes, time.GetUtcNow());
    }
    private async Task<JsonDocument> GetAsync(string path, int weight, CancellationToken ct,bool requireFreshDate=false)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (path.StartsWith("/futures/data/", StringComparison.Ordinal)) await limiter.WaitAsync("BinanceStatistics", 1, ct);
            if(path.StartsWith("/fapi/v1/fundingRate",StringComparison.Ordinal)||path.StartsWith("/fapi/v1/fundingInfo",StringComparison.Ordinal))await limiter.WaitAsync("BinanceFunding",1,ct);
            await limiter.WaitAsync("Binance", weight, ct);
            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var response = await http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.StatusCode is HttpStatusCode.TooManyRequests || (int)response.StatusCode == 418)
                {
                    var now = time.GetUtcNow();
                    var pause = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - now)
                        ?? TimeSpan.FromMinutes((int)response.StatusCode == 418 ? 2 : 1);
                    // Some responses expose the ban deadline only inside the error message.
                    var body = await response.Content.ReadAsStringAsync(ct);
                    var match = System.Text.RegularExpressions.Regex.Match(body, @"until\s+(\d{13})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (match.Success && long.TryParse(match.Groups[1].Value, out var deadline))
                    {
                        var remaining = DateTimeOffset.FromUnixTimeMilliseconds(deadline) - now;
                        if (remaining > pause) pause = remaining;
                    }
                    limiter.Pause("Binance", pause > TimeSpan.Zero ? pause : TimeSpan.FromSeconds(1));
                    throw new HttpRequestException($"Binance cooldown HTTP {(int)response.StatusCode}; retry after UTC {now.Add(pause):O}", null, response.StatusCode);
                }
                response.EnsureSuccessStatusCode();
                if(requireFreshDate&&(response.Headers.Date is not {} date||date>time.GetUtcNow().AddSeconds(5)||time.GetUtcNow()-date>TimeSpan.FromSeconds(90)))
                    throw new InvalidDataException("Execution response HTTP Date missing, future or stale");
                var stream = await response.Content.ReadAsStreamAsync(ct);
                var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                health.Success("Binance REST", stopwatch.Elapsed.TotalMilliseconds, 180);
                return json;
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException && !ct.IsCancellationRequested)
            {
                health.Failure("Binance REST", ex.Message);
                if (attempt >= 3 || ex is HttpRequestException { StatusCode: { } status } && (int)status < 500 && (int)status is not (429 or 418)) throw;
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt) + Random.Shared.NextDouble()), time, ct);
            }
        }
    }
}
