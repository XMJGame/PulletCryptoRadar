using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Infrastructure;

public sealed partial class FoundationStore
{
    public IReadOnlyList<MarketInstrument> ReserveDepthBatch(IReadOnlyList<MarketInstrument> universe,
        int count, DateTimeOffset now)
    {
        if (count is < 1 or > 4 || universe.Select(item => item.Symbol).Distinct().Count() != universe.Count ||
            universe.Any(item => !System.Text.RegularExpressions.Regex.IsMatch(item.Symbol, @"\A[\p{L}\p{N}]{1,40}USDT\z")))
            throw new ArgumentException("Invalid depth collection batch.");
        lock (writer)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT Symbol,AttemptAt,NextAt FROM V2DepthSchedule";
            var attempted = new Dictionary<string,(long AttemptAt,long NextAt)>(StringComparer.Ordinal);
            using (var reader = command.ExecuteReader())
                while (reader.Read()) attempted.Add(reader.GetString(0),(reader.GetInt64(1),reader.GetInt64(2)));
            var batch = universe.Where(item => !attempted.TryGetValue(item.Symbol,out var state) ||
                    state.NextAt <= now.ToUnixTimeMilliseconds())
                .OrderBy(item => attempted.TryGetValue(item.Symbol,out var state) ? state.AttemptAt : long.MinValue)
                .ThenBy(item => item.Symbol,StringComparer.Ordinal).Take(count).ToArray();
            foreach (var item in batch)
            {
                command.Parameters.Clear();
                command.CommandText = """
                    INSERT INTO V2DepthSchedule VALUES($symbol,$at,$next)
                    ON CONFLICT(Symbol) DO UPDATE SET AttemptAt=excluded.AttemptAt,NextAt=excluded.NextAt;
                    """;
                command.Parameters.AddWithValue("$symbol",item.Symbol);
                command.Parameters.AddWithValue("$at",now.ToUnixTimeMilliseconds());
                command.Parameters.AddWithValue("$next",now.AddMinutes(15).ToUnixTimeMilliseconds());
                command.ExecuteNonQuery();
            }
            transaction.Commit();
            return batch;
        }
    }

    public object DepthStatus(DateTimeOffset now)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Symbol,AttemptAt FROM V2DepthSchedule ORDER BY AttemptAt DESC,Symbol";
        using var reader = command.ExecuteReader();
        var attempts = new List<object>();
        while (reader.Read()) attempts.Add(new { symbol = reader.GetString(0),
            reservedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)) });
        return new { asOf = now, mode = "BackgroundRoundRobin", maximumBatch = 4,
            reservedSymbols = attempts.Count, reservations = attempts,
            note = "Reservations are not successful coverage; inspect source health and feature timestamps." };
    }

    public int BackgroundRequestHeadroom(DateTimeOffset now)=>CollectionCapacity(now,RequestPriority.Background);

    public int CollectionCapacity(DateTimeOffset now,RequestPriority priority)
    {
        now=now.ToUniversalTime();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText="SELECT Until FROM V2Cooldown WHERE Provider='Binance'";
        if(command.ExecuteScalar() is long until && until>now.ToUnixTimeMilliseconds())return 0;
        command.CommandText = "SELECT COUNT(*) FROM V2Request WHERE Provider='Binance' AND At>=$since";
        command.Parameters.AddWithValue("$since",now.AddMinutes(-1).ToUnixTimeMilliseconds());
        var limit=priority switch {RequestPriority.Background=>18,RequestPriority.Core=>24,_=>30};
        var calls=Math.Max(0,limit-(int)(long)command.ExecuteScalar()!);
        command.CommandText="SELECT COALESCE(SUM(Bytes),0) FROM V2Request WHERE At>=$since";
        command.Parameters["$since"].Value=new DateTimeOffset(now.UtcDateTime.Date,TimeSpan.Zero).ToUnixTimeMilliseconds();
        var dayUsage=(long)command.ExecuteScalar()!;
        command.Parameters["$since"].Value=new DateTimeOffset(now.Year,now.Month,1,0,0,0,TimeSpan.Zero).ToUnixTimeMilliseconds();
        var monthUsage=(long)command.ExecuteScalar()!;
        var dayLimit=priority switch {RequestPriority.Background=>700_000_000L,RequestPriority.Core=>850_000_000L,_=>900_000_000L};
        var monthLimit=priority switch {RequestPriority.Background=>24_000_000_000L,RequestPriority.Core=>27_000_000_000L,_=>30_000_000_000L};
        return (int)Math.Max(0,Math.Min(calls,Math.Min((dayLimit-dayUsage)/262144,(monthLimit-monthUsage)/262144)));
    }

    public IReadOnlyList<AssetIdentity> CollectionFocus(DateTimeOffset now,int maximum = 2)
    {
        if (maximum is < 1 or > 6) throw new ArgumentException("Invalid focus size.");
        var held = Portfolio().Positions.Where(item => item.RemainingQuantity > 0).Select(item => item.AssetId);
        var setups = LatestEntries().Where(item => item.AsOf <= now && item.Setup is { } setup &&
                setup.ExpiresAt > now && setup.Phase == SetupPhase.AwaitingConfirmation)
            .OrderBy(item => item.Setup!.ExpiresAt).Select(item => item.AssetId);
        var assets = Assets().ToDictionary(item => item.AssetId);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Name,NextAt FROM V2Job WHERE Name LIKE 'Focus:%'";
        var due = new Dictionary<Guid,long>();
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                if (Guid.TryParse(reader.GetString(0)[6..],out var id)) due[id] = reader.GetInt64(1);
        var heldIds = held.ToHashSet();
        return held.Concat(setups).Distinct().Where(assets.ContainsKey)
            .OrderBy(id => heldIds.Contains(id) ? 0 : 1)
            .ThenBy(id => due.GetValueOrDefault(id,long.MinValue)).ThenBy(id => id)
            .Take(maximum).Select(id => assets[id]).ToArray();
    }
}
