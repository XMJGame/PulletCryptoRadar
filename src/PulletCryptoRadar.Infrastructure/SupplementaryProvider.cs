using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Infrastructure;

public sealed class SupplementaryProvider(HttpClient http, IRequestLedger ledger, TimeProvider clock) : ISupplementaryProvider
{
    public async Task<ProviderRead> ReadAsync(AssetIdentity asset, string capability, CancellationToken token)
    {
        var chain = ResearchNetworks.Single(asset);
        var (provider, url, variable, header) = capability switch
        {
            "GeckoIdentity" => ("CoinGecko",asset.CoinGeckoId is { } identityId ?
                "https://api.coingecko.com/api/v3/coins/"+Uri.EscapeDataString(identityId)+"?localization=false&tickers=false&market_data=false&community_data=false&developer_data=false" : null,
                "PULLET_COINGECKO_API_KEY","x-cg-demo-api-key"),
            "Gecko" => ("CoinGecko", asset.CoinGeckoId is { } id
                ? "https://api.coingecko.com/api/v3/coins/markets?vs_currency=usd&ids=" + Uri.EscapeDataString(id) : null,
                "PULLET_COINGECKO_API_KEY", "x-cg-demo-api-key"),
            "Cmc" => ("CoinMarketCap", asset.CoinMarketCapId is { } id
                ? "https://pro-api.coinmarketcap.com/v3/cryptocurrency/quotes/latest?id=" + id : null,
                "PULLET_CMC_API_KEY", "X-CMC_PRO_API_KEY"),
            "Dex" => ("DEXScreener", chain is not null ? "https://api.dexscreener.com/token-pairs/v1/"+ResearchNetworks.Dex(chain.Chain)+"/" + chain.Address : null, null, null),
            "Distribution" or "Clusters" => ("InsightX", chain is not null
                ? "https://api.insightx.network/dex-metrics/v1/"+ResearchNetworks.Insight(chain.Chain)+"/" + chain.Address + "/" + capability.ToLowerInvariant() : null,
                "PULLET_INSIGHTX_API_KEY", "X-API-Key"),
            _ => throw new ArgumentException("Unsupported supplementary capability.")
        };
        var requestId = Guid.NewGuid();
        RequestDecision? decision=null;
        DateTimeOffset? cooldown = null;
        DateTimeOffset? responseDate = null;
        ProviderRead Failure(CollectionFailure failure, int? status = null, string? body = null) =>
            new(new(requestId,provider,asset.BinanceSymbol,url??capability,clock.GetUtcNow(),status,failure,body,responseDate,
                failure==CollectionFailure.Quota?decision?.Reason:failure==CollectionFailure.None?null:failure.ToString(),
                decision is {Allowed:false}?decision.RetryAt:cooldown,RequestPriority.Background),[]);
        if (url is null) return Failure(CollectionFailure.Unsupported);
        var key = variable is null ? null : Environment.GetEnvironmentVariable(variable) ??
            (OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable(variable, EnvironmentVariableTarget.User) : null);
        if (variable is not null && string.IsNullOrWhiteSpace(key)) return Failure(CollectionFailure.MissingKey);
        const int maximumBytes = 262144;
        decision=ledger.TryReserve(requestId,provider,clock.GetUtcNow(),maximumBytes,RequestPriority.Background);
        if(!decision.Allowed) return Failure(CollectionFailure.Quota);
        var charged = maximumBytes;
        string? cooldownReason=null;
        int? status = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (header is not null) request.Headers.Add(header, key);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            responseDate=response.Headers.Date;
            status = (int)response.StatusCode;
            var now = clock.GetUtcNow();
            if (provider == "InsightX")
            {
                var monthEnd = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1);
                long? Remaining(string name) => response.Headers.TryGetValues(name, out var values) &&
                    long.TryParse(values.FirstOrDefault(), out var count) && count >= 0 ? count : null;
                var accountMonth = Remaining("X-Quota-Month-Remaining");
                var ipMonth = Remaining("X-RateLimit-IP-Month-Remaining");
                var accountMinute = Remaining("X-RateLimit-Minute-Remaining");
                var ipMinute = Remaining("X-RateLimit-IP-Minute-Remaining");
                if (accountMonth is null or <= 200 || ipMonth is null or <= 200 ||
                    accountMinute is null || ipMinute is null) {cooldown = monthEnd;cooldownReason="ProviderQuotaHeadersUncertainOrLow";}
                else if (accountMinute <= 1 || ipMinute <= 1) {cooldown = now.AddMinutes(1);cooldownReason="ProviderMinuteHeadroomLow";}
            }
            if (status is 401 or 402 or 403 or 429)
            {
                if (provider == "InsightX" && status == 429)
                    cooldown = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1);
                else cooldown = response.Headers.RetryAfter?.Date ??
                    now.Add(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(15));
                cooldownReason=status==429?"CooldownAfterRateLimit":"CooldownAfterForbidden";
                return Failure(status == 429 ? CollectionFailure.RateLimited : CollectionFailure.Forbidden, status);
            }
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var bytes = new MemoryStream();
            var buffer = new byte[4096];
            int read;
            while ((read = await stream.ReadAsync(buffer, token)) > 0)
            {
                if (bytes.Length + read > maximumBytes) return Failure(CollectionFailure.Invalid, status);
                await bytes.WriteAsync(buffer.AsMemory(0, read), token);
            }
            charged = (int)bytes.Length;
            var body = Encoding.UTF8.GetString(bytes.GetBuffer(), 0, charged);
            if (!response.IsSuccessStatusCode) return Failure(CollectionFailure.Transport, status, body);
            try
            {
                if (capability == "GeckoIdentity") return new(new(requestId,provider,asset.BinanceSymbol,url,
                    clock.GetUtcNow(),status,CollectionFailure.None,body),[]);
                var parsed = Parse(asset, capability, body, clock.GetUtcNow(), response.Headers.Date, requestId);
                return parsed with { Evidence = parsed.Evidence with { Endpoint = url,ResponseDate=responseDate,Priority=RequestPriority.Background } };
            }
            catch (Exception error) when (error is JsonException or InvalidDataException or InvalidOperationException or
                ArgumentException or OverflowException or FormatException or KeyNotFoundException)
            {
                return Failure(CollectionFailure.Invalid, status, body);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            cooldown = clock.GetUtcNow().AddMinutes(1);
            cooldownReason="CooldownAfterTimeout";
            return Failure(CollectionFailure.Timeout, status);
        }
        catch (Exception error) when (error is HttpRequestException or IOException)
        {
            cooldown = clock.GetUtcNow().AddMinutes(1);
            cooldownReason="CooldownAfterTransport";
            return Failure(CollectionFailure.Transport, status);
        }
        finally { ledger.CompleteWithReason(requestId,charged,cooldown,cooldownReason); }
    }

    public static ProviderRead Parse(AssetIdentity asset, string capability, string body, DateTimeOffset received,
        DateTimeOffset? httpDate, Guid requestId)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var observations = new List<Observation>();
        var pools = new List<DexPool>();
        var clusters = new List<WalletCluster>();
        var provider = capability switch { "Gecko" => "CoinGecko", "Cmc" => "CoinMarketCap", "Dex" => "DEXScreener", _ => "InsightX" };
        decimal? Number(JsonElement row, string name)
        {
            if (!row.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var result)) return result;
            if (value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out result)) return result;
            throw new InvalidDataException("Unexpected numeric schema.");
        }
        decimal Required(JsonElement row, string name, decimal maximum)
        {
            var value = Number(row, name);
            if (value is null || value < 0 || value > maximum) throw new InvalidDataException("Metric outside valid range.");
            return value.Value;
        }
        DateTimeOffset? Timestamp(JsonElement row) => row.TryGetProperty("last_updated", out var time) &&
            time.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(time.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
        void Add(Metric metric, decimal? value, DateTimeOffset? source, string methodology)
        {
            if (value < 0 || source > received) throw new InvalidDataException("Invalid metadata evidence.");
            observations.Add(new(asset.AssetId, metric, value, provider, methodology, source, received,
                value is null ? ObservationStatus.Missing : ObservationStatus.Valid,
                source is not null ? TimeQuality.ProviderTime : TimeQuality.ReceivedOnly,
                capability is "Distribution" or "Clusters" ? ResearchNetworks.Single(asset)?.Chain : null,
                capability is "Distribution" or "Clusters" ? ResearchNetworks.Single(asset)?.Address : null));
        }
        if (capability == "Gecko")
        {
            var matches = root.EnumerateArray().Where(item => item.GetProperty("id").GetString() == asset.CoinGeckoId).ToArray();
            if (matches.Length != 1) throw new InvalidDataException("CoinGecko asset identity mismatch.");
            var row = matches[0];
            var time = Timestamp(row);
            Add(Metric.MarketCap, Number(row, "market_cap"), time, "reported-circulating-market-cap");
            Add(Metric.Fdv, Number(row, "fully_diluted_valuation"), time, "reported-fdv");
            Add(Metric.CirculatingSupply, Number(row, "circulating_supply"), time, "provider-circulating-supply");
        }
        else if (capability == "Cmc")
        {
            var row = root.GetProperty("data").EnumerateArray().Single(item => Number(item, "id") == asset.CoinMarketCapId);
            var usd = row.GetProperty("quote").EnumerateArray().Single(item => item.GetProperty("symbol").GetString() == "USD");
            var time = Timestamp(usd);
            Add(Metric.MarketCap, Number(usd, "market_cap"), time, "reported-circulating-market-cap");
            Add(Metric.Fdv, Number(usd, "fully_diluted_market_cap"), time, "reported-fdv");
            Add(Metric.CirculatingSupply, Number(row, "circulating_supply"), time, "provider-circulating-supply");
        }
        else if (capability == "Dex")
        {
            // HTTP Date is response metadata, never proof of when liquidity was observed.
            var usableHttpDate=httpDate<=received?httpDate:null;
            var token = ResearchNetworks.Single(asset) ?? throw new InvalidDataException("Select an unambiguous supported chain.");
            var contract = token.Address;
            var trusted = new HashSet<string>(token.Chain == ChainId.Solana ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
            foreach (var address in token.Chain switch
            {
                ChainId.Bsc => new[] {
                "0xbb4CdB9CBd36B01bD1cBaEBF2De08d9173bc095c",
                "0x55d398326f99059ff775485246999027b3197955",
                "0x8ac76a51cc950d9822d68b83fe1ad97b32cd580d" },
                ChainId.Ethereum => ["0xc02aaa39b223fe8d0a0e5c4f27ead9083c756cc2","0xa0b86991c6218b36c1d19d4a2e9eb0ce3606eb48"],
                ChainId.Solana => ["So11111111111111111111111111111111111111112","EPjFWdd5AufqSSqeM2qN1xzybapC8G4wEGGkZwyTDt1v"],
                _ => []
            }) trusted.Add(address);
            foreach (var row in root.EnumerateArray())
            {
                if (row.GetProperty("chainId").GetString() != ResearchNetworks.Dex(token.Chain)) continue;
                var pair = row.GetProperty("pairAddress").GetString()!;
                var baseAddress = row.GetProperty("baseToken").GetProperty("address").GetString()!;
                var quoteAddress = row.GetProperty("quoteToken").GetProperty("address").GetString()!;
                // A v4 pool ID can be bytes32 rather than a token/pair address. Retain raw rows,
                // but only supported address-based pools contribute to this conservative metric.
                if(!IdentityValidation.IsAddress(token.Chain,pair)) continue;
                if (!IdentityValidation.IsAddress(token.Chain, baseAddress) ||
                    !IdentityValidation.IsAddress(token.Chain, quoteAddress)) throw new InvalidDataException("Invalid DEX address.");
                var targetIsBase = ResearchNetworks.SameAddress(token.Chain,baseAddress,contract);
                if (!targetIsBase && !ResearchNetworks.SameAddress(token.Chain,quoteAddress,contract)) continue;
                var liquidity = row.TryGetProperty("liquidity", out var liquidityRow) ? Number(liquidityRow, "usd") : null;
                var price = targetIsBase ? Number(row, "priceUsd") : null;
                if (liquidity < 0 || price < 0) throw new InvalidDataException("Invalid DEX amount.");
                if (pools.Any(pool => ResearchNetworks.SameAddress(token.Chain,pool.Pair,pair))) continue;
                pools.Add(new(ResearchNetworks.Dex(token.Chain), pair, row.GetProperty("dexId").GetString()!, baseAddress, quoteAddress,
                    targetIsBase, liquidity, price, trusted.Contains(targetIsBase ? quoteAddress : baseAddress)));
            }
            var primary = pools.Where(pool => pool.VerifiedCounterAsset && pool.LiquidityUsd is > 0).MaxBy(pool => pool.LiquidityUsd);
            observations.Add(new(asset.AssetId, Metric.DexLiquidity, primary?.LiquidityUsd, provider,
                "supported-address-pools-verified-counterasset-primary", usableHttpDate, received,
                primary is null ? ObservationStatus.Missing : ObservationStatus.Valid,
                usableHttpDate is null ? TimeQuality.ReceivedOnly : TimeQuality.HttpDate, token.Chain, contract));
        }
        else if (capability == "Distribution")
        {
            var gini = Required(root, "gini", 1);
            var hhi = Required(root, "hhi", 1);
            var nakamoto = Required(root, "nakamoto", int.MaxValue);
            var top10 = Required(root, "top_10_holder_concentration", 100);
            if (nakamoto != decimal.Truncate(nakamoto)) throw new InvalidDataException("Non-integral Nakamoto coefficient.");
            if (nakamoto == 0 || top10 == 0)
            {
                foreach (var metric in new[] { Metric.Gini,Metric.Hhi,Metric.Nakamoto,Metric.Top10 })
                    Add(metric,null,null,"provider-distribution-no-holder-coverage");
            }
            else
            {
            Add(Metric.Gini,gini,null,"provider-distribution");
            Add(Metric.Hhi,hhi,null,"provider-distribution");
            Add(Metric.Nakamoto, nakamoto, null, "provider-distribution");
            Add(Metric.Top10,top10,null,"provider-distribution");
            }
        }
        else if (capability == "Clusters")
        {
            var total = Required(root,"total_cluster_pct",100);
            Add(Metric.TotalClusterPercent,total,null,"provider-inferred-relationship");
            var token = ResearchNetworks.Single(asset) ?? throw new InvalidDataException("Select a supported chain.");
            var seen = new HashSet<string>(token.Chain == ChainId.Solana ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
            foreach (var row in root.GetProperty("clusters").EnumerateArray())
            {
                var percent = Required(row, "pct", 100);
                var addresses = row.GetProperty("cluster_addresses").EnumerateArray().Select(item =>
                {
                    var address = item.GetProperty("address").GetString()!;
                    if (!IdentityValidation.IsAddress(token.Chain, address)) throw new InvalidDataException("Invalid cluster address.");
                    if (!seen.Add(address)) throw new InvalidDataException("Repeated cluster address would double-count ownership.");
                    Required(item, "percentage", 100);
                    return address;
                }).ToArray();
                var tags = row.GetProperty("tags").EnumerateArray().Select(item => item.GetString() ?? "").ToArray();
                clusters.Add(new(percent, addresses, tags));
            }
            if (clusters.Sum(cluster => cluster.Percent) > 100.01m || Math.Abs(clusters.Sum(cluster => cluster.Percent)-total) > .01m)
                throw new InvalidDataException("Cluster total does not reconcile.");
        }
        else throw new ArgumentException("Unsupported supplementary capability.");
        return new(new(requestId, provider, asset.BinanceSymbol, capability, received, 200, CollectionFailure.None, body),
            observations, pools.Count > 0 ? pools : null, clusters.Count > 0 ? clusters : null);
    }
}
