using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Infrastructure;

public sealed partial class SqliteDatabase
{
    private static void InitializeFlows(SqliteConnection business,SqliteConnection market)
    {
        market.Execute("""
            CREATE TABLE IF NOT EXISTS FlowCursor(Chain TEXT NOT NULL,Contract TEXT NOT NULL,NextBlock INTEGER NOT NULL,Payload TEXT NOT NULL,PRIMARY KEY(Chain,Contract));
            CREATE TABLE IF NOT EXISTS TokenTransfer(Chain TEXT NOT NULL,Contract TEXT NOT NULL,TxHash TEXT NOT NULL,LogIndex INTEGER NOT NULL,BlockNumber INTEGER NOT NULL,BlockTime INTEGER NOT NULL,Payload TEXT NOT NULL,PRIMARY KEY(Chain,Contract,TxHash,LogIndex));
            CREATE INDEX IF NOT EXISTS IX_Transfer_Time ON TokenTransfer(Chain,Contract,BlockTime);
            CREATE TABLE IF NOT EXISTS FlowAttempt(Chain TEXT NOT NULL,Contract TEXT NOT NULL,NextAttempt INTEGER NOT NULL,Payload TEXT NOT NULL,PRIMARY KEY(Chain,Contract));
            CREATE TABLE IF NOT EXISTS FlowBudget(Time INTEGER NOT NULL,Requests INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_FlowBudget_Time ON FlowBudget(Time);
            CREATE TABLE IF NOT EXISTS AddressInspection(Chain TEXT NOT NULL,Address TEXT NOT NULL,Payload TEXT NOT NULL,PRIMARY KEY(Chain,Address));
            CREATE TABLE IF NOT EXISTS AddressInspectionHistory(Chain TEXT NOT NULL,Address TEXT NOT NULL,BlockNumber INTEGER NOT NULL,Payload TEXT NOT NULL,PRIMARY KEY(Chain,Address,BlockNumber));
            """);
        business.Execute("INSERT OR IGNORE INTO SchemaVersion VALUES(10,unixepoch());");
    }
    public bool ReserveFlow(FlowAttempt attempt,DateTimeOffset now,int requests,int hourLimit,int dayLimit)
    {
        if(requests is <1 or >160||attempt.NextAttempt<=now)throw new ArgumentException("无效 RPC 预留");
        using var c=Open(true);using var tx=c.BeginTransaction();
        var prior=c.QuerySingleOrDefault<string>("SELECT Payload FROM FlowAttempt WHERE Chain=@Chain AND Contract=@ContractAddress",attempt,tx);
        if(prior is not null&&JsonSerializer.Deserialize<FlowAttempt>(prior) is {} p&&(p.Halted||p.NextAttempt>now))return false;
        foreach(var (seconds,limit) in new[]{(3600,hourLimit),(86400,dayLimit)})
            if(c.QuerySingle<int>("SELECT COALESCE(SUM(Requests),0) FROM FlowBudget WHERE Time>@Since",new{Since=now.AddSeconds(-seconds).ToUnixTimeMilliseconds()},tx)+requests>limit)return false;
        c.Execute("INSERT INTO FlowBudget VALUES(@Time,@requests)",new{Time=now.ToUnixTimeMilliseconds(),requests},tx);SaveFlowAttempt(c,tx,attempt);
        c.Execute("DELETE FROM FlowBudget WHERE Time<@Since",new{Since=now.AddDays(-1).ToUnixTimeMilliseconds()},tx);tx.Commit();return true;
    }
    private static void SaveFlowAttempt(SqliteConnection c,SqliteTransaction tx,FlowAttempt a)=>c.Execute("INSERT OR REPLACE INTO FlowAttempt VALUES(@Chain,@ContractAddress,@Next,@Payload)",new{a.Chain,a.ContractAddress,Next=a.NextAttempt.ToUnixTimeMilliseconds(),Payload=JsonSerializer.Serialize(a)},tx);
    public void FailFlow(FlowAttempt a){using var c=Open(true);using var tx=c.BeginTransaction();SaveFlowAttempt(c,tx,a);tx.Commit();}
    public bool RetryFlowTransport(string chain,string contract,DateTimeOffset now)
    {
        using var c=Open(true);using var tx=c.BeginTransaction();var raw=c.QuerySingleOrDefault<string>("SELECT Payload FROM FlowAttempt WHERE Chain=@chain AND Contract=@contract",new{chain,contract},tx);
        var prior=raw is null?null:JsonSerializer.Deserialize<FlowAttempt>(raw);
        // Only the controlled transport-failure category can be retried after two minutes.
        // Provider/429 cooldowns, validation errors and reorg halts cannot be bypassed.
        if(prior is null||prior.Halted||prior.Error!="RPC 网络采集未完成；已保存断点不前移"||now<prior.NextAttempt.AddMinutes(-13))return false;
        SaveFlowAttempt(c,tx,prior with{NextAttempt=now,Error="等待网络失败重试；请求预算保持"});tx.Commit();return true;
    }
    public void SaveFlow(FlowBatch batch,FlowAttempt attempt)
    {
        var b=batch.Cursor;using var c=Open(true);using var tx=c.BeginTransaction();
        var next=c.QuerySingleOrDefault<long?>("SELECT NextBlock FROM FlowCursor WHERE Chain=@Chain AND Contract=@ContractAddress",b,tx);
        if(next!=batch.ExpectedNextBlock)throw new MappingConflictException("资金流断点版本冲突");
        var from=next??b.StartBlock;
        if(b.Chain!=attempt.Chain||b.ContractAddress!=attempt.ContractAddress||b.NextBlock<=from||b.LastBlockTime>b.UpdatedAt||b.ObservedHead<b.NextBlock-1)throw new InvalidDataException("无效资金流断点");
        foreach(var row in batch.Transfers)
        {
            if(row.Chain!=b.Chain||row.ContractAddress!=b.ContractAddress||row.BlockNumber<from||row.BlockNumber>=b.NextBlock||row.BlockTime>row.ReceivedTime
                ||!BigInteger.TryParse(row.RawAmount,NumberStyles.None,CultureInfo.InvariantCulture,out var amount)||amount<0||amount>=(BigInteger.One<<256))throw new InvalidDataException("转账身份、区块范围或原始数量无效");
            var payload=JsonSerializer.Serialize(row);
            var old=c.QuerySingleOrDefault<string>("SELECT Payload FROM TokenTransfer WHERE Chain=@Chain AND Contract=@ContractAddress AND TxHash=@TransactionHash AND LogIndex=@LogIndex",row,tx);
            if(old is not null&&JsonSerializer.Deserialize<TokenTransfer>(old) is {} oldRow&&oldRow with{ReceivedTime=row.ReceivedTime}!=row)throw new InvalidDataException("已保存日志冲突");
            c.Execute("INSERT OR IGNORE INTO TokenTransfer VALUES(@Chain,@ContractAddress,@TransactionHash,@LogIndex,@BlockNumber,@Time,@Payload)",new{row.Chain,row.ContractAddress,row.TransactionHash,row.LogIndex,row.BlockNumber,Time=row.BlockTime.ToUnixTimeMilliseconds(),Payload=payload},tx);
        }
        foreach(var i in batch.Inspections)
        {
            if(i.Chain!=b.Chain||i.BlockNumber>=b.NextBlock||i.CheckedAt>b.UpdatedAt)throw new InvalidDataException("无效地址核验观测");
            c.Execute("INSERT OR REPLACE INTO AddressInspection VALUES(@Chain,@Address,@Payload)",new{i.Chain,i.Address,Payload=JsonSerializer.Serialize(i)},tx);
            c.Execute("INSERT OR IGNORE INTO AddressInspectionHistory VALUES(@Chain,@Address,@BlockNumber,@Payload)",new{i.Chain,i.Address,i.BlockNumber,Payload=JsonSerializer.Serialize(i)},tx);
        }
        c.Execute("INSERT OR REPLACE INTO FlowCursor VALUES(@Chain,@ContractAddress,@NextBlock,@Payload)",new{b.Chain,b.ContractAddress,b.NextBlock,Payload=JsonSerializer.Serialize(b)},tx);
        SaveFlowAttempt(c,tx,attempt);c.Execute("DELETE FROM TokenTransfer WHERE BlockTime<@Cutoff",new{Cutoff=b.UpdatedAt.AddDays(-90).ToUnixTimeMilliseconds()},tx);tx.Commit();
    }
}
public sealed class FlowRepository(SqliteDatabase db):IFlowRepository
{
    private T? Read<T>(string table,string chain,string contract)where T:class
    {using var c=db.Open(true);var raw=c.QuerySingleOrDefault<string>($"SELECT Payload FROM {table} WHERE Chain=@chain AND Contract=@contract",new{chain,contract});return raw is null?null:JsonSerializer.Deserialize<T>(raw);}
    public FlowCursor? Cursor(string chain,string contract)=>Read<FlowCursor>("FlowCursor",chain,contract);
    public FlowAttempt? Attempt(string chain,string contract)=>Read<FlowAttempt>("FlowAttempt",chain,contract);
    public IReadOnlyList<TokenTransfer> Transfers(string chain,string contract,DateTimeOffset since,int limit)
    {using var c=db.Open(true);return c.Query<string>("SELECT Payload FROM TokenTransfer WHERE Chain=@chain AND Contract=@contract AND BlockTime>=@Since ORDER BY BlockNumber DESC,LogIndex DESC LIMIT @Limit",new{chain,contract,Since=since.ToUnixTimeMilliseconds(),Limit=Math.Clamp(limit,1,10000)}).Select(p=>JsonSerializer.Deserialize<TokenTransfer>(p)!).ToArray();}
    public IReadOnlyList<AddressInspection> Inspections(string chain)
    {using var c=db.Open(true);return c.Query<string>("SELECT Payload FROM AddressInspection WHERE Chain=@chain",new{chain}).Select(p=>JsonSerializer.Deserialize<AddressInspection>(p)!).ToArray();}
}
public sealed record ReserveFlowCommand(FlowAttempt Attempt,DateTimeOffset Now,int Requests,int HourLimit,int DayLimit,TaskCompletionSource<bool> Completion):PersistenceCommand;
public sealed record SaveFlowCommand(FlowBatch Batch,FlowAttempt Attempt,TaskCompletionSource Completion):PersistenceCommand;
public sealed record FailFlowCommand(FlowAttempt Attempt,TaskCompletionSource Completion):PersistenceCommand;
public sealed record RetryFlowCommand(string Chain,string Contract,DateTimeOffset Now,TaskCompletionSource<bool> Completion):PersistenceCommand;
public sealed class FlowPersistence(PersistenceQueue queue):IFlowPersistence
{
    public async Task<bool> RetryTransportAsync(string chain,string contract,DateTimeOffset now,CancellationToken ct)
    {var ack=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);await queue.Channel.Writer.WriteAsync(new RetryFlowCommand(chain,contract,now,ack),ct);return await ack.Task.WaitAsync(ct);}
    public async Task<bool> ReserveAsync(FlowAttempt a,DateTimeOffset now,int requests,int hour,int day,CancellationToken ct)
    {var ack=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);await queue.Channel.Writer.WriteAsync(new ReserveFlowCommand(a,now,requests,hour,day,ack),ct);return await ack.Task.WaitAsync(ct);}
    public async Task SaveAsync(FlowBatch batch,FlowAttempt a,CancellationToken ct)
    {var ack=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);await queue.Channel.Writer.WriteAsync(new SaveFlowCommand(batch,a,ack),ct);await ack.Task.WaitAsync(ct);}
    public async Task FailAsync(FlowAttempt a,CancellationToken ct)
    {var ack=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);await queue.Channel.Writer.WriteAsync(new FailFlowCommand(a,ack),ct);await ack.Task.WaitAsync(ct);}
}
