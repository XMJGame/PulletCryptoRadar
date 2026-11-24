using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Infrastructure;

public sealed partial class FoundationStore
{
    public void SavePaperAdmission(Guid assetId,Guid setupId,Guid evidenceId,DateTimeOffset at,string status,string policy)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO V2PaperAdmission VALUES($setup,$evidence,$asset,$at,$status,$policy)";
        command.Parameters.AddWithValue("$setup",setupId.ToString()); command.Parameters.AddWithValue("$evidence",evidenceId.ToString());
        command.Parameters.AddWithValue("$asset",assetId.ToString()); command.Parameters.AddWithValue("$at",at.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$status",status); command.Parameters.AddWithValue("$policy",policy); command.ExecuteNonQuery();
    }
    public IReadOnlyList<object> PaperAdmissions()
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT AssetId,SetupId,EvidenceId,At,Status,Policy FROM V2PaperAdmission ORDER BY At DESC LIMIT 100";
        using var reader = command.ExecuteReader(); var result = new List<object>();
        while (reader.Read()) result.Add(new { assetId = reader.GetString(0),setupId = reader.GetString(1),bookEvidenceId = reader.GetString(2),
            at = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),status = reader.GetString(4),policy = reader.GetString(5) });
        return result;
    }
    public object BudgetUsage(DateTimeOffset now)
    {
        now = now.ToUniversalTime();
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(SUM(CASE WHEN At>=$day THEN Bytes ELSE 0 END),0),COALESCE(SUM(Bytes),0) FROM V2Request WHERE At>=$month";
        command.Parameters.AddWithValue("$day",new DateTimeOffset(now.UtcDateTime.Date,TimeSpan.Zero).ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$month",new DateTimeOffset(now.Year,now.Month,1,0,0,0,TimeSpan.Zero).ToUnixTimeMilliseconds());
        using var reader = command.ExecuteReader(); reader.Read();
        return new { asOf = now,dayBytesIncludingReservations = reader.GetInt64(0),monthBytesIncludingReservations = reader.GetInt64(1),
            dayLimitBytes = 900_000_000L,monthLimitBytes = 30_000_000_000L,scope = "ResponsePayloadExcludesTlsVpnOverhead" };
    }
    public void SetPaperIssue(string key,string detail,DateTimeOffset at,bool active = true)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO V2PaperIssue VALUES($key,$detail,$at,$active)
            ON CONFLICT(IssueKey) DO UPDATE SET Detail=excluded.Detail,At=excluded.At,Active=excluded.Active;
            """;
        command.Parameters.AddWithValue("$key",key); command.Parameters.AddWithValue("$detail",detail);
        command.Parameters.AddWithValue("$at",at.ToUnixTimeMilliseconds()); command.Parameters.AddWithValue("$active",active ? 1 : 0);
        command.ExecuteNonQuery();
    }
    public IReadOnlyList<object> PaperIssues()
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT IssueKey,Detail,At FROM V2PaperIssue WHERE Active=1 ORDER BY At DESC";
        using var reader = command.ExecuteReader(); var result = new List<object>();
        while (reader.Read()) result.Add(new { key = reader.GetString(0),detail = reader.GetString(1),
            at = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)) });
        return result;
    }
    public DateTimeOffset? FundingCursor(Guid assetId)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT AfterAt FROM V2PaperFundingCursor WHERE AssetId=$id";
        command.Parameters.AddWithValue("$id",assetId.ToString());
        return command.ExecuteScalar() is long at ? DateTimeOffset.FromUnixTimeMilliseconds(at) : null;
    }
    public void SaveFundingCursor(Guid assetId,DateTimeOffset at)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO V2PaperFundingCursor VALUES($id,$at) ON CONFLICT(AssetId) DO UPDATE SET AfterAt=MAX(AfterAt,excluded.AfterAt);
            """;
        command.Parameters.AddWithValue("$id",assetId.ToString()); command.Parameters.AddWithValue("$at",at.ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
    }
}
