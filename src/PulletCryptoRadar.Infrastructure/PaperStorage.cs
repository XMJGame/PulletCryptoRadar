using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Infrastructure;

public sealed partial class SqliteDatabase
{
    private static void InitializePaper(SqliteConnection c)
    {
        c.Execute("""
            CREATE TABLE IF NOT EXISTS PaperAccount(Id INTEGER PRIMARY KEY CHECK(Id=1),Revision INTEGER NOT NULL,Payload BLOB NOT NULL);
            CREATE TABLE IF NOT EXISTS PaperTrade(Id TEXT PRIMARY KEY,ClosedAt INTEGER NOT NULL,Payload BLOB NOT NULL);
            CREATE TABLE IF NOT EXISTS PaperAudit(Revision INTEGER PRIMARY KEY,Time INTEGER NOT NULL,Payload BLOB NOT NULL);
            INSERT OR IGNORE INTO SchemaVersion VALUES(11,unixepoch());
            """);
    }
    public void SavePaper(PaperBatch batch)
    {
        var s=batch.State;if(s.Revision!=batch.ExpectedRevision+1||s.Cash<0||s.Positions.Select(p=>p.AssetId).Distinct().Count()!=s.Positions.Count||s.Positions.Count>s.Rules.MaximumPositions)
            throw new InvalidDataException("模拟账本余额、持仓或版本无效");
        using var c=Open();using var tx=c.BeginTransaction();var revision=c.QuerySingleOrDefault<long?>("SELECT Revision FROM PaperAccount WHERE Id=1",transaction:tx)??0;
        if(revision!=batch.ExpectedRevision)throw new MappingConflictException("模拟账户版本冲突，禁止重复记账");
        foreach(var t in batch.Trades)c.Execute("INSERT INTO PaperTrade VALUES(@Id,@Time,@Payload)",new{Id=t.Id.ToString(),Time=t.ClosedAt.ToUnixTimeMilliseconds(),Payload=Compress(t)},tx);
        c.Execute("INSERT OR REPLACE INTO PaperAccount VALUES(1,@Revision,@Payload)",new{s.Revision,Payload=Compress(s)},tx);
        var changed=s.Decisions.Where(d=>d.Time==s.UpdatedAt).ToArray();
        if(changed.Length>0||batch.ExpectedRevision==0)c.Execute("INSERT INTO PaperAudit VALUES(@Revision,@Time,@Payload)",new{s.Revision,Time=s.UpdatedAt.ToUnixTimeMilliseconds(),Payload=Compress(new{Rules=s.Rules,Decisions=changed,Positions=s.Positions,Pending=s.Pending,Cash=s.Cash,Enabled=s.Enabled})},tx);
        tx.Commit();
    }
}
public sealed class PaperRepository(SqliteDatabase db):IPaperRepository
{
    public PaperState? Restore(){using var c=db.Open();var p=c.QuerySingleOrDefault<byte[]>("SELECT Payload FROM PaperAccount WHERE Id=1");return p is null?null:SqliteDatabase.Decompress<PaperState>(p);}
    public IReadOnlyList<PaperTrade> Trades(){using var c=db.Open();return c.Query<byte[]>("SELECT Payload FROM PaperTrade ORDER BY ClosedAt DESC").Select(SqliteDatabase.Decompress<PaperTrade>).ToArray();}
}
public sealed record SavePaperCommand(PaperBatch Batch,TaskCompletionSource Completion):PersistenceCommand;
public sealed class PaperPersistence(PersistenceQueue queue):IPaperPersistence
{
    public async Task SaveAsync(PaperBatch batch,CancellationToken ct){var ack=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);await queue.Channel.Writer.WriteAsync(new SavePaperCommand(batch,ack),ct);await ack.Task.WaitAsync(ct);}
}
