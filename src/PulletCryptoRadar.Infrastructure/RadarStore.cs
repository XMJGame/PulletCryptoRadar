using System.Text.Json;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Infrastructure;

public sealed partial class FoundationStore
{
    public void RefreshPriority(IReadOnlyList<PriorityCandidate> candidates, DateTimeOffset now)
    {
        if (candidates.Count > 4 || candidates.Select(item => item.Symbol).Distinct().Count() != candidates.Count ||
            candidates.Any(item => item.SourceTime > now || now-item.SourceTime > TimeSpan.FromSeconds(90)))
            throw new ArgumentException("Invalid priority candidates.");
        lock (writer)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT COALESCE(MAX(SelectedAt),0) FROM V2Priority";
            if ((long)command.ExecuteScalar()! > now.ToUnixTimeMilliseconds()) return;
            foreach (var candidate in candidates)
            {
                command.Parameters.Clear();
                command.CommandText = """
                    INSERT INTO V2Priority(Symbol,Payload,SelectedAt) VALUES($symbol,$payload,$at)
                    ON CONFLICT(Symbol) DO UPDATE SET Payload=excluded.Payload,SelectedAt=excluded.SelectedAt;
                    """;
                command.Parameters.AddWithValue("$symbol",candidate.Symbol);
                command.Parameters.AddWithValue("$payload",JsonSerializer.Serialize(candidate));
                command.Parameters.AddWithValue("$at",now.ToUnixTimeMilliseconds());
                command.ExecuteNonQuery();
            }
            command.Parameters.Clear();
            // Remove dropped candidates even when refreshing twice at the same timestamp.
            command.CommandText = "SELECT Symbol FROM V2Priority";
            var dropped = new List<string>();
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                    if (!candidates.Any(item => item.Symbol == reader.GetString(0))) dropped.Add(reader.GetString(0));
            foreach (var symbol in dropped)
            {
                command.Parameters.Clear();
                command.CommandText = "DELETE FROM V2Priority WHERE Symbol=$symbol";
                command.Parameters.AddWithValue("$symbol",symbol);
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }

    public PriorityCandidate? ReservePriority(DateTimeOffset now)
    {
        lock (writer)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT Payload FROM V2Priority WHERE SelectedAt<=$now AND SelectedAt>=$fresh AND ReservedAt<=$due ORDER BY ReservedAt,Symbol LIMIT 1";
            command.Parameters.AddWithValue("$now",now.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$fresh",now.AddMinutes(-3).ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$due",now.AddMinutes(-2).ToUnixTimeMilliseconds());
            if (command.ExecuteScalar() is not string payload) return null;
            var selected = JsonSerializer.Deserialize<PriorityCandidate>(payload)!;
            command.Parameters.Clear();
            command.CommandText = "UPDATE V2Priority SET ReservedAt=$at WHERE Symbol=$symbol";
            command.Parameters.AddWithValue("$at",now.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$symbol",selected.Symbol);
            command.ExecuteNonQuery();
            transaction.Commit();
            return selected;
        }
    }

    public IReadOnlyList<PriorityCandidate> Priority(DateTimeOffset now)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Payload FROM V2Priority WHERE SelectedAt<=$now AND SelectedAt>=$fresh ORDER BY Symbol";
        command.Parameters.AddWithValue("$now",now.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$fresh",now.AddMinutes(-3).ToUnixTimeMilliseconds());
        using var reader = command.ExecuteReader();
        var result = new List<PriorityCandidate>();
        while (reader.Read()) result.Add(JsonSerializer.Deserialize<PriorityCandidate>(reader.GetString(0))!);
        return result;
    }

    public RadarEpisode SaveRadar(RadarAssessment assessment)
    {
        lock (writer)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT Payload FROM V2RadarEpisode WHERE AssetId=$id ORDER BY UpdatedAt DESC LIMIT 1";
            command.Parameters.AddWithValue("$id",assessment.AssetId.ToString());
            var previous = command.ExecuteScalar() is string payload ? JsonSerializer.Deserialize<RadarEpisode>(payload) : null;
            if (previous is not null && previous.UpdatedAt.ToUnixTimeMilliseconds() >= assessment.AsOf.ToUnixTimeMilliseconds()) return previous;
            var expired = previous is null || assessment.AsOf-previous.UpdatedAt > TimeSpan.FromHours(6);
            var episode = new RadarEpisode(expired ? Guid.NewGuid() : previous!.EpisodeId,assessment.AssetId,
                expired ? assessment.AsOf : previous!.StartedAt,assessment.AsOf,assessment);
            command.Parameters.Clear();
            command.CommandText = """
                INSERT INTO V2RadarEpisode VALUES($episode,$asset,$at,$payload)
                ON CONFLICT(EpisodeId) DO UPDATE SET UpdatedAt=excluded.UpdatedAt,Payload=excluded.Payload;
                """;
            command.Parameters.AddWithValue("$episode",episode.EpisodeId.ToString());
            command.Parameters.AddWithValue("$asset",assessment.AssetId.ToString());
            command.Parameters.AddWithValue("$at",assessment.AsOf.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$payload",JsonSerializer.Serialize(episode));
            command.ExecuteNonQuery();
            var transition = expired || previous!.Assessment.Stage != assessment.Stage ||
                previous.Assessment.LowCapResearchQualified != assessment.LowCapResearchQualified ||
                previous.Assessment.Version != assessment.Version;
            if (transition)
            {
                command.Parameters.Clear();
                command.CommandText = "INSERT INTO V2RadarEvent VALUES($episode,$at,$payload)";
                command.Parameters.AddWithValue("$episode",episode.EpisodeId.ToString());
                command.Parameters.AddWithValue("$at",assessment.AsOf.ToUnixTimeMilliseconds());
                command.Parameters.AddWithValue("$payload",JsonSerializer.Serialize(assessment));
                command.ExecuteNonQuery();
            }
            transaction.Commit();
            return episode;
        }
    }

    public IReadOnlyList<RadarEpisode> Radar()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.Payload FROM V2RadarEpisode a WHERE NOT EXISTS
                (SELECT 1 FROM V2RadarEpisode b WHERE b.AssetId=a.AssetId AND b.UpdatedAt>a.UpdatedAt)
            ORDER BY a.UpdatedAt DESC;
            """;
        using var reader = command.ExecuteReader();
        var result = new List<RadarEpisode>();
        while (reader.Read()) result.Add(JsonSerializer.Deserialize<RadarEpisode>(reader.GetString(0))!);
        return result;
    }

    public IReadOnlyList<RadarAssessment> RadarEvents(Guid assetId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.Payload FROM V2RadarEvent e JOIN V2RadarEpisode p ON p.EpisodeId=e.EpisodeId
            WHERE p.AssetId=$id ORDER BY e.At DESC LIMIT 100;
            """;
        command.Parameters.AddWithValue("$id",assetId.ToString());
        using var reader = command.ExecuteReader();
        var result = new List<RadarAssessment>();
        while (reader.Read()) result.Add(JsonSerializer.Deserialize<RadarAssessment>(reader.GetString(0))!);
        return result;
    }
}
