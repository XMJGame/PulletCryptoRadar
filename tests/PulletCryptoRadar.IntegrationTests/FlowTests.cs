using System.Numerics;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;
using PulletCryptoRadar.Providers.OnChain;

namespace PulletCryptoRadar.IntegrationTests;

public sealed class FlowTests:IDisposable
{
    private static readonly DateTimeOffset Now=new(2026,10,29,10,0,0,TimeSpan.Zero);
    private const string Contract="0x1111111111111111111111111111111111111111",From="0x2222222222222222222222222222222222222222",To="0x3333333333333333333333333333333333333333";
    private static readonly string Hash="0x"+new string('a',64),Tx="0x"+new string('b',64);
    private readonly string _root=Path.Combine(Path.GetTempPath(),"Pullet-Flows-"+Guid.NewGuid());
    private SqliteDatabase Db(){var db=new SqliteDatabase(Options.Create(new StorageOptions{Directory=_root}));db.Initialize();return db;}
    private static ChainBinding Binding=>new(1,"bsc",Contract,"coin",true,true,1,Now,"Fixture",[]);
    private static JsonElement Log(bool removed=false,string? contract=null,string? amount=null)=>JsonSerializer.SerializeToElement(new{address=contract??Contract,topics=new[]{TransferLogParser.Topic,"0x"+new string('0',24)+From[2..],"0x"+new string('0',24)+To[2..]},data=amount??"0x"+new string('f',64),transactionHash=Tx,blockHash=Hash,blockNumber="0x64",logIndex="0x2",removed});
    private static IReadOnlyList<TokenTransfer> Parse(params JsonElement[] logs)=>TransferLogParser.Parse(Binding,JsonSerializer.SerializeToElement(logs),100,100,new Dictionary<long,(string,DateTimeOffset)>{{100,(Hash,Now)}},Now);
    private static TokenTransfer Transfer()=>new("bsc",Contract,100,Hash,Tx,2,Now,From,To,"1000","BSC RPC",Now);
    private static FlowBatch Batch()=>new(new("bsc",Contract,100,101,Hash,Now,165,Now){StartTime=Now},null,[Transfer()],[]);
    private static FlowAttempt Attempt=>new("bsc",Contract,Now.AddMinutes(2),null);
    [Fact] public void ExactUint256AndDuplicateLogIdentityArePreserved()
    {
        var rows=Parse(Log(),Log());Assert.Single(rows);Assert.Equal(((BigInteger.One<<256)-1).ToString(),rows[0].RawAmount);Assert.Equal(From,rows[0].From);Assert.Equal(To,rows[0].To);
        Assert.Equal(2,rows[0].LogIndex);
    }
    [Theory] [InlineData("removed")] [InlineData("contract")] [InlineData("data")] [InlineData("conflict")]
    public void InvalidOrConflictingLogsRejectWholeRange(string issue)
    {
        var log=issue switch{"removed"=>Log(true),"contract"=>Log(contract:From),"data"=>Log(amount:"0x01"),_=>Log(amount:"0x"+new string('0',64))};
        Assert.Throws<InvalidDataException>(()=>issue=="conflict"?Parse(Log(),log):Parse(log));
    }
    [Fact] public void AtomicCursorAndRowsRestoreWithoutDuplicatesAndRejectOldCommit()
    {
        var db=Db();var batch=Batch();db.SaveFlow(batch,Attempt);var repo=new FlowRepository(db);
        Assert.Equal(101,repo.Cursor("bsc",Contract)!.NextBlock);Assert.Single(repo.Transfers("bsc",Contract,Now.AddHours(-1),20));
        Assert.Throws<MappingConflictException>(()=>db.SaveFlow(batch,Attempt));
        var invalid=batch with{ExpectedNextBlock=101,Cursor=batch.Cursor with{NextBlock=102},Transfers=[Transfer() with{BlockNumber=101,RawAmount="-1"}]};
        Assert.Throws<InvalidDataException>(()=>db.SaveFlow(invalid,Attempt));Assert.Equal(101,repo.Cursor("bsc",Contract)!.NextBlock);
        var empty=batch with{ExpectedNextBlock=101,Cursor=batch.Cursor with{NextBlock=102},Transfers=[]};db.SaveFlow(empty,Attempt);
        Assert.Equal(102,repo.Cursor("bsc",Contract)!.NextBlock);Assert.Single(repo.Transfers("bsc",Contract,Now.AddHours(-1),20));
    }
    [Fact] public void BudgetReservationsAndHaltedStateSurviveRestart()
    {
        var db=Db();Assert.True(db.ReserveFlow(Attempt,Now,160,160,160));Assert.False(db.ReserveFlow(Attempt,Now,160,160,160));
        Assert.False(db.ReserveFlow(Attempt with{NextAttempt=Now.AddMinutes(10)},Now.AddMinutes(3),160,160,160));
        db.FailFlow(Attempt with{Halted=true,Error="Fixture anchor mismatch"});
        Assert.False(db.ReserveFlow(Attempt with{NextAttempt=Now.AddDays(3)},Now.AddDays(2),160,1000,1000));Assert.True(new FlowRepository(db).Attempt("bsc",Contract)!.Halted);
        using var c=db.Open(true);Assert.Equal(160,c.QuerySingle<int>("SELECT SUM(Requests) FROM FlowBudget"));
    }
    [Fact] public void ManualTransportRetryRespectsMinimumDelayAndNeverResetsBudgetOrProviderCooldown()
    {
        var db=Db();Assert.True(db.ReserveFlow(Attempt,Now,160,1000,1000));
        db.FailFlow(Attempt with{NextAttempt=Now.AddMinutes(15),Error="RPC 网络采集未完成；已保存断点不前移"});
        Assert.False(db.RetryFlowTransport("bsc",Contract,Now.AddMinutes(1)));Assert.True(db.RetryFlowTransport("bsc",Contract,Now.AddMinutes(2)));
        Assert.False(db.RetryFlowTransport("bsc",Contract,Now.AddMinutes(3)));using var c=db.Open(true);Assert.Equal(160,c.QuerySingle<int>("SELECT SUM(Requests) FROM FlowBudget"));
        db.FailFlow(Attempt with{NextAttempt=Now.AddMinutes(15),Error="转账 RPC HTTP 429，停止本轮请求"});Assert.False(db.RetryFlowTransport("bsc",Contract,Now.AddHours(1)));
    }
    private static OnChainView View(params AddressLabel[] labels)=>new(Binding,new(){TrustedIdentity=true,Labels=labels},new(Guid.NewGuid(),"bsc",Contract,"Fixture",Now,Now,"Fixture",10,"1000000",0,[],100,true),null);
    [Fact] public void ClassificationRequiresVerifiedSameChainEvidenceAndSelfOrMintIsNotExchangeInflow()
    {
        var labels=new[]{new AddressLabel("bsc",From,WalletLabelType.Project,true,"https://example.com/project",1,Now),new AddressLabel("bsc",To,WalletLabelType.Exchange,true,"https://example.com/exchange",2,Now)};
        var result=FlowEvidenceEngine.Calculate(View(labels),Batch().Cursor,Attempt,[Transfer()],Now);
        Assert.True(result.Summary.Fresh);Assert.Equal(1,result.Summary.ProjectOutflows);Assert.Equal(.1m,result.Summary.ProjectToExchangeSupplyPercent);Assert.Equal("ProjectToExchange",result.Rows[0].Kind);
        var unknown=FlowEvidenceEngine.Calculate(View(labels[0] with{Verified=false},labels[1] with{Chain="ethereum"}),Batch().Cursor,Attempt,[Transfer()],Now);
        Assert.Equal("Transfer",unknown.Rows[0].Kind);Assert.Equal(0,unknown.Summary.ProjectOutflows);
        var self=FlowEvidenceEngine.Calculate(View(labels),Batch().Cursor,Attempt,[Transfer() with{From=To}],Now);Assert.Equal(0,self.Summary.ExchangeInflows);Assert.Equal("SelfTransfer",self.Rows[0].Kind);
        Assert.False(result.Summary.WindowComplete);
    }
    [Fact] public void FailedOrStaleCursorAndStaleSupplyNeverProvideFreshRisk()
    {
        var result=FlowEvidenceEngine.Calculate(View(),Batch().Cursor with{LastBlockTime=Now.AddHours(-1)},Attempt,[Transfer()],Now);Assert.False(result.Summary.Fresh);
        result=FlowEvidenceEngine.Calculate(View(),Batch().Cursor,Attempt with{Error="Failure"},[Transfer()],Now);Assert.False(result.Summary.Fresh);
        var view=View();result=FlowEvidenceEngine.Calculate(view with{Sample=view.Sample! with{SourceTime=Now.AddHours(-1)}},Batch().Cursor,Attempt,[Transfer()],Now);Assert.Null(result.Rows[0].SupplyPercent);
    }
    [Fact] public void FullTimeCoverageRequiresStartBaselineAndCaughtUpConfirmedTail()
    {
        var cursor=Batch().Cursor with{StartTime=Now.AddHours(-2),ObservedHead=164};
        Assert.True(FlowEvidenceEngine.Calculate(View(),cursor,Attempt,[],Now).Summary.WindowComplete);
        Assert.False(FlowEvidenceEngine.Calculate(View(),cursor with{ObservedHead=1000},Attempt,[],Now).Summary.WindowComplete);
        Assert.False(FlowEvidenceEngine.Calculate(View(),cursor with{LastBlockTime=Now.AddMinutes(-3)},Attempt,[],Now).Summary.WindowComplete);
    }
    [Fact] public async Task RpcChainIdentityIsCheckedBeforeLogs()
    {
        var handler=new RpcHandler();using var http=new HttpClient(handler){BaseAddress=new Uri("https://example.com")};
        await Assert.ThrowsAsync<InvalidDataException>(()=>new FlowProvider(http,new(),TimeProvider.System).FetchAsync(Binding,null,[],default));Assert.Equal(1,handler.Count);
    }
    private sealed class RpcHandler:HttpMessageHandler
    {public int Count;protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){Count++;return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":\"0x1\"}")});}}
    [Fact] public async Task EmptyConfirmedRangesAdvanceAndKeepStartTime()
    {
        var handler=new BscHandler();using var http=new HttpClient(handler){BaseAddress=new Uri("https://example.com")};
        var batch=await new FlowProvider(http,new(){InitialBlocks=2,PageBlocks=2},new Clock()).FetchAsync(Binding,null,[],default);
        Assert.Empty(batch.Transfers);Assert.Equal(101,batch.Cursor.NextBlock);Assert.Equal(99,batch.Cursor.StartBlock);Assert.Equal(Now,batch.Cursor.StartTime);Assert.Null(batch.ExpectedNextBlock);
    }
    [Fact] public async Task ChangedSavedAnchorHaltsBeforeRequestingAnyLogs()
    {
        var handler=new BscHandler();using var http=new HttpClient(handler){BaseAddress=new Uri("https://example.com")};
        var cursor=Batch().Cursor with{LastBlockHash="0x"+new string('c',64)};
        await Assert.ThrowsAsync<FlowReorgException>(()=>new FlowProvider(http,new(),new Clock()).FetchAsync(Binding,cursor,[],default));Assert.DoesNotContain("eth_getLogs",handler.Methods);
    }
    [Fact] public async Task SingleWriterAcknowledgesDurableRangeAndInspectionHistory()
    {
        var db=Db();var queue=new PersistenceQueue();using var worker=new PersistenceWorker(db,queue,new ProviderHealthStore(new Clock()),Microsoft.Extensions.Logging.Abstractions.NullLogger<PersistenceWorker>.Instance);await worker.StartAsync(default);await queue.Ready.Task;
        try
        {
            var writer=new FlowPersistence(queue);Assert.True(await writer.ReserveAsync(Attempt,Now,160,1000,1000,default));
            var batch=Batch() with{Inspections=[new("bsc",From,100,Hash,true,"code",Now,"Fixture","Contract only")]};await writer.SaveAsync(batch,Attempt,default);
            Assert.Single(new FlowRepository(db).Transfers("bsc",Contract,Now.AddHours(-1),20));Assert.Single(new FlowRepository(db).Inspections("bsc"));
            using var c=db.Open(true);Assert.Equal(1,c.QuerySingle<int>("SELECT COUNT(*) FROM AddressInspectionHistory"));
        }
        finally{await worker.StopAsync(default);}
    }
    private sealed class Clock:TimeProvider{public override DateTimeOffset GetUtcNow()=>Now;}
    private sealed class BscHandler:HttpMessageHandler
    {
        public List<string> Methods=[];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            using var doc=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));var root=doc.RootElement;var method=root.GetProperty("method").GetString()!;Methods.Add(method);
            object result=method switch{"eth_chainId"=>"0x38","eth_blockNumber"=>"0xa4","eth_getLogs"=>Array.Empty<object>(),"eth_getBlockByNumber"=>new{number=root.GetProperty("params")[0].GetString(),hash=Hash,timestamp="0x"+Now.ToUnixTimeSeconds().ToString("x")},_=>throw new InvalidOperationException(method)};
            return new(System.Net.HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new{jsonrpc="2.0",id=root.GetProperty("id").GetInt32(),result}))};
        }
    }
    public void Dispose(){SqliteConnection.ClearAllPools();if(Directory.Exists(_root))Directory.Delete(_root,true);}
}
