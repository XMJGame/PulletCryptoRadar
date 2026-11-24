using System.Text.Json;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Infrastructure;

public sealed partial class FoundationStore
{
    public ProviderRead? LatestCandles(Guid assetId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Payload FROM V2ClosedCandles WHERE AssetId=$id";
        command.Parameters.AddWithValue("$id",assetId.ToString());
        return command.ExecuteScalar() is string payload ? JsonSerializer.Deserialize<ProviderRead>(payload) : null;
    }

    public EntryEvaluation? LatestEntry(Guid assetId,EntryStrategy strategy,TradeSide side)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Payload FROM V2EntryEvaluation WHERE AssetId=$id AND Strategy=$strategy AND Side=$side ORDER BY At DESC LIMIT 1";
        command.Parameters.AddWithValue("$id",assetId.ToString());
        command.Parameters.AddWithValue("$strategy",(int)strategy);
        command.Parameters.AddWithValue("$side",(int)side);
        return command.ExecuteScalar() is string payload ? JsonSerializer.Deserialize<EntryEvaluation>(payload) : null;
    }

    public void SaveEntry(EntryEvaluation evaluation)
    {
        if (evaluation.ExecutionAllowed || evaluation.Setup is { } setup && (setup.AssetId != evaluation.AssetId ||
            setup.Strategy != evaluation.Strategy || setup.Side != evaluation.Side))
            throw new ArgumentException("Invalid research entry evaluation.");
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO V2EntryEvaluation VALUES($id,$strategy,$side,$at,$payload)";
        command.Parameters.AddWithValue("$id",evaluation.AssetId.ToString());
        command.Parameters.AddWithValue("$strategy",(int)evaluation.Strategy);
        command.Parameters.AddWithValue("$side",(int)evaluation.Side);
        command.Parameters.AddWithValue("$at",evaluation.AsOf.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$payload",JsonSerializer.Serialize(evaluation));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<EntryEvaluation> Entries(Guid? assetId = null,int maximum = 100)
    {
        if (maximum is < 1 or > 500) throw new ArgumentException("Invalid entry history limit.");
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Payload FROM V2EntryEvaluation WHERE ($id IS NULL OR AssetId=$id) ORDER BY At DESC,Strategy,Side LIMIT $count";
        command.Parameters.AddWithValue("$id",assetId is null ? DBNull.Value : assetId.ToString()!);
        command.Parameters.AddWithValue("$count",maximum);
        using var reader = command.ExecuteReader();
        var result = new List<EntryEvaluation>();
        while (reader.Read()) result.Add(JsonSerializer.Deserialize<EntryEvaluation>(reader.GetString(0))!);
        return result;
    }

    public IReadOnlyList<EntryEvaluation> LatestEntries()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.Payload FROM V2EntryEvaluation a WHERE NOT EXISTS
                (SELECT 1 FROM V2EntryEvaluation b WHERE b.AssetId=a.AssetId AND b.Strategy=a.Strategy AND b.Side=a.Side AND b.At>a.At)
            ORDER BY a.At DESC,a.AssetId,a.Strategy,a.Side;
            """;
        using var reader = command.ExecuteReader();
        var result = new List<EntryEvaluation>();
        while (reader.Read()) result.Add(JsonSerializer.Deserialize<EntryEvaluation>(reader.GetString(0))!);
        return result;
    }
}
