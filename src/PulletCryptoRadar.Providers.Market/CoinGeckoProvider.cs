using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Providers.Market;

public sealed class CoinGeckoOptions
{
    public bool Enabled { get; set; } = true;
    public string BaseUrl { get; set; } = "https://api.coingecko.com/api/v3/";
    public int PollMinutes { get; set; } = 30;
    public int CatalogHours { get; set; } = 24;
    public int RequestsPerMinute { get; set; } = 10;
    public int BatchSize { get; set; } = 100;
    public bool IsValid() => PollMinutes is >= 30 and <= 1440 && CatalogHours is >= 1 and <= 168
        && RequestsPerMinute is >= 1 and <= 30 && BatchSize is >= 1 and <= 100 && Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.Host is "api.coingecko.com" or "pro-api.coingecko.com" && BaseUrl.EndsWith('/')
        && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;
}
public sealed class CoinGeckoPacer(TimeProvider time, IOptions<CoinGeckoOptions> options)
{
    private readonly SemaphoreSlim _gate = new(1, 1); private DateTimeOffset _next, _paused;
    private readonly object _pauseGate = new();
    public async Task WaitAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            while (true)
            {
                DateTimeOffset until;
                lock (_pauseGate) until = _next > _paused ? _next : _paused;
                if (until <= time.GetUtcNow()) break;
                await Task.Delay(until - time.GetUtcNow(), time, ct);
            }
            _next = time.GetUtcNow().AddMinutes(1d / options.Value.RequestsPerMinute);
        }
        finally { _gate.Release(); }
    }
    public Task PauseAsync(TimeSpan duration, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_pauseGate) { var until = time.GetUtcNow() + duration; if (until > _paused) _paused = until; }
        return Task.CompletedTask;
    }
}
public sealed class CoinGeckoProvider(HttpClient http, CoinGeckoPacer pacer, TimeProvider time) : IMarketCapitalProvider
{
    public async Task<CoinCatalog> GetCatalogAsync(CancellationToken ct)
    {
        using var json = await GetAsync("coins/list?include_platform=true", ct);
        return new(time.GetUtcNow(), CoinGeckoParser.Catalog(json.RootElement));
    }
    public async Task<IReadOnlyList<MarketCapitalSnapshot>> GetMarketsAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        if (ids.Count is < 1 or > 100) throw new ArgumentException("CoinGecko 每批必须为 1～100 个 ID");
        using var json = await GetAsync($"coins/markets?vs_currency=usd&ids={Uri.EscapeDataString(string.Join(',', ids))}&per_page=100&page=1&sparkline=false", ct);
        return CoinGeckoParser.Markets(json.RootElement, time.GetUtcNow()).Where(s => ids.Contains(s.CoinGeckoId, StringComparer.Ordinal)).ToArray();
    }
    private async Task<JsonDocument> GetAsync(string path, CancellationToken ct)
    {
        await pacer.WaitAsync(ct);
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        var key = Environment.GetEnvironmentVariable("PULLET_COINGECKO_API_KEY");
        if (!string.IsNullOrWhiteSpace(key)) request.Headers.Add(http.BaseAddress?.Host == "pro-api.coingecko.com" ? "x-cg-pro-api-key" : "x-cg-demo-api-key", key);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var pause = response.Headers.RetryAfter?.Delta ?? response.Headers.RetryAfter?.Date - time.GetUtcNow() ?? TimeSpan.FromMinutes(2);
            if (pause < TimeSpan.FromSeconds(60)) pause = TimeSpan.FromSeconds(60);
            await pacer.PauseAsync(pause, ct);
            throw new HttpRequestException($"CoinGecko HTTP 429；冷却至 UTC {time.GetUtcNow().Add(pause):O}", null, response.StatusCode);
        }
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }
}
public static class CoinGeckoParser
{
    public static IReadOnlyList<MarketCoin> Catalog(JsonElement root)
    {
        var coins = new List<MarketCoin>();
        foreach (var element in root.EnumerateArray())
        {
            var id = Text(element, "id"); var symbol = Text(element, "symbol"); var name = Text(element, "name");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(symbol) || string.IsNullOrWhiteSpace(name)) continue;
            var platforms = new Dictionary<string, string>(StringComparer.Ordinal);
            if (element.TryGetProperty("platforms", out var p) && p.ValueKind == JsonValueKind.Object)
                foreach (var platform in p.EnumerateObject()) if (platform.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(platform.Value.GetString())) platforms[platform.Name] = platform.Value.GetString()!;
            coins.Add(new(id, symbol, name, platforms));
        }
        return coins.GroupBy(c => c.Id, StringComparer.Ordinal).Select(g => g.Last()).ToArray();
    }
    public static IReadOnlyList<MarketCapitalSnapshot> Markets(JsonElement root, DateTimeOffset received)
    {
        var result = new List<MarketCapitalSnapshot>();
        foreach (var e in root.EnumerateArray())
        {
            var id = Text(e, "id");
            if (string.IsNullOrWhiteSpace(id) || !DateTimeOffset.TryParse(Text(e, "last_updated"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var source) || source > received) continue;
            result.Add(new(id, source, received, Number(e, "market_cap"), Number(e, "fully_diluted_valuation"), Number(e, "total_volume"),
                Number(e, "circulating_supply"), Number(e, "total_supply"), Number(e, "max_supply"), Number(e, "ath")));
        }
        return result;
    }
    private static string? Text(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static decimal? Number(JsonElement e, string key) => e.TryGetProperty(key, out var v) && decimal.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number >= 0 ? number : null;
}
