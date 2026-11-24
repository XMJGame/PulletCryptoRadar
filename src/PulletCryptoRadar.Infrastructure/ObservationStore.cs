using System.Text.Json;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Infrastructure;

public sealed partial class FoundationStore
{
    public void BeginObservation(Guid runId,DateTimeOffset now)
    {
        if (runId == Guid.Empty) throw new ArgumentException("Run ID is required.");
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO ObservationRun VALUES($id,$now,$now,$now,0,0,0)";
        command.Parameters.AddWithValue("$id",runId.ToString());
        command.Parameters.AddWithValue("$now",now.ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
    }

    public void RecordObservation(Guid runId,ObservationSample sample,bool stopped = false)
    {
        lock (writer)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT * FROM ObservationRun WHERE RunId=$id";
            command.Parameters.AddWithValue("$id",runId.ToString());
            ObservationRun run;
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read()) throw new InvalidOperationException("Observation run is not started.");
                run = ReadRun(reader);
            }
            if (run.Stopped) throw new InvalidOperationException("Observation run has stopped.");
            if (sample.At < run.LastHeartbeat) throw new ArgumentException("Observation clock moved backwards.");
            var elapsed = sample.At-run.LastHeartbeat;
            var interrupted = elapsed > TimeSpan.FromSeconds(90);
            command.CommandText = """
                UPDATE ObservationRun SET LastHeartbeat=$at,ContinuousSince=$since,ObservedSeconds=$seconds,
                    Interruptions=$interruptions,Stopped=$stopped WHERE RunId=$id;
                INSERT OR REPLACE INTO ObservationSample VALUES($id,$at,$problem,$payload);
                DELETE FROM ObservationSample WHERE At<$retention;
                """;
            command.Parameters.AddWithValue("$at",sample.At.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$since",(interrupted ? sample.At : run.ContinuousSince).ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$seconds",run.ObservedSeconds+(interrupted ? 0 : (long)elapsed.TotalSeconds));
            command.Parameters.AddWithValue("$interruptions",run.Interruptions+(interrupted ? 1 : 0));
            command.Parameters.AddWithValue("$stopped",stopped ? 1 : 0);
            command.Parameters.AddWithValue("$problem",!sample.HttpResponsive || !sample.CashReconciled || !sample.MarketContextFresh || sample.StalePositions > 0 ||
                sample.FailedSources > 0 || sample.ActiveIssues > 0 ? 1 : 0);
            command.Parameters.AddWithValue("$payload",JsonSerializer.Serialize(sample));
            command.Parameters.AddWithValue("$retention",sample.At.AddDays(-30).ToUnixTimeMilliseconds());
            command.ExecuteNonQuery();
            transaction.Commit();
        }
    }

    private static ObservationRun ReadRun(Microsoft.Data.Sqlite.SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
        reader.GetInt64(4),reader.GetInt32(5),reader.GetInt64(6) != 0);

    public ObservationReport Observation(DateTimeOffset now)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred:true);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$now",now.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$since",now.AddDays(-1).ToUnixTimeMilliseconds());
        command.CommandText = "SELECT * FROM ObservationRun ORDER BY StartedAt DESC,rowid DESC LIMIT 10";
        var runs = new List<ObservationRun>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) runs.Add(ReadRun(reader));
        var run = runs.FirstOrDefault();
        var fresh = run is { Stopped:false } && run.LastHeartbeat <= now && now-run.LastHeartbeat <= TimeSpan.FromSeconds(90);
        var continuous = fresh ? (long)(run!.LastHeartbeat-run.ContinuousSince).TotalSeconds : 0;
        command.CommandText = "SELECT Payload FROM ObservationSample WHERE RunId=$id AND At<=$now ORDER BY At DESC LIMIT 1";
        command.Parameters.AddWithValue("$id",run?.RunId.ToString() ?? "");
        var sample = command.ExecuteScalar() is string payload ? JsonSerializer.Deserialize<ObservationSample>(payload) : null;
        command.CommandText = "SELECT COUNT(*),COALESCE(SUM(Problem),0) FROM ObservationSample WHERE At BETWEEN $since AND $now";
        int samples,problems;
        using (var reader = command.ExecuteReader())
        {
            reader.Read(); samples = reader.GetInt32(0); problems = reader.GetInt32(1);
        }
        IReadOnlyList<ObservationCount> Counts(string sql)
        {
            command.CommandText = sql;
            var result = new List<ObservationCount>();
            using var reader = command.ExecuteReader();
            while (reader.Read()) result.Add(new(reader.GetString(0),reader.GetInt64(1)));
            return result;
        }
        var statuses = Counts("""
            SELECT json_extract(Payload,'$.Status'),COUNT(*) FROM V2EntryEvaluation
            WHERE At BETWEEN $since AND $now GROUP BY 1 ORDER BY 2 DESC,1
            """);
        var reasons = Counts("""
            SELECT reason.value,COUNT(*) FROM V2EntryEvaluation,json_each(Payload,'$.Reasons') reason
            WHERE At BETWEEN $since AND $now GROUP BY 1 ORDER BY 2 DESC,1
            """);
        var admissions = Counts("SELECT Status,COUNT(*) FROM V2PaperAdmission WHERE At BETWEEN $since AND $now GROUP BY 1 ORDER BY 2 DESC,1");
        var actions = Counts("""
            SELECT json_extract(Payload,'$.Type'),COUNT(*) FROM V2PaperLedger
            WHERE At BETWEEN $since AND $now GROUP BY 1 ORDER BY 2 DESC,1
            """);
        transaction.Commit();
        var universe = Universe();
        var symbols = universe?.Universe?.Select(item => item.Symbol).ToHashSet() ?? [];
        var ids = Assets().Where(item => symbols.Contains(item.BinanceSymbol)).Select(item => item.AssetId).ToHashSet();
        var entries = LatestEntries().Where(item => ids.Contains(item.AssetId) && item.AsOf <= now).ToArray();
        var recent = entries.Where(item => now-item.AsOf <= TimeSpan.FromMinutes(3)).ToArray();
        var valid = recent.Where(item => item.Setup is { } setup && setup.ExpiresAt > now).ToArray();
        var coverage = new ObservationCoverage(symbols.Count,entries.Select(item => item.AssetId).Distinct().Count(),
            recent.Select(item => item.AssetId).Distinct().Count(),valid.Count(item => item.Status == "Waiting"),
            valid.Count(item => item.Status == "PatternConfirmed" && item.Setup!.ConfirmedAt <= now &&
                now-item.Setup.ConfirmedAt <= TimeSpan.FromMinutes(1)));
        return new(now,run,fresh,continuous,continuous >= 86400,sample,samples,problems,coverage,statuses,reasons,admissions,actions,runs);
    }
}
