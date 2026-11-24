using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Infrastructure;

public sealed partial class FoundationStore
{
    public AssetIdentity EnsureRegistered(AssetIdentity identity)
    {
        lock (writer)
        {
            var known = Assets().SingleOrDefault(item => item.AssetId == identity.AssetId);
            if (known is not null)
            {
                if (known.BinanceSymbol != identity.BinanceSymbol) throw new ArgumentException("Asset identity mismatch.");
                return known;
            }
            Register(identity);
            return identity;
        }
    }

    public bool TryBeginQuickCheck(Guid assetId, DateTimeOffset now)
    {
        lock (writer)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT MAX(LastStarted) FROM V2QuickJob";
            if (command.ExecuteScalar() is long last && now.ToUnixTimeMilliseconds()-last < 60000) return false;
            command.CommandText = "SELECT COUNT(*) FROM V2Request WHERE Provider='Binance' AND At>=$minute";
            command.Parameters.AddWithValue("$minute",now.AddMinutes(-1).ToUnixTimeMilliseconds());
            // Leave enough room for the six-request refresh. The shared ledger enforces the final cap.
            if ((long)command.ExecuteScalar()! > 24) return false;
            command.Parameters.Clear();
            command.CommandText = """
                INSERT INTO V2QuickJob VALUES($id,$lease,$now,NULL)
                ON CONFLICT(AssetId) DO UPDATE SET LeaseUntil=excluded.LeaseUntil,LastStarted=excluded.LastStarted
                WHERE V2QuickJob.LeaseUntil<=$now AND V2QuickJob.LastStarted<=$due;
                """;
            command.Parameters.AddWithValue("$id",assetId.ToString());
            command.Parameters.AddWithValue("$lease",now.AddMinutes(5).ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$now",now.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$due",now.AddMinutes(-1).ToUnixTimeMilliseconds());
            var reserved = command.ExecuteNonQuery() == 1;
            transaction.Commit();
            return reserved;
        }
    }

    public void FinishQuickCheck(Guid assetId, DateTimeOffset startedAt, DateTimeOffset now)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE V2QuickJob SET CompletedAt=$at,LeaseUntil=$at WHERE AssetId=$id AND LastStarted=$started AND LastStarted<=$at AND LeaseUntil>=$at";
        command.Parameters.AddWithValue("$id",assetId.ToString());
        command.Parameters.AddWithValue("$at",now.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$started",startedAt.ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
    }

    public DateTimeOffset? QuickCheckCompleted(Guid assetId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT CompletedAt FROM V2QuickJob WHERE AssetId=$id";
        command.Parameters.AddWithValue("$id",assetId.ToString());
        return command.ExecuteScalar() is long at ? DateTimeOffset.FromUnixTimeMilliseconds(at) : null;
    }

    public void TouchQuickCheck(Guid assetId, DateTimeOffset now)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO V2QuickHistory VALUES($id,$at) ON CONFLICT(AssetId) DO UPDATE SET ViewedAt=excluded.ViewedAt
            WHERE excluded.ViewedAt>=V2QuickHistory.ViewedAt;
            DELETE FROM V2QuickHistory WHERE AssetId NOT IN (SELECT AssetId FROM V2QuickHistory ORDER BY ViewedAt DESC,AssetId LIMIT 8);
            """;
        command.Parameters.AddWithValue("$id",assetId.ToString());
        command.Parameters.AddWithValue("$at",now.ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public IReadOnlyList<QuickCheckHistory> QuickCheckHistory()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT AssetId,ViewedAt FROM V2QuickHistory ORDER BY ViewedAt DESC,AssetId";
        using var reader = command.ExecuteReader();
        var result = new List<QuickCheckHistory>();
        while (reader.Read()) result.Add(new(Guid.Parse(reader.GetString(0)),DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1))));
        return result;
    }

    public void DeleteQuickCheckHistory(Guid? assetId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = assetId is null ? "DELETE FROM V2QuickHistory" : "DELETE FROM V2QuickHistory WHERE AssetId=$id";
        if (assetId is not null) command.Parameters.AddWithValue("$id",assetId.ToString()!);
        command.ExecuteNonQuery();
    }
}
