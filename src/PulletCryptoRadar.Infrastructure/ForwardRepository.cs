using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Infrastructure;

// Separate local research database; no paper-account writes. Immutable first observations.
public sealed class ForwardRepository : IForwardRepository
{
    private readonly string path;
    public ForwardRepository(IOptions<StorageOptions> storage)
    {
        path=Path.GetFullPath(Path.Combine(storage.Value.Directory,"PulletCryptoRadar.Forward.db"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var c=Open();c.Execute("""
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS Cohort(Id INTEGER PRIMARY KEY CHECK(Id=1),Payload BLOB NOT NULL);
            CREATE TABLE IF NOT EXISTS Snapshot(Bucket INTEGER PRIMARY KEY,Captured INTEGER NOT NULL,Payload BLOB NOT NULL);
            CREATE TABLE IF NOT EXISTS SnapshotSummary(Bucket INTEGER PRIMARY KEY,Payload BLOB NOT NULL);
            CREATE TABLE IF NOT EXISTS Decision(Id TEXT PRIMARY KEY,Captured INTEGER NOT NULL,Payload BLOB NOT NULL);
            CREATE TABLE IF NOT EXISTS Outcome(Id TEXT PRIMARY KEY,Payload BLOB NOT NULL);
            """);
    }
    private SqliteConnection Open()
    {
        var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Pooling=true}.ToString());c.Open();
        c.Execute("PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL;");return c;
    }
    public ForwardCohort? Cohort()
    {
        using var c=Open();var bytes=c.QuerySingleOrDefault<byte[]>("SELECT Payload FROM Cohort WHERE Id=1");
        if(bytes is null)return null;var cohort=SqliteDatabase.Decompress<ForwardCohort>(bytes);
        if(cohort.RulesHash!=ForwardEngine.Hash(cohort.Rules)||cohort.Rules.Version!="Forward-Observation-1")throw new InvalidDataException("前瞻冻结规则校验失败，禁止继续写入");
        return cohort;
    }
    public void Initialize(ForwardCohort cohort)
    {
        if(cohort.RulesHash!=ForwardEngine.Hash(cohort.Rules))throw new InvalidDataException("Rules hash mismatch");
        using var c=Open();c.Execute("INSERT OR IGNORE INTO Cohort VALUES(1,@payload)",new{payload=SqliteDatabase.Compress(cohort)});
    }
    public bool HasSnapshot(DateTimeOffset bucket)
    {using var c=Open();return c.ExecuteScalar<int>("SELECT COUNT(*) FROM Snapshot WHERE Bucket=@bucket",new{bucket=bucket.ToUnixTimeSeconds()})>0;}
    public void Save(ForwardSnapshot snapshot,IReadOnlyList<ForwardDecision> decisions)
    {
        using var c=Open();using var tx=c.BeginTransaction();
        c.Execute("INSERT INTO Snapshot VALUES(@bucket,@captured,@payload)",new{bucket=snapshot.Bucket.ToUnixTimeSeconds(),captured=snapshot.CapturedAt.ToUnixTimeSeconds(),payload=SqliteDatabase.Compress(snapshot)},tx);
        c.Execute("INSERT INTO SnapshotSummary VALUES(@bucket,@payload)",new{bucket=snapshot.Bucket.ToUnixTimeSeconds(),payload=SqliteDatabase.Compress(new ForwardSnapshotSummary(snapshot.CapturedAt,snapshot.Rows.Count,snapshot.Rows.Count(r=>r.Fresh)))},tx);
        foreach(var d in decisions)c.Execute("INSERT OR IGNORE INTO Decision VALUES(@id,@captured,@payload)",new{id=d.Id,captured=d.CapturedAt.ToUnixTimeSeconds(),payload=SqliteDatabase.Compress(d)},tx);
        tx.Commit();
    }
    public IReadOnlyList<ForwardDecision> Decisions()
    {using var c=Open();return c.Query<byte[]>("SELECT Payload FROM Decision ORDER BY Captured DESC,Id").Select(SqliteDatabase.Decompress<ForwardDecision>).ToArray();}
    public IReadOnlyList<ForwardSnapshot> Snapshots(DateTimeOffset from,DateTimeOffset to)
    {using var c=Open();return c.Query<byte[]>("SELECT Payload FROM Snapshot WHERE Captured>=@a AND Captured<=@b ORDER BY Captured",new{a=from.ToUnixTimeSeconds(),b=to.ToUnixTimeSeconds()}).Select(SqliteDatabase.Decompress<ForwardSnapshot>).ToArray();}
    public IReadOnlyList<ForwardSnapshotSummary> SnapshotSummaries()
    {using var c=Open();return c.Query<byte[]>("SELECT Payload FROM SnapshotSummary ORDER BY Bucket").Select(SqliteDatabase.Decompress<ForwardSnapshotSummary>).ToArray();}
    public ForwardSnapshot? LatestSnapshot()
    {using var c=Open();var b=c.QuerySingleOrDefault<byte[]>("SELECT Payload FROM Snapshot ORDER BY Bucket DESC LIMIT 1");return b is null?null:SqliteDatabase.Decompress<ForwardSnapshot>(b);}
    public IReadOnlyList<ForwardOutcome> Outcomes()
    {using var c=Open();return c.Query<byte[]>("SELECT Payload FROM Outcome ORDER BY Id").Select(SqliteDatabase.Decompress<ForwardOutcome>).ToArray();}
    public void SaveOutcomes(IReadOnlyList<ForwardOutcome> outcomes)
    {
        using var c=Open();using var tx=c.BeginTransaction();
        foreach(var o in outcomes)c.Execute("INSERT OR IGNORE INTO Outcome VALUES(@id,@payload)",new{id=o.Id,payload=SqliteDatabase.Compress(o)},tx);
        tx.Commit();
    }
}
