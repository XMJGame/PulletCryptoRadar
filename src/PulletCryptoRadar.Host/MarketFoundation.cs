using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;

namespace PulletCryptoRadar.Host;

public enum DataCapability { MarketCapital, DexPairs, HolderDistribution, WalletCluster }
public enum TimeQuality { ProviderTimestamp, HttpDate, ReceivedTimeOnly }
public enum ProviderError { None, MissingKey, NoData, Unauthorized, Forbidden, RateLimited, QuotaExhausted, Timeout, Transport, SchemaChanged, Cooldown, NotRequested }
public sealed record DataObservation<T>(string Provider, DataCapability Capability, DateTimeOffset? SourceTime,
    DateTimeOffset ReceivedTime, TimeQuality TimeQuality, ProviderError Error, T? Value, double? ElapsedMilliseconds = null);
public sealed record MarketCapital(decimal? MarketCap, decimal? Fdv, decimal? CirculatingSupply);
public sealed record DexPair(string DexId, string PairAddress, string QuoteToken, decimal? PriceUsd,
    decimal? LiquidityUsd, decimal? Volume5m, decimal? Volume1h, decimal? Volume6h, decimal? Volume24h,
    decimal? Buys5m, decimal? Sells5m, decimal? Change5m, decimal? MarketCap, decimal? Fdv, long? CreatedAtMs, bool TargetIsBase = true);

public sealed class MarketFoundation(IHttpClientFactory clients, ICapitalRuntimeStore capital, TimeProvider clock,
    IOptions<StorageOptions> storage, ProviderRouter router)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private static readonly Dictionary<string, string> Chains = new()
    {
        ["ethereum"] = "ethereum", ["binance-smart-chain"] = "bsc", ["solana"] = "solana",
        ["base"] = "base", ["arbitrum-one"] = "arbitrum", ["polygon-pos"] = "polygon"
    };

    public async Task<IResult> Collect(string coinId, string chain, bool includeOwnership, CancellationToken ct, bool scheduled = false, bool compareMarketCapital = false)
    {
        var coin = capital.Catalog?.Coins.FirstOrDefault(c => c.Id == coinId);
        if (coin is null || !Chains.TryGetValue(chain, out var dexChain) ||
            !coin.Platforms.TryGetValue(chain, out var address) || string.IsNullOrWhiteSpace(address))
            return Results.BadRequest(new { error = "请指定目录中的项目 ID 和有合约的支持链。" });
        if (!await gate.WaitAsync(0, ct)) return Results.StatusCode(429);
        try
        {
            var gecko = await router.Observe(ProviderRouter.Gecko, async key =>
            {
                var (json, date) = await Get("https://api.coingecko.com/api/v3/coins/markets?vs_currency=usd&ids=" +
                    Uri.EscapeDataString(coin.Id), "x-cg-demo-api-key", key, ct);
                foreach (var row in json.EnumerateArray())
                    if (Str(row, "id") == coin.Id)
                        return (new MarketCapital(Num(row, "market_cap"), Num(row, "fully_diluted_valuation"),
                            Num(row, "circulating_supply")), Timestamp(row, "last_updated"), date);
                return (null, null, date);
            }, ct);
            var useCmc = compareMarketCapital || !MarketCapitalResolver.IsUsable(gecko, clock.GetUtcNow());
            var cmc = useCmc ? await router.Observe(ProviderRouter.Cmc, async key =>
            {
                const string root = "https://pro-api.coinmarketcap.com";
                const string header = "X-CMC_PRO_API_KEY";
                var symbol = coin.Symbol.ToUpperInvariant();
                var (info, date) = await Get(root + "/v2/cryptocurrency/info?symbol=" + Uri.EscapeDataString(symbol), header, key, ct);
                var candidates = Child(Child(info, "data"), symbol);
                if (candidates.ValueKind != JsonValueKind.Array) return (null, null, date);
                var matches = candidates.EnumerateArray().Where(item =>
                    Child(item, "contract_address") is { ValueKind: JsonValueKind.Array } contracts &&
                    contracts.EnumerateArray().Any(c => Same(chain, Str(c, "contract_address"), address) &&
                        CmcChain(chain) == Str(Child(Child(c, "platform"), "coin"), "slug"))).ToArray();
                if (matches.Length != 1 || Child(matches[0], "id") is not { ValueKind: JsonValueKind.Number } idValue ||
                    !idValue.TryGetInt32(out var id) || id <= 0) return (null, null, date);
                var (quotes, quoteDate) = await Get(root + "/v2/cryptocurrency/quotes/latest?id=" + id, header, key, ct);
                var row = quotes.GetProperty("data").GetProperty(id.ToString(CultureInfo.InvariantCulture));
                var usd = row.GetProperty("quote").GetProperty("USD");
                return (new MarketCapital(Num(usd, "market_cap"), Num(usd, "fully_diluted_market_cap"),
                    Num(row, "circulating_supply")), Timestamp(usd, "last_updated"), quoteDate);
            }, ct) : new DataObservation<MarketCapital>(ProviderRouter.Cmc.Name, DataCapability.MarketCapital,
                null, clock.GetUtcNow(), TimeQuality.ReceivedTimeOnly, ProviderError.NotRequested, null);
            var dex = await router.Observe(ProviderRouter.Dex, async _ =>
            {
                var (json, date) = await Get($"https://api.dexscreener.com/token-pairs/v1/{dexChain}/{Uri.EscapeDataString(address)}", null, null, ct);
                var pairs = ReadPairs(json, chain, dexChain, address);
                return (pairs.Length > 0 ? pairs : null, (DateTimeOffset?)null, date);
            }, ct);
            var insightNetwork = chain switch
            {
                "ethereum" => "eth", "binance-smart-chain" => "bsc",
                "solana" => "sol", "base" => "base", _ => null
            };
            DataObservation<JsonNode>? distribution = null;
            DataObservation<JsonNode>? clusters = null;
            if (includeOwnership && insightNetwork is not null)
            {
                var url = $"https://api.insightx.network/dex-metrics/v1/{insightNetwork}/{Uri.EscapeDataString(address)}";
                distribution = await ReadOwnership(ProviderRouter.Distribution, url + "/distribution", ct);
                clusters = await ReadOwnership(ProviderRouter.Clusters, url + "/clusters", ct);
            }
            var now = clock.GetUtcNow();
            var primary = dex.Value?.Where(p => p.TargetIsBase && p.LiquidityUsd is > 0).MaxBy(p => p.LiquidityUsd);
            var evidence = new { shadowOnly = true, origin = scheduled ? "Scheduled" : "Manual", roundId = Guid.NewGuid(), coinId, chain, address, observedAt = now, gecko, cmc, dex,
                ownership = new { distribution, clusters, relationship = "ProviderInferredRelationship" },
                marketCapital = MarketCapitalResolver.Resolve(gecko, cmc, now),
                primaryPair = primary, primaryLiquidityUsd = primary?.LiquidityUsd,
                volumeToLiquidity = primary?.Volume24h / primary?.LiquidityUsd };
            Directory.CreateDirectory(storage.Value.Directory);
            await File.AppendAllTextAsync(Path.Combine(storage.Value.Directory, "market-foundation-shadow.jsonl"),
                JsonSerializer.Serialize(evidence) + Environment.NewLine, ct);
            return Results.Ok(evidence);
        }
        finally { gate.Release(); }
    }

    private Task<DataObservation<JsonNode>> ReadOwnership(ProviderRoute route, string url, CancellationToken ct) =>
        router.Observe<JsonNode>(route, async key =>
        {
            var (json, date) = await Get(url, "X-API-Key", key, ct);
            if (json.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                throw new JsonException("Unexpected ownership payload.");
            return (JsonNode.Parse(json.GetRawText()), null, date);
        }, ct);

    private async Task<(JsonElement Json, DateTimeOffset? Date)> Get(string url, string? header, string? key, CancellationToken ct)
    {
        using var client = clients.CreateClient("MarketFoundation");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (header is not null && key is not null) request.Headers.Add(header, key);
        using var response = await client.SendAsync(request, ct);
        if (header == "X-API-Key") router.RecordInsightQuota(response);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        return (document.RootElement.Clone(), response.Headers.Date);
    }

    private static string CmcChain(string chain) => chain switch
    {
        "binance-smart-chain" => "bnb", "arbitrum-one" => "arbitrum", "polygon-pos" => "polygon", _ => chain
    };
    private static bool Same(string chain, string? left, string right) =>
        string.Equals(left, right, chain == "solana" ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    private static string? Str(JsonElement row, string key) =>
        row.ValueKind == JsonValueKind.Object && row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static DateTimeOffset? Timestamp(JsonElement row, string key) =>
        DateTimeOffset.TryParse(Str(row, key), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;
    private static decimal? Num(JsonElement row, string key)
    {
        if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty(key, out var value)) return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) ? number :
            value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out number) ? number : null;
    }
    private static JsonElement Child(JsonElement row, string key) =>
        row.ValueKind == JsonValueKind.Object && row.TryGetProperty(key, out var child) ? child : default;
    public static DexPair[] ReadPairs(JsonElement json, string chain, string dexChain, string address) =>
        json.EnumerateArray()
            .Where(pair => Str(pair, "chainId") == dexChain && !string.IsNullOrWhiteSpace(Str(pair, "pairAddress")))
            .Select(pair => new { Pair = pair,
                IsBase = Same(chain, Str(Child(pair, "baseToken"), "address"), address),
                IsQuote = Same(chain, Str(Child(pair, "quoteToken"), "address"), address) })
            .Where(pair => pair.IsBase || pair.IsQuote)
            .Select(pair => ParsePair(pair.Pair, pair.IsBase))
            .OrderByDescending(pair => pair.TargetIsBase)
            .ThenByDescending(pair => pair.LiquidityUsd)
            .DistinctBy(pair => chain == "solana" ? pair.PairAddress : pair.PairAddress.ToLowerInvariant())
            .ToArray();

    private static DexPair ParsePair(JsonElement pair, bool targetIsBase)
    {
        var volume = Child(pair, "volume");
        var tx = Child(Child(pair, "txns"), "m5");
        var created = Child(pair, "pairCreatedAt");
        return new(Str(pair, "dexId") ?? "", Str(pair, "pairAddress")!,
            Str(Child(pair, "quoteToken"), "address") ?? "", targetIsBase ? Num(pair, "priceUsd") : null,
            Num(Child(pair, "liquidity"), "usd"), Num(volume, "m5"), Num(volume, "h1"),
            Num(volume, "h6"), Num(volume, "h24"), targetIsBase ? Num(tx, "buys") : null, targetIsBase ? Num(tx, "sells") : null,
            targetIsBase ? Num(Child(pair, "priceChange"), "m5") : null,
            targetIsBase ? Num(pair, "marketCap") : null, targetIsBase ? Num(pair, "fdv") : null,
            created.ValueKind == JsonValueKind.Number && created.TryGetInt64(out var ms) ? ms : null, targetIsBase);
    }
}
