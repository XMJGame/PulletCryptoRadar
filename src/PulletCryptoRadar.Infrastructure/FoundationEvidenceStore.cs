using Microsoft.Data.Sqlite;
using System.Text.Json;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Infrastructure;

public sealed partial class FoundationStore
{
    public void RetryJobEarlier(string name,DateTimeOffset retryAt)
    {
        using var connection=Open();using var command=connection.CreateCommand();
        command.CommandText="UPDATE V2Job SET NextAt=MIN(NextAt,$retry) WHERE Name=$name";
        command.Parameters.AddWithValue("$name",name);command.Parameters.AddWithValue("$retry",retryAt.ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
    }
    public bool ReserveJob(string name, DateTimeOffset now, TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero) throw new ArgumentException("Invalid job interval.");
        lock (writer)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO V2Job VALUES($name,$next)
                ON CONFLICT(Name) DO UPDATE SET NextAt=excluded.NextAt WHERE NextAt<=$now;
                """;
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$next", now.Add(interval).ToUnixTimeMilliseconds());
            var changed = command.ExecuteNonQuery() == 1;
            transaction.Commit();
            return changed;
        }
    }

    public bool Reserve(Guid requestId,string provider,DateTimeOffset now,int maximumBytes) =>
        TryReserve(requestId,provider,now,maximumBytes,RequestPriority.Critical).Allowed;

    public RequestDecision TryReserve(Guid requestId,string provider,DateTimeOffset now,int maximumBytes,RequestPriority priority)
    {
        now = now.ToUniversalTime();
        if (!Enum.IsDefined(priority) || maximumBytes is <= 0 or > 4_000_000 ||
            provider is not ("Binance" or "BinanceStream" or "BinanceArchive" or "CoinGecko" or "CoinMarketCap" or "DEXScreener" or "InsightX" or "GoPlus"))
            throw new ArgumentException("Invalid request reservation.");
        lock (writer)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            var at = now.ToUnixTimeMilliseconds();
            var day = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
            var month = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
            RequestDecision Deny(string reason,DateTimeOffset retry)
            {
                using var denied=connection.CreateCommand();denied.Transaction=transaction;
                denied.CommandText="INSERT INTO RequestDenial VALUES($id,$provider,$at,$reason,$retry,$priority)";
                denied.Parameters.AddWithValue("$id",requestId.ToString());denied.Parameters.AddWithValue("$provider",provider);
                denied.Parameters.AddWithValue("$at",at);denied.Parameters.AddWithValue("$reason",reason);
                denied.Parameters.AddWithValue("$retry",retry.ToUnixTimeMilliseconds());denied.Parameters.AddWithValue("$priority",(int)priority);
                denied.ExecuteNonQuery();transaction.Commit();return new(false,reason,retry,priority);
            }
            command.CommandText = "SELECT Until FROM V2Cooldown WHERE Provider=$provider";
            command.Parameters.AddWithValue("$provider", provider);
            if (command.ExecuteScalar() is long until && until > at)
            {
                command.CommandText="SELECT Reason FROM CooldownDetail WHERE Provider=$provider AND Until=$until";
                command.Parameters.AddWithValue("$until",until);
                return Deny(command.ExecuteScalar() as string ?? "ProviderCooldown",DateTimeOffset.FromUnixTimeMilliseconds(until));
            }
            command.CommandText = """
                SELECT COUNT(*) FROM V2Request WHERE Provider=$provider AND At>=$minute;
                """;
            command.Parameters.AddWithValue("$minute", at - 60000);
            var minuteLimit=provider=="Binance"?priority switch {RequestPriority.Background=>18,RequestPriority.Core=>24,_=>30}:provider=="GoPlus"?3:30;
            if ((long)command.ExecuteScalar()! >= minuteLimit)
            {
                command.CommandText="SELECT MIN(At) FROM V2Request WHERE Provider=$provider AND At>=$minute";
                var retry=DateTimeOffset.FromUnixTimeMilliseconds((long)command.ExecuteScalar()!+60001);
                return Deny(provider=="Binance"&&minuteLimit<30?"ReservedCriticalMinuteCapacity":"ProviderMinuteLimit",retry);
            }
            var monthlyCalls = provider switch { "CoinGecko" => 8000, "CoinMarketCap" => 12000, "InsightX" => 800, "GoPlus"=>1000, _ => int.MaxValue };
            command.CommandText = "SELECT COUNT(*) FROM V2Request WHERE Provider=$provider AND At>=$month";
            command.Parameters.AddWithValue("$month", month.ToUnixTimeMilliseconds());
            if ((long)command.ExecuteScalar()! >= monthlyCalls) return Deny("ProviderMonthlyCalls",month.AddMonths(1));
            if (provider == "InsightX")
            {
                command.CommandText = "SELECT COUNT(*) FROM V2Request WHERE Provider=$provider AND At>=$insightMinute";
                command.Parameters.AddWithValue("$insightMinute", at - 60000);
                if ((long)command.ExecuteScalar()! >= 4) return Deny("ProviderMinuteLimit",now.AddMinutes(1));
                command.CommandText = "SELECT COUNT(*) FROM V2Request WHERE Provider=$provider AND At>=$day";
                command.Parameters.AddWithValue("$day", day.ToUnixTimeMilliseconds());
                if ((long)command.ExecuteScalar()! >= 24) return Deny("ProviderDailyCalls",day.AddDays(1));
            }
            command.CommandText = "SELECT COALESCE(SUM(Bytes),0) FROM V2Request WHERE At>=$boundary";
            command.Parameters.AddWithValue("$boundary", day.ToUnixTimeMilliseconds());
            var dayUsage=(long)command.ExecuteScalar()!;
            if (dayUsage > 900_000_000 - maximumBytes) return Deny("DailyPayloadBudget",day.AddDays(1));
            var dayLimit=priority switch {RequestPriority.Background=>700_000_000,RequestPriority.Core=>850_000_000,_=>900_000_000};
            if(dayUsage>dayLimit-maximumBytes) return Deny("ReservedCriticalDailyCapacity",day.AddDays(1));
            command.Parameters["$boundary"].Value = month.ToUnixTimeMilliseconds();
            var monthUsage=(long)command.ExecuteScalar()!;
            if(monthUsage>30_000_000_000L-maximumBytes) return Deny("MonthlyPayloadBudget",month.AddMonths(1));
            var monthLimit=priority switch {RequestPriority.Background=>24_000_000_000L,RequestPriority.Core=>27_000_000_000L,_=>30_000_000_000L};
            if(monthUsage>monthLimit-maximumBytes) return Deny("ReservedCriticalMonthlyCapacity",month.AddMonths(1));
            command.CommandText = "INSERT INTO V2Request(Id, Provider, At, Bytes) VALUES($id,$provider,$at,$bytes)";
            command.Parameters.AddWithValue("$id", requestId.ToString());
            command.Parameters.AddWithValue("$at", at);
            command.Parameters.AddWithValue("$bytes", maximumBytes);
            command.ExecuteNonQuery();
            transaction.Commit();
            return new(true,"Allowed",null,priority);
        }
    }

    public void Complete(Guid requestId,int actualBytes,DateTimeOffset? cooldownUntil = null) =>
        CompleteWithReason(requestId,actualBytes,cooldownUntil,null);

    public void CompleteWithReason(Guid requestId,int actualBytes,DateTimeOffset? cooldownUntil,string? reason)
    {
        if (actualBytes < 0) throw new ArgumentException("Invalid response size.");
        lock (writer)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT Provider FROM V2Request WHERE Id=$id";
            command.Parameters.AddWithValue("$id", requestId.ToString());
            var provider = command.ExecuteScalar() as string ?? throw new InvalidOperationException("Missing reservation.");
            command.CommandText = "UPDATE V2Request SET Bytes=$bytes,Completed=1 WHERE Id=$id AND Completed=0";
            command.Parameters.AddWithValue("$bytes", actualBytes);
            command.ExecuteNonQuery();
            if (cooldownUntil is { } until)
            {
                command.CommandText = """
                    INSERT INTO V2Cooldown VALUES($provider,$until)
                    ON CONFLICT(Provider) DO UPDATE SET Until=MAX(Until,excluded.Until);
                    """;
                command.Parameters.AddWithValue("$provider", provider);
                command.Parameters.AddWithValue("$until", until.ToUnixTimeMilliseconds());
                command.ExecuteNonQuery();
                command.CommandText="""
                    INSERT INTO CooldownDetail VALUES($provider,$until,$reason)
                    ON CONFLICT(Provider) DO UPDATE SET Until=excluded.Until,Reason=excluded.Reason
                    WHERE excluded.Until>=CooldownDetail.Until;
                    """;
                command.Parameters.AddWithValue("$reason",reason??"ProviderCooldown");command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }

    public void Save(FeatureSnapshot snapshot, IReadOnlyList<ProviderRead> reads)
    {
        lock (writer)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            SaveReads(connection, transaction, reads);
            command.Parameters.Clear();
            command.CommandText = "INSERT INTO V2Feature VALUES($asset,$at,$payload)";
            command.Parameters.AddWithValue("$asset", snapshot.AssetId.ToString());
            command.Parameters.AddWithValue("$at", snapshot.AsOf.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(snapshot));
            command.ExecuteNonQuery();
            transaction.Commit();
        }
    }

    private static void SaveReads(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<ProviderRead> reads)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        foreach (var read in reads)
        {
            command.Parameters.Clear();
            command.CommandText = read.Evidence.Endpoint == "/fapi/v1/fundingInfo" && read.Evidence.Symbol == "*USDT-PERPETUAL*"
                ? "INSERT OR IGNORE INTO V2RawEvidence VALUES($id,$provider,$at,$payload)"
                : "INSERT INTO V2RawEvidence VALUES($id,$provider,$at,$payload)";
            command.Parameters.AddWithValue("$id", read.Evidence.Id.ToString());
            command.Parameters.AddWithValue("$provider", read.Evidence.Provider);
            command.Parameters.AddWithValue("$at", read.Evidence.ReceivedTime.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(read.Evidence.Endpoint == "/fapi/v1/fundingInfo"
                && read.Evidence.Symbol == "*USDT-PERPETUAL*" ? read with { Observations = [] } : read));
            command.ExecuteNonQuery();
            if (read.Evidence.Provider == "Binance" && read.Evidence.Failure == CollectionFailure.None && read.Candles is { Count: > 0 } candles)
            {
                command.Parameters.Clear();
                command.CommandText = """
                    INSERT INTO V2ClosedCandles VALUES($asset,$at,$payload)
                    ON CONFLICT(AssetId) DO UPDATE SET ReceivedAt=excluded.ReceivedAt,Payload=excluded.Payload
                    WHERE excluded.ReceivedAt>=V2ClosedCandles.ReceivedAt;
                    """;
                command.Parameters.AddWithValue("$asset",candles[0].AssetId.ToString());
                command.Parameters.AddWithValue("$at",read.Evidence.ReceivedTime.ToUnixTimeMilliseconds());
                command.Parameters.AddWithValue("$payload",JsonSerializer.Serialize(read));
                command.ExecuteNonQuery();
            }
            command.Parameters.Clear();
            command.CommandText = """
                INSERT INTO V2ProviderHealth VALUES($provider,$symbol,$endpoint,$at,$success,$failure,$count)
                ON CONFLICT(Provider,Symbol,Endpoint) DO UPDATE SET
                    CheckedAt=excluded.CheckedAt, Failure=excluded.Failure,
                    LastSuccess=COALESCE(excluded.LastSuccess,V2ProviderHealth.LastSuccess),
                    ConsecutiveFailures=CASE WHEN excluded.Failure=0 THEN 0 ELSE V2ProviderHealth.ConsecutiveFailures+1 END
                WHERE excluded.CheckedAt>=V2ProviderHealth.CheckedAt;
                """;
            command.Parameters.AddWithValue("$provider", read.Evidence.Provider);
            command.Parameters.AddWithValue("$symbol", read.Evidence.Symbol);
            command.Parameters.AddWithValue("$endpoint", read.Evidence.Endpoint);
            command.Parameters.AddWithValue("$at", read.Evidence.ReceivedTime.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$success", read.Evidence.Failure == CollectionFailure.None
                ? read.Evidence.ReceivedTime.ToUnixTimeMilliseconds() : DBNull.Value);
            command.Parameters.AddWithValue("$failure", (int)read.Evidence.Failure);
            command.Parameters.AddWithValue("$count", read.Evidence.Failure == CollectionFailure.None ? 0 : 1);
            command.ExecuteNonQuery();
            command.CommandText="""
                INSERT INTO SourceHealthDetail VALUES($provider,$symbol,$endpoint,$at,$reason,$retry,$priority)
                ON CONFLICT(Provider,Symbol,Endpoint) DO UPDATE SET At=excluded.At,Reason=excluded.Reason,
                    RetryAt=excluded.RetryAt,Priority=excluded.Priority WHERE excluded.At>=SourceHealthDetail.At;
                """;
            command.Parameters.AddWithValue("$reason",(object?)read.Evidence.FailureDetail??DBNull.Value);
            command.Parameters.AddWithValue("$retry",(object?)read.Evidence.RetryAt?.ToUnixTimeMilliseconds()??DBNull.Value);
            command.Parameters.AddWithValue("$priority",(int)read.Evidence.Priority);command.ExecuteNonQuery();
        }
    }

    public void SaveMarketContext(MarketContext context, IReadOnlyList<ProviderRead> reads)
    {
        lock (writer)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            SaveReads(connection, transaction, reads);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO V2MarketContext VALUES($at,$payload)";
            command.Parameters.AddWithValue("$at", context.AsOf.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(context));
            command.ExecuteNonQuery();
            foreach (var read in reads.Where(item => item.Universe is not null))
            {
                command.CommandText = "INSERT INTO V2Universe VALUES(1,$payload) ON CONFLICT(Id) DO UPDATE SET Payload=excluded.Payload";
                command.Parameters["$payload"].Value = JsonSerializer.Serialize(read);
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }

    public ProviderRead? Universe()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Payload FROM V2Universe WHERE Id=1";
        return command.ExecuteScalar() is string payload ? JsonSerializer.Deserialize<ProviderRead>(payload) : null;
    }

    public MarketContext? MarketContext()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Payload FROM V2MarketContext ORDER BY AsOf DESC LIMIT 1";
        return command.ExecuteScalar() is string payload ? JsonSerializer.Deserialize<MarketContext>(payload) : null;
    }

    public FeatureSnapshot? Latest(Guid assetId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Payload FROM V2Feature WHERE AssetId=$id ORDER BY AsOf DESC LIMIT 1";
        command.Parameters.AddWithValue("$id", assetId.ToString());
        return command.ExecuteScalar() is string payload ? JsonSerializer.Deserialize<FeatureSnapshot>(payload) : null;
    }

    public object CollectionStatus()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*), COALESCE(MAX(ReceivedAt),0) FROM V2RawEvidence";
        using var reader = command.ExecuteReader();
        reader.Read();
        return new { rawResponses = reader.GetInt64(0), lastReceivedAt =
            reader.GetInt64(1) is > 0 and var time ? DateTimeOffset.FromUnixTimeMilliseconds(time) : (DateTimeOffset?)null };
    }

    public IReadOnlyList<SourceHealth> SourceHealth(DateTimeOffset now)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT h.Provider,h.Symbol,h.Endpoint,h.CheckedAt,h.LastSuccess,h.Failure,h.ConsecutiveFailures,d.Reason,d.RetryAt,d.Priority
            FROM V2ProviderHealth h LEFT JOIN SourceHealthDetail d USING(Provider,Symbol,Endpoint)
            ORDER BY h.Provider,h.Symbol,h.Endpoint
            """;
        using var reader = command.ExecuteReader();
        var result = new List<SourceHealth>();
        while (reader.Read())
        {
            var provider = reader.GetString(0);
            var endpoint = reader.GetString(2);
            var checkedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3));
            var failure = (CollectionFailure)reader.GetInt32(5);
            var freshness = provider == "Binance"
                ? endpoint.StartsWith("wss:/",StringComparison.Ordinal) ? TimeSpan.FromSeconds(15) :
                    endpoint.Contains("/depth?",StringComparison.Ordinal) ? TimeSpan.FromSeconds(5) :
                    endpoint.Contains("/fundingRate?",StringComparison.Ordinal) ? TimeSpan.FromMinutes(10) :
                    endpoint == "/fapi/v1/exchangeInfo" ? TimeSpan.FromHours(8) :
                    endpoint == "/fapi/v1/fundingInfo" ? TimeSpan.FromMinutes(5) :
                    endpoint == "/fapi/v1/ticker/24hr" ? TimeSpan.FromMinutes(3) :
                    endpoint.Contains("/klines?", StringComparison.Ordinal) || endpoint.Contains("/openInterestHist?", StringComparison.Ordinal)
                        ? TimeSpan.FromMinutes(10) : TimeSpan.FromMinutes(2)
                : TimeSpan.FromHours(8);
            var state = failure != CollectionFailure.None ? "Degraded"
                : checkedAt > now || now - checkedAt > freshness ? "Stale" : "Healthy";
            result.Add(new(provider, reader.GetString(1), endpoint, state, checkedAt,
                reader.IsDBNull(4) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
                failure,reader.GetInt32(6),reader.IsDBNull(7)?failure==CollectionFailure.Quota?"LegacyQuotaUnclassified":null:reader.GetString(7),
                reader.IsDBNull(8)?null:DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(8)),
                reader.IsDBNull(9)?null:(RequestPriority)reader.GetInt32(9)));
        }
        return result;
    }
}

public sealed record SourceHealth(string Provider, string Symbol, string Endpoint, string State,
    DateTimeOffset CheckedAt, DateTimeOffset? LastSuccess, CollectionFailure Failure,int ConsecutiveFailures,
    string? FailureDetail=null,DateTimeOffset? RetryAt=null,RequestPriority? Priority=null);
