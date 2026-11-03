using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PulletCryptoRadar.Infrastructure;

namespace PulletCryptoRadar.Host;

public sealed record ProviderRoute(string Name, DataCapability Capability, string? KeyVariable, int RequestCost);
public sealed class ProviderQuotaException : Exception;

public sealed class ProviderRouter(IOptions<StorageOptions> storage, TimeProvider clock)
{
    public static readonly ProviderRoute Gecko = new("CoinGecko Demo", DataCapability.MarketCapital, "PULLET_COINGECKO_API_KEY", 1);
    public static readonly ProviderRoute Cmc = new("CoinMarketCap Basic", DataCapability.MarketCapital, "PULLET_CMC_API_KEY", 2);
    public static readonly ProviderRoute Dex = new("DEXScreener", DataCapability.DexPairs, null, 1);
    public static readonly ProviderRoute Distribution = new("InsightX Free", DataCapability.HolderDistribution, "PULLET_INSIGHTX_API_KEY", 1);
    public static readonly ProviderRoute Clusters = Distribution with { Capability = DataCapability.WalletCluster };
    private readonly Dictionary<(string, DataCapability), (int Failures, DateTimeOffset Retry)> circuits = [];
    private string Ledger => Path.Combine(storage.Value.Directory, "foundation-quota.json");

    public sealed record Usage(DateOnly Day, int Requests, DateTimeOffset BlockUntil);

    private Dictionary<string, Usage> ReadLedger() => File.Exists(Ledger)
        ? JsonSerializer.Deserialize<Dictionary<string, Usage>>(File.ReadAllText(Ledger)) ?? throw new JsonException()
        : [];

    private void SaveLedger(Dictionary<string, Usage> entries)
    {
        Directory.CreateDirectory(storage.Value.Directory);
        var temporary = Ledger + ".tmp";
        using (var file = File.Create(temporary))
        {
            JsonSerializer.Serialize(file, entries);
            file.Flush(true);
        }
        File.Move(temporary, Ledger, true);
    }

    private bool Reserve(string provider, int cost, DateTimeOffset now)
    {
        try
        {
            var entries = ReadLedger();
            var day = DateOnly.FromDateTime(now.UtcDateTime);
            var previous = entries.GetValueOrDefault(provider) ?? new Usage(day, 0, default);
            var requests = previous.Day == day ? previous.Requests : 0;
            var dailyLimit = provider == Distribution.Name ? 24 : 32;
            if (previous.BlockUntil > now || requests < 0 || requests > dailyLimit - cost) return false;
            entries[provider] = new(day, requests + cost, previous.BlockUntil);
            SaveLedger(entries);
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return false; }
    }

    public void RecordInsightQuota(HttpResponseMessage response)
    {
        var now = clock.GetUtcNow();
        var monthEnd = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1);
        var minuteEnd = now.AddSeconds(60 - now.Second);
        int? Remaining(string header) => response.Headers.TryGetValues(header, out var values) &&
            int.TryParse(values.FirstOrDefault(), out var count) && count >= 0 ? count : null;
        var ipMinute = Remaining("X-RateLimit-IP-Minute-Remaining");
        var ipMonth = Remaining("X-RateLimit-IP-Month-Remaining");
        var accountMinute = Remaining("X-RateLimit-Minute-Remaining");
        var accountMonth = Remaining("X-Quota-Month-Remaining");
        var blockUntil = default(DateTimeOffset);
        if ((int)response.StatusCode == 429)
            blockUntil = response.Headers.RetryAfter?.Date ??
                (response.Headers.RetryAfter?.Delta is { } delay ? now + delay : monthEnd);
        else if (response.IsSuccessStatusCode)
        {
            if (ipMinute is null || ipMonth is null || accountMinute is null || accountMonth is null ||
                ipMonth <= 200 || accountMonth <= 200) blockUntil = monthEnd;
            else if (ipMinute <= 1 || accountMinute <= 1) blockUntil = minuteEnd;
        }
        if (blockUntil <= now) return;
        try
        {
            var entries = ReadLedger();
            var usage = entries[Distribution.Name];
            entries[Distribution.Name] = usage with { BlockUntil = blockUntil };
            SaveLedger(entries);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { throw new ProviderQuotaException(); }
        if (response.IsSuccessStatusCode && (ipMinute is null || ipMonth is null || accountMinute is null || accountMonth is null))
            throw new ProviderQuotaException();
    }

    public async Task<DataObservation<T>> Observe<T>(ProviderRoute route,
        Func<string?, Task<(T? Value, DateTimeOffset? Source, DateTimeOffset? HttpDate)>> read, CancellationToken ct) where T : class
    {
        var (provider, capability, env, cost) = route;
        var now = clock.GetUtcNow();
        var key = env is null ? null : Environment.GetEnvironmentVariable(env) ?? (OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable(env, EnvironmentVariableTarget.User) : null);
        long? started = null;
        double? Elapsed() => started is { } tick ? Stopwatch.GetElapsedTime(tick).TotalMilliseconds : null;
        DataObservation<T> Failure(ProviderError error) => new(provider, capability, null, clock.GetUtcNow(), TimeQuality.ReceivedTimeOnly, error, null, Elapsed());
        if (env is not null && string.IsNullOrWhiteSpace(key)) return Failure(ProviderError.MissingKey);
        var circuitKey = (provider, capability);
        var state = circuits.GetValueOrDefault(circuitKey);
        if (state.Retry > now) return Failure(ProviderError.Cooldown);
        if (!Reserve(provider, cost, now)) return Failure(ProviderError.QuotaExhausted);
        ProviderError error;
        try
        {
            started = Stopwatch.GetTimestamp();
            var (value, source, httpDate) = await read(key);
            circuits.Remove(circuitKey);
            return new(provider, capability, source ?? httpDate, clock.GetUtcNow(),
                source.HasValue ? TimeQuality.ProviderTimestamp : httpDate.HasValue ? TimeQuality.HttpDate : TimeQuality.ReceivedTimeOnly,
                value is null ? ProviderError.NoData : ProviderError.None, value, Elapsed());
        }
        catch (ProviderQuotaException) { return Failure(ProviderError.QuotaExhausted); }
        catch (HttpRequestException ex)
        {
            error = (int?)ex.StatusCode switch
            {
                401 => ProviderError.Unauthorized, 402 or 403 => ProviderError.Forbidden,
                429 => ProviderError.RateLimited, 404 => ProviderError.NoData, _ => ProviderError.Transport
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { error = ProviderError.Timeout; }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException) { error = ProviderError.SchemaChanged; }
        var failures = state.Failures + 1;
        circuits[circuitKey] = (failures, failures >= 3 || error is ProviderError.RateLimited or ProviderError.Forbidden or ProviderError.Unauthorized
            ? now.AddMinutes(15) : now.AddMinutes(1));
        return Failure(error);
    }

}
