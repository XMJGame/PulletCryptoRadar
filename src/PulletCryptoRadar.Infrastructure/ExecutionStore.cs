using System.Text.Json;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Infrastructure;

public sealed partial class FoundationStore
{
    public void SaveExecution(ExecutionEvidence evidence)
    {
        lock (writer)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            SaveReads(connection,transaction,[evidence.Book,evidence.Funding]);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO V2ExecutionEvidence VALUES($id,$at,$payload)";
            command.Parameters.AddWithValue("$id",evidence.AssetId.ToString());
            command.Parameters.AddWithValue("$at",evidence.AsOf.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$payload",JsonSerializer.Serialize(evidence));
            command.ExecuteNonQuery();
            transaction.Commit();
        }
    }

    public ExecutionEvidence? LatestExecution(Guid assetId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Payload FROM V2ExecutionEvidence WHERE AssetId=$id ORDER BY At DESC LIMIT 1";
        command.Parameters.AddWithValue("$id",assetId.ToString());
        return command.ExecuteScalar() is string payload ? JsonSerializer.Deserialize<ExecutionEvidence>(payload) : null;
    }

    public void SaveSizing(SizingProposal proposal)
    {
        if (proposal.ExecutionAllowed) throw new ArgumentException("Portfolio execution is not connected.");
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO V2Sizing VALUES($id,$side,$at,$payload)";
        command.Parameters.AddWithValue("$id",proposal.AssetId.ToString());
        command.Parameters.AddWithValue("$side",(int)proposal.Side);
        command.Parameters.AddWithValue("$at",proposal.AsOf.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$payload",JsonSerializer.Serialize(proposal));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<SizingProposal> SizingHistory(Guid assetId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Payload FROM V2Sizing WHERE AssetId=$id ORDER BY At DESC LIMIT 100";
        command.Parameters.AddWithValue("$id",assetId.ToString());
        using var reader = command.ExecuteReader();
        var result = new List<SizingProposal>();
        while (reader.Read()) result.Add(JsonSerializer.Deserialize<SizingProposal>(reader.GetString(0))!);
        return result;
    }
}
