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
            CREATE TABLE IF NOT EXISTS PaperHoldingSnapshot(PositionId TEXT NOT NULL,Time INTEGER NOT NULL,Revision INTEGER NOT NULL,Payload BLOB NOT NULL,PRIMARY KEY(PositionId,Time));
            CREATE INDEX IF NOT EXISTS IX_PaperHoldingSnapshot_Time ON PaperHoldingSnapshot(Time);
            CREATE TABLE IF NOT EXISTS PaperFundingEvent(EventKey TEXT PRIMARY KEY,PositionId TEXT NOT NULL,BookedAt INTEGER NOT NULL,Payload BLOB NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_PaperFundingEvent_Position ON PaperFundingEvent(PositionId);
            CREATE TABLE IF NOT EXISTS PaperEntryReview(Id TEXT PRIMARY KEY,Time INTEGER NOT NULL,Revision INTEGER NOT NULL,DecisionId TEXT NOT NULL,Summary BLOB NOT NULL,Payload BLOB NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_PaperEntryReview_Time ON PaperEntryReview(Time);
            CREATE TABLE IF NOT EXISTS PaperEquitySample(Revision INTEGER PRIMARY KEY,Time INTEGER NOT NULL,Summary BLOB NOT NULL,Payload BLOB NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_PaperEquitySample_Time ON PaperEquitySample(Time);
            INSERT OR IGNORE INTO SchemaVersion VALUES(16,unixepoch());
            INSERT OR IGNORE INTO SchemaVersion VALUES(15,unixepoch());
            INSERT OR IGNORE INTO SchemaVersion VALUES(14,unixepoch());
            INSERT OR IGNORE INTO SchemaVersion VALUES(11,unixepoch());
            INSERT OR IGNORE INTO SchemaVersion VALUES(12,unixepoch());
            """);
    }
    public void SavePaper(PaperBatch batch)
    {
        var s=batch.State;if(s.Revision!=batch.ExpectedRevision+1||s.Cash<0&&s.QuantControl is null||s.Positions.Select(p=>p.AssetId).Distinct().Count()!=s.Positions.Count||s.Positions.Count>s.Rules.MaximumPositions)
            throw new InvalidDataException("模拟账本余额、持仓或版本无效");
        if(s.Rules.ExperimentalSimulation&&(!PulletCryptoRadar.Application.StructuralPaperEngine.ValidRules(s.Rules)
            ||s.Positions.Any(p=>p.Quantity<=0||p.InitialQuantity<p.Quantity||p.InitialQuantity<=0||p.RemainingMargin<0&&p.Quant is null||p.InitialMargin>100||p.Direction is not ("Long" or "Short"))
            ||batch.Trades.Any(t=>t.PositionId is null||t.Quantity<=0||Math.Abs(t.GrossPnl-t.Fees-t.FundingCost+t.SettlementAdjustment-t.NetPnl)>.00000001m)))
            throw new InvalidDataException("双向实验规则、持仓或分批结算不守恒");
        if(s.QuantControl is {} control&&(control.Version!=PulletCryptoRadar.Application.QuantPaperEngine.Version||control.RulesHash!=PulletCryptoRadar.Application.QuantRules.Hash(control.Parameters,control.Costs)||s.Pending.Any(p=>p.QuantIntent is null))
            ||batch.FundingEvents.Any(e=>e.BookedAt!=s.UpdatedAt||e.Evidence.PositionId==Guid.Empty||Math.Abs(e.OpenAllocated+e.ClosedAllocated-e.Evidence.SignedCost)>.00000001m)
            ||batch.Trades.Any(t=>t.Quant is not null&&(t.Version!=PulletCryptoRadar.Application.QuantPaperEngine.Version||t.ExitQuote?.Status!="Estimated"||t.SettlementAdjustment!=0)))throw new InvalidDataException("新执行版本或资金费事件无效");
        foreach(var review in batch.EntryReviews)
        {
            if(review.At!=s.UpdatedAt||review.Revision!=s.Revision||review.Version!=PulletCryptoRadar.Application.QuantEntryEngine.Version||review.Id!=PulletCryptoRadar.Application.QuantEntryEngine.ReviewId(review.Intent,review.At,review.Revision)
                ||PulletCryptoRadar.Application.QuantRules.HashValue(review.Control)!=PulletCryptoRadar.Application.QuantRules.HashValue(s.QuantControl)||review.Outcome is not ("Created" or "Waiting" or "Cancelled" or "Filled"))throw new InvalidDataException("入场复核与账户版本不一致");
            if(review.Outcome=="Filled"&&(!s.Positions.Any(p=>p.Intent.Key==review.Intent.Key&&p.OpenedAt==review.At&&p.Quant?.EntryReviewId==review.Id&&p.Quant.EntryImplementationVersion==review.Version)||review.Values.Any(v=>v.Passed==false)))throw new InvalidDataException("入场复核成交与仓位或上限不一致");
        }
        using var c=Open();using var tx=c.BeginTransaction();var revision=c.QuerySingleOrDefault<long?>("SELECT Revision FROM PaperAccount WHERE Id=1",transaction:tx)??0;
        if(revision!=batch.ExpectedRevision)throw new MappingConflictException("模拟账户版本冲突，禁止重复记账");
        var previous=c.QuerySingleOrDefault<byte[]>("SELECT Payload FROM PaperAccount WHERE Id=1",transaction:tx);
        if(previous is not null&&Decompress<PaperState>(previous).QuantControl is {} frozen&&PulletCryptoRadar.Application.QuantRules.HashValue(frozen)!=PulletCryptoRadar.Application.QuantRules.HashValue(s.QuantControl))throw new InvalidDataException("执行生效时间及参数不可覆盖");
        foreach(var e in batch.FundingEvents)c.Execute("INSERT INTO PaperFundingEvent VALUES(@Key,@PositionId,@At,@Payload)",new{e.Key,PositionId=e.Evidence.PositionId.ToString(),At=e.BookedAt.ToUnixTimeMilliseconds(),Payload=Compress(e)},tx);
        foreach(var review in batch.EntryReviews)c.Execute("INSERT INTO PaperEntryReview VALUES(@Id,@Time,@Revision,@DecisionId,@Summary,@Payload)",new{review.Id,Time=review.At.ToUnixTimeMilliseconds(),review.Revision,DecisionId=review.Intent.QuantIntent!.Decision.Id,Summary=Compress(PulletCryptoRadar.Application.QuantEntryEngine.Summary(review)),Payload=Compress(review)},tx);
        foreach(var t in batch.Trades)c.Execute("INSERT INTO PaperTrade VALUES(@Id,@Time,@Payload)",new{Id=t.Id.ToString(),Time=t.ClosedAt.ToUnixTimeMilliseconds(),Payload=Compress(t)},tx);
        foreach(var observation in batch.HoldingSnapshots)
        {
            if(observation.AccountRevision!=s.Revision||observation.CapturedAt!=s.UpdatedAt)throw new InvalidDataException("持仓观察必须与同轮账户版本原子提交");
            c.Execute("INSERT INTO PaperHoldingSnapshot VALUES(@id,@time,@revision,@payload)",new{id=observation.PositionId.ToString(),time=observation.CapturedAt.ToUnixTimeMilliseconds(),revision=s.Revision,payload=Compress(observation)},tx);
        }
        foreach(var sample in batch.EquitySamples)
        {
            if(sample.Version!=PulletCryptoRadar.Application.PaperEquityRecorder.Version||sample.Revision!=s.Revision||sample.CapturedAt!=s.UpdatedAt
                ||sample.Cash!=s.Cash||sample.InitialCash!=s.Rules.InitialCash||sample.Positions.Count!=s.Positions.Count
                ||sample.Positions.Any(p=>!s.Positions.Any(held=>held.Id==p.PositionId&&held.Quantity==p.Quantity&&held.LastMark==p.Mark&&held.RemainingMargin==p.RemainingMargin))
                ||s.LastEquitySampleAt!=sample.CapturedAt||sample.Equity is not null&&(!sample.MarksFresh||!sample.FundingObserved||!sample.Reconciled
                    ||sample.Equity!=sample.Cash+sample.RemainingMargin+sample.Gross+sample.Adjustment))throw new InvalidDataException("净值采样必须与账户同轮原子提交并保持估值守恒");
            c.Execute("INSERT INTO PaperEquitySample VALUES(@Revision,@Time,@Summary,@Payload)",new{sample.Revision,Time=sample.CapturedAt.ToUnixTimeMilliseconds(),Summary=Compress(sample.Point()),Payload=Compress(sample)},tx);
        }
        c.Execute("INSERT OR REPLACE INTO PaperAccount VALUES(1,@Revision,@Payload)",new{s.Revision,Payload=Compress(s)},tx);
        var changed=s.Decisions.Where(d=>d.Time==s.UpdatedAt).ToArray();
        if(changed.Length>0||batch.ExpectedRevision==0||batch.FundingEvents.Count>0||batch.EntryReviews.Count>0)c.Execute("INSERT INTO PaperAudit VALUES(@Revision,@Time,@Payload)",new{s.Revision,Time=s.UpdatedAt.ToUnixTimeMilliseconds(),Payload=Compress(new{Rules=s.Rules,QuantControl=s.QuantControl,QuantFunding=s.QuantFunding,FundingEvents=batch.FundingEvents,EntryReviewIds=batch.EntryReviews.Select(r=>r.Id).ToArray(),Decisions=changed,Positions=s.Positions,Pending=s.Pending,Cash=s.Cash,Enabled=s.Enabled,SignalHour=s.SignalHour,UniverseVersion=s.UniverseVersion,AnalysisSymbols=s.AnalysisSymbols,Signals=changed.Any(d=>d.Action=="双向分析快照")?s.SignalObservations.Where(d=>d.DecisionAt==s.AnalysisHour).ToArray():[],ReviewThresholdReached=s.ReviewThresholdReached})},tx);
        tx.Commit();
    }
}
public sealed class PaperRepository(SqliteDatabase db):IPaperRepository
{
    public IReadOnlyList<PaperEquityPoint> EquityPoints(DateTimeOffset since,DateTimeOffset through)
    {using var c=db.Open();return c.Query<byte[]>("SELECT Summary FROM PaperEquitySample WHERE Time>=@from AND Time<=@through ORDER BY Time,Revision",new{from=since.ToUnixTimeMilliseconds(),through=through.ToUnixTimeMilliseconds()}).Select(SqliteDatabase.Decompress<PaperEquityPoint>).ToArray();}
    public IReadOnlyList<PaperEquitySample> EquitySamples(DateTimeOffset since,DateTimeOffset through)
    {using var c=db.Open();return c.Query<byte[]>("SELECT Payload FROM PaperEquitySample WHERE Time>=@from AND Time<=@through ORDER BY Time,Revision",new{from=since.ToUnixTimeMilliseconds(),through=through.ToUnixTimeMilliseconds()}).Select(SqliteDatabase.Decompress<PaperEquitySample>).ToArray();}
    public DateTimeOffset? FirstEquitySampleAt()
    {using var c=db.Open();var at=c.ExecuteScalar<long?>("SELECT MIN(Time) FROM PaperEquitySample");return at is null?null:DateTimeOffset.FromUnixTimeMilliseconds(at.Value);}
    public PaperState? Restore(){using var c=db.Open();var p=c.QuerySingleOrDefault<byte[]>("SELECT Payload FROM PaperAccount WHERE Id=1");return p is null?null:SqliteDatabase.Decompress<PaperState>(p);}
    public IReadOnlyList<PaperTrade> Trades(){using var c=db.Open();return c.Query<byte[]>("SELECT Payload FROM PaperTrade ORDER BY ClosedAt DESC").Select(SqliteDatabase.Decompress<PaperTrade>).ToArray();}
    public IReadOnlyList<QuantFundingEvent> QuantFundingEvents(){using var c=db.Open();return c.Query<byte[]>("SELECT Payload FROM PaperFundingEvent ORDER BY BookedAt,EventKey").Select(SqliteDatabase.Decompress<QuantFundingEvent>).ToArray();}
    public QuantEntryWindow EntryReviews(DateTimeOffset since,DateTimeOffset through,int limit=200)
    {
        limit=Math.Clamp(limit,1,500);using var c=db.Open();var rows=c.Query<byte[]>("SELECT Summary FROM PaperEntryReview WHERE Time>=@start AND Time<@end ORDER BY Time DESC,Id LIMIT @count",new{start=since.ToUnixTimeMilliseconds(),end=through.ToUnixTimeMilliseconds(),count=limit+1}).ToArray();
        return new(rows.Take(limit).Select(SqliteDatabase.Decompress<QuantEntrySummary>).ToArray(),rows.Length>limit);
    }
    public QuantEntryReview? EntryReview(string id)
    {
        if(id.Length!=64||!id.All(Uri.IsHexDigit))throw new ArgumentException("入场复核ID格式无效");using var c=db.Open();var payload=c.QuerySingleOrDefault<byte[]>("SELECT Payload FROM PaperEntryReview WHERE Id=@id",new{id});return payload is null?null:SqliteDatabase.Decompress<QuantEntryReview>(payload);
    }
    public DateTimeOffset? FirstEntryReviewAt(){using var c=db.Open();var at=c.ExecuteScalar<long?>("SELECT MIN(Time) FROM PaperEntryReview");return at is null?null:DateTimeOffset.FromUnixTimeMilliseconds(at.Value);}
    public IReadOnlyList<string> EntryReviewIds(DateTimeOffset since,DateTimeOffset through)
    {using var c=db.Open();return c.Query<string>("SELECT Id FROM PaperEntryReview WHERE Time>=@start AND Time<=@end ORDER BY Time,Id",new{start=since.ToUnixTimeMilliseconds(),end=through.ToUnixTimeMilliseconds()}).ToArray();}
    public IReadOnlyList<PaperHoldingSnapshot> HoldingHistory(Guid? positionId,int limit,long? before=null)
    {
        using var c=db.Open();return c.Query<byte[]>("SELECT Payload FROM PaperHoldingSnapshot WHERE (@id IS NULL OR PositionId=@id) AND (@before IS NULL OR Time<@before) ORDER BY Time DESC,PositionId LIMIT @limit",
            new{id=positionId?.ToString(),before,limit=Math.Clamp(limit,1,1000)}).Select(SqliteDatabase.Decompress<PaperHoldingSnapshot>).ToArray();
    }
    public long HoldingSnapshotCount(){using var c=db.Open();return c.ExecuteScalar<long>("SELECT COUNT(*) FROM PaperHoldingSnapshot");}
    public PaperAuditWindow AnalysisAudit(DateTimeOffset since,DateTimeOffset through)
    {
        using var c=db.Open();var raw=c.Query("SELECT Revision,Time,Payload FROM PaperAudit WHERE Time>=@start AND Time<=@end ORDER BY Revision DESC LIMIT 1001",
            new{start=since.ToUnixTimeMilliseconds(),end=through.ToUnixTimeMilliseconds()}).ToArray();
        var rows=raw.Take(1000).Reverse().Select(row=>
        {
            var content=SqliteDatabase.Decompress<PaperAuditContent>((byte[])row.Payload);
            return new PaperAnalysisAudit((long)row.Revision,DateTimeOffset.FromUnixTimeMilliseconds((long)row.Time),content.Signals,content.Decisions);
        }).ToArray();return new(rows,raw.Length>1000);
    }
    private sealed record PaperAuditContent(IReadOnlyList<ForwardDecision> Signals,IReadOnlyList<PaperDecision> Decisions);
}
public sealed record SavePaperCommand(PaperBatch Batch,TaskCompletionSource Completion):PersistenceCommand;
public sealed class PaperPersistence(PersistenceQueue queue):IPaperPersistence
{
    public async Task SaveAsync(PaperBatch batch,CancellationToken ct){var ack=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);await queue.Channel.Writer.WriteAsync(new SavePaperCommand(batch,ack),ct);await ack.Task;}
}
