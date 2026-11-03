using Dapper;
using Microsoft.Data.Sqlite;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Infrastructure;

public sealed partial class SqliteDatabase
{
    private static void InitializeOpportunities(SqliteConnection c)
    {
        c.Execute("""
            CREATE TABLE IF NOT EXISTS OpportunityScan(Id TEXT PRIMARY KEY,AsOf INTEGER NOT NULL,Payload BLOB NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_OpportunityScan_Time ON OpportunityScan(AsOf);
            INSERT OR IGNORE INTO SchemaVersion VALUES(13,unixepoch());
            """);
    }
    public void SaveOpportunity(OpportunitySnapshot snapshot)
    {
        using var c=Open();using var tx=c.BeginTransaction();
        c.Execute("INSERT INTO OpportunityScan VALUES(@id,@time,@payload)",new{id=snapshot.Id.ToString(),time=snapshot.AsOf.ToUnixTimeMilliseconds(),payload=Compress(snapshot)},tx);
        c.Execute("DELETE FROM OpportunityScan WHERE AsOf<@cutoff",new{cutoff=snapshot.AsOf.AddDays(-90).ToUnixTimeMilliseconds()},tx);tx.Commit();
    }
}
public sealed class OpportunityRepository(SqliteDatabase db):IOpportunityRepository
{
    public OpportunitySnapshot? Latest()=>History(1).FirstOrDefault();
    public IReadOnlyList<OpportunitySnapshot> History(int limit)
    {
        using var c=db.Open();return c.Query<byte[]>("SELECT Payload FROM OpportunityScan ORDER BY AsOf DESC LIMIT @limit",new{limit=Math.Clamp(limit,1,12)})
            .Select(SqliteDatabase.Decompress<OpportunitySnapshot>).ToArray();
    }
    public void Save(OpportunitySnapshot snapshot)=>db.SaveOpportunity(snapshot);
    public OpportunitySnapshot? AtOrBefore(DateTimeOffset time)
    {
        using var c=db.Open();var bytes=c.QuerySingleOrDefault<byte[]>("SELECT Payload FROM OpportunityScan WHERE AsOf<=@time AND AsOf>=@min ORDER BY AsOf DESC LIMIT 1",new{time=time.ToUnixTimeMilliseconds(),min=time.AddHours(-1).ToUnixTimeMilliseconds()});
        return bytes is null?null:SqliteDatabase.Decompress<OpportunitySnapshot>(bytes);
    }
}
