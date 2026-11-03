using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Infrastructure;

public sealed partial class SqliteDatabase
{
    private static void InitializeOnChain(SqliteConnection business,SqliteConnection market)
    {
        business.Execute("""
            CREATE TABLE IF NOT EXISTS ChainBinding(AssetId INTEGER PRIMARY KEY REFERENCES Asset(Id),Chain TEXT NOT NULL,ContractAddress TEXT NOT NULL,Revision INTEGER NOT NULL,Payload TEXT NOT NULL,UNIQUE(Chain,ContractAddress));
            CREATE TABLE IF NOT EXISTS ChainBindingAudit(AssetId INTEGER NOT NULL,Revision INTEGER NOT NULL,Payload TEXT NOT NULL,PRIMARY KEY(AssetId,Revision));
            CREATE TABLE IF NOT EXISTS AddressLabel(Chain TEXT NOT NULL,Address TEXT NOT NULL,Revision INTEGER NOT NULL,Payload TEXT NOT NULL,PRIMARY KEY(Chain,Address));
            CREATE TABLE IF NOT EXISTS AddressLabelAudit(Chain TEXT NOT NULL,Address TEXT NOT NULL,Revision INTEGER NOT NULL,Payload TEXT NOT NULL,PRIMARY KEY(Chain,Address,Revision));
            CREATE TABLE IF NOT EXISTS OnChainAttempt(Chain TEXT NOT NULL,ContractAddress TEXT NOT NULL,NextAttempt INTEGER NOT NULL,Payload TEXT NOT NULL,PRIMARY KEY(Chain,ContractAddress));
            CREATE TABLE IF NOT EXISTS OnChainBudget(Time INTEGER NOT NULL,Requests INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_OnChainBudget_Time ON OnChainBudget(Time);
            INSERT OR IGNORE INTO SchemaVersion VALUES(9,unixepoch());
            """);
        market.Execute("""
            CREATE TABLE IF NOT EXISTS OnChainSnapshot(Chain TEXT NOT NULL,ContractAddress TEXT NOT NULL,Provider TEXT NOT NULL,SourceTime INTEGER NOT NULL,ReceivedTime INTEGER NOT NULL,Payload BLOB NOT NULL,PRIMARY KEY(Chain,ContractAddress,Provider,SourceTime));
            CREATE INDEX IF NOT EXISTS IX_OnChainSnapshot_Time ON OnChainSnapshot(SourceTime);
            """);
    }
    public void SaveChainBinding(ChainBinding b,long expected)
    {
        using var c=Open();using var tx=c.BeginTransaction();var revision=c.QuerySingleOrDefault<long?>("SELECT Revision FROM ChainBinding WHERE AssetId=@AssetId",b,tx)??0;
        if(revision!=expected||b.Revision!=expected+1)throw new MappingConflictException("链上资产映射版本冲突");
        if(c.QuerySingle<int>("SELECT COUNT(*) FROM ChainBinding WHERE Chain=@Chain AND ContractAddress=@ContractAddress AND AssetId<>@AssetId",b,tx)>0)throw new MappingConflictException("该链合约已绑定另一个本地资产");
        var data=new{b.AssetId,b.Chain,b.ContractAddress,b.Revision,Payload=JsonSerializer.Serialize(b)};
        c.Execute("INSERT INTO ChainBinding VALUES(@AssetId,@Chain,@ContractAddress,@Revision,@Payload) ON CONFLICT(AssetId) DO UPDATE SET Chain=excluded.Chain,ContractAddress=excluded.ContractAddress,Revision=excluded.Revision,Payload=excluded.Payload",data,tx);
        c.Execute("INSERT INTO ChainBindingAudit VALUES(@AssetId,@Revision,@Payload)",data,tx);tx.Commit();
    }
    public void SaveAddressLabel(AddressLabel l,long expected)
    {
        using var c=Open();using var tx=c.BeginTransaction();var revision=c.QuerySingleOrDefault<long?>("SELECT Revision FROM AddressLabel WHERE Chain=@Chain AND Address=@Address",l,tx)??0;
        if(revision!=expected||l.Revision!=expected+1)throw new MappingConflictException("地址标签已更新，请刷新");
        var data=new{l.Chain,l.Address,l.Revision,Payload=JsonSerializer.Serialize(l)};
        c.Execute("INSERT INTO AddressLabel VALUES(@Chain,@Address,@Revision,@Payload) ON CONFLICT(Chain,Address) DO UPDATE SET Revision=excluded.Revision,Payload=excluded.Payload",data,tx);
        c.Execute("INSERT INTO AddressLabelAudit VALUES(@Chain,@Address,@Revision,@Payload)",data,tx);tx.Commit();
    }
    public bool ReserveOnChain(ChainReservation r)
    {
        using var c=Open();using var tx=c.BeginTransaction();var now=r.Attempt.LastAttempt;
        var due=c.QuerySingleOrDefault<long?>("SELECT NextAttempt FROM OnChainAttempt WHERE Chain=@Chain AND ContractAddress=@ContractAddress",r.Attempt,tx);
        if(due>now.ToUnixTimeMilliseconds()){tx.Commit();return false;}
        foreach(var (seconds,limit) in new[]{(3600,r.HourLimit),(86400,r.DayLimit),(604800,r.WeekLimit)})
            if(c.QuerySingle<int>("SELECT COALESCE(SUM(Requests),0) FROM OnChainBudget WHERE Time>@Start",new{Start=now.AddSeconds(-seconds).ToUnixTimeMilliseconds()},tx)+r.Requests>limit){tx.Commit();return false;}
        c.Execute("INSERT INTO OnChainBudget VALUES(@Time,@Requests)",new{Time=now.ToUnixTimeMilliseconds(),r.Requests},tx);
        c.Execute("INSERT OR REPLACE INTO OnChainAttempt VALUES(@Chain,@ContractAddress,@Next,@Payload)",new{r.Attempt.Chain,r.Attempt.ContractAddress,Next=r.Attempt.NextAttempt.ToUnixTimeMilliseconds(),Payload=JsonSerializer.Serialize(r.Attempt)},tx);
        c.Execute("DELETE FROM OnChainBudget WHERE Time<=@Cutoff",new{Cutoff=now.AddDays(-7).ToUnixTimeMilliseconds()},tx);tx.Commit();return true;
    }
    public void SaveOnChain(OnChainWrite write)
    {
        if(write.Sample is {} s)
        {
            if(s.Chain!=write.Attempt.Chain||s.ContractAddress!=write.Attempt.ContractAddress||s.SourceTime>s.ReceivedTime||s.Holders.Count>s.RequestedCount)throw new InvalidDataException("链上样本身份、时间或覆盖无效");
            if(s.Security is {} evidence&&(evidence.Chain!=s.Chain||!ChainIdentity.Same(s.Chain,evidence.ContractAddress,s.ContractAddress)
                ||evidence.SourceTime>evidence.ReceivedTime||evidence.Holders.Count>10))throw new InvalidDataException("补充证据身份、时间或覆盖无效");
            using var m=Open(true);using var tx=m.BeginTransaction();
            m.Execute("INSERT INTO OnChainSnapshot VALUES(@Chain,@ContractAddress,@Provider,@Source,@Received,@Payload) ON CONFLICT(Chain,ContractAddress,Provider,SourceTime) DO UPDATE SET ReceivedTime=excluded.ReceivedTime,Payload=excluded.Payload WHERE excluded.ReceivedTime>=OnChainSnapshot.ReceivedTime",
                new{s.Chain,s.ContractAddress,s.Provider,Source=s.SourceTime.ToUnixTimeMilliseconds(),Received=s.ReceivedTime.ToUnixTimeMilliseconds(),Payload=Compress(s)},tx);
            m.Execute("DELETE FROM OnChainSnapshot WHERE SourceTime<@Cutoff",new{Cutoff=write.Attempt.LastAttempt.AddDays(-90).ToUnixTimeMilliseconds()},tx);tx.Commit();
        }
        using var c=Open();c.Execute("INSERT OR REPLACE INTO OnChainAttempt VALUES(@Chain,@ContractAddress,@Next,@Payload)",new{write.Attempt.Chain,write.Attempt.ContractAddress,Next=write.Attempt.NextAttempt.ToUnixTimeMilliseconds(),Payload=JsonSerializer.Serialize(write.Attempt)});
    }
}
public sealed class OnChainRepository(SqliteDatabase db):IOnChainRepository
{
    public OnChainState Restore()
    {
        using var c=db.Open();var bindings=c.Query<string>("SELECT Payload FROM ChainBinding").Select(p=>JsonSerializer.Deserialize<ChainBinding>(p)!).ToArray();
        var labels=c.Query<string>("SELECT Payload FROM AddressLabel").Select(p=>JsonSerializer.Deserialize<AddressLabel>(p)!).ToArray();
        var attempts=c.Query<string>("SELECT Payload FROM OnChainAttempt").Select(p=>JsonSerializer.Deserialize<ChainAttempt>(p)!).ToArray();
        return new(bindings,labels,bindings.SelectMany(b=>History(b.Chain,b.ContractAddress,256)).ToArray(),attempts);
    }
    public IReadOnlyList<OnChainSample> History(string chain,string contractAddress,int limit)
    {using var c=db.Open(true);return c.Query<byte[]>("SELECT Payload FROM OnChainSnapshot WHERE Chain=@chain AND ContractAddress=@contractAddress ORDER BY SourceTime DESC,ReceivedTime DESC LIMIT @limit",new{chain,contractAddress,limit=Math.Clamp(limit,1,256)}).Select(SqliteDatabase.Decompress<OnChainSample>).ToArray();}
}
public sealed class OnChainPersistence(PersistenceQueue queue):IOnChainPersistence
{
    private async Task Write(Func<TaskCompletionSource,PersistenceCommand> command,CancellationToken ct){var ack=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);await queue.Channel.Writer.WriteAsync(command(ack),ct);await ack.Task.WaitAsync(ct);}
    public Task SaveBindingAsync(ChainBinding b,long expected,CancellationToken ct)=>Write(a=>new SaveChainBindingCommand(b,expected,a),ct);
    public Task SaveLabelAsync(AddressLabel l,long expected,CancellationToken ct)=>Write(a=>new SaveAddressLabelCommand(l,expected,a),ct);
    public Task SaveSampleAsync(OnChainWrite w,CancellationToken ct)=>Write(a=>new SaveOnChainCommand(w,a),ct);
    public async Task<bool> ReserveAsync(ChainReservation r,CancellationToken ct){var ack=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);await queue.Channel.Writer.WriteAsync(new ReserveOnChainCommand(r,ack),ct);return await ack.Task.WaitAsync(ct);}
}
