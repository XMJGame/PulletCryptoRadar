using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Infrastructure;

public sealed partial class FoundationStore
{
    public object CollectionDiagnostics(DateTimeOffset now)
    {
        using var connection=Open();using var command=connection.CreateCommand();
        command.Parameters.AddWithValue("$now",now.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$since",now.AddDays(-1).ToUnixTimeMilliseconds());
        command.CommandText="SELECT Provider,Reason,Priority,COUNT(*) FROM RequestDenial WHERE At BETWEEN $since AND $now GROUP BY Provider,Reason,Priority ORDER BY COUNT(*) DESC";
        var counts=new List<object>();
        using(var reader=command.ExecuteReader())while(reader.Read())counts.Add(new
        {provider=reader.GetString(0),reason=reader.GetString(1),priority=((RequestPriority)reader.GetInt32(2)).ToString(),count=reader.GetInt64(3)});
        command.CommandText="SELECT Provider,At,Reason,RetryAt,Priority FROM RequestDenial WHERE At<=$now ORDER BY At DESC LIMIT 30";
        var recent=new List<object>();
        using(var reader=command.ExecuteReader())while(reader.Read())recent.Add(new
        {provider=reader.GetString(0),at=DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),reason=reader.GetString(2),
            retryAt=reader.IsDBNull(3)?(DateTimeOffset?)null:DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),priority=((RequestPriority)reader.GetInt32(4)).ToString()});
        command.CommandText="SELECT c.Provider,c.Until,d.Reason FROM V2Cooldown c LEFT JOIN CooldownDetail d ON d.Provider=c.Provider AND d.Until=c.Until WHERE c.Until>$now ORDER BY c.Provider";
        var cooldowns=new List<object>();
        using(var reader=command.ExecuteReader())while(reader.Read())cooldowns.Add(new
        {provider=reader.GetString(0),until=DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),reason=reader.IsDBNull(2)?"ProviderCooldown":reader.GetString(2)});
        command.Parameters.AddWithValue("$hour",now.AddHours(-1).ToUnixTimeMilliseconds());
        command.CommandText="SELECT COUNT(*),COALESCE(SUM(Bytes),0),COALESCE(SUM(CASE WHEN Completed=0 THEN Bytes ELSE 0 END),0),MIN(At) FROM V2Request WHERE At BETWEEN $hour AND $now";
        long calls,bytes,reserved;DateTimeOffset? first;
        using(var reader=command.ExecuteReader())
        {reader.Read();calls=reader.GetInt64(0);bytes=reader.GetInt64(1);reserved=reader.GetInt64(2);first=reader.IsDBNull(3)?null:DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3));}
        return new {asOf=now,diagnosisSince="New detailed reasons only; old Quota records remain unclassified",cooldowns,
            minuteCapacity=new {background=CollectionCapacity(now,RequestPriority.Background),core=CollectionCapacity(now,RequestPriority.Core),critical=CollectionCapacity(now,RequestPriority.Critical)},
            policy=new {binanceMinuteLimits=new {background=18,core=24,critical=30},
                dailyPayloadLimits=new {background=700_000_000L,core=850_000_000L,critical=900_000_000L},
                monthlyPayloadLimits=new {background=24_000_000_000L,core=27_000_000_000L,critical=30_000_000_000L}},
            lastHour=new {calls,bytesIncludingReservations=bytes,inFlightReservedBytes=reserved,firstRequestAt=first,
                sampledSeconds=first is null?0:Math.Max(0,(now-first.Value).TotalSeconds)},denials24h=counts,recentDenials=recent};
    }
}
