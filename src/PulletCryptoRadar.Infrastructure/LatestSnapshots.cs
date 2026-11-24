using System.Text.Json;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Infrastructure;

public sealed partial class FoundationStore
{
    public IReadOnlyList<FeatureSnapshot> LatestSnapshots()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.Payload FROM V2Feature a WHERE NOT EXISTS
                (SELECT 1 FROM V2Feature b WHERE b.AssetId=a.AssetId AND b.AsOf>a.AsOf)
            ORDER BY a.AssetId
            """;
        var result = new List<FeatureSnapshot>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(JsonSerializer.Deserialize<FeatureSnapshot>(reader.GetString(0))!);
        return result;
    }
}
