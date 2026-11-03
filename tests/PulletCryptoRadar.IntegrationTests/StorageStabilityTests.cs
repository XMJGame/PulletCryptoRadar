using System.IO.Compression;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Data.Sqlite;
using Dapper;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;

namespace PulletCryptoRadar.IntegrationTests;
public sealed class StorageStabilityTests
{
    private static byte[] Zip(string text)
    {
        using var output=new MemoryStream();using(var zip=new GZipStream(output,CompressionLevel.Fastest,true))zip.Write(Encoding.UTF8.GetBytes(text));return output.ToArray();
    }
    [Fact]public void MalformedJsonReportsRecordTypeAndFingerprintWithoutLeakingPayload()
    {
        var error=Assert.Throws<InvalidDataException>(()=>StorageRecordCodec.Decode<Dictionary<string,string>>(Zip("{\"private-value\": broken")));
        Assert.Contains("Dictionary",error.Message);Assert.Contains("SHA256",error.Message);Assert.DoesNotContain("private-value",error.Message);Assert.IsType<JsonException>(error.InnerException,exactMatch:false);
    }
    [Fact]public void TruncatedGzipAndExpandedLimitsFailClosed()
    {
        var value=Zip("{\"value\":123}");Assert.Throws<InvalidDataException>(()=>StorageRecordCodec.Decode<object>(value[..^5]));
        Assert.Throws<InvalidDataException>(()=>StorageRecordCodec.Decode<object>(Zip("\""+new string('x',10000)+"\""),100));
        Assert.Throws<InvalidDataException>(()=>StorageRecordCodec.Decode<object>("not gzip"u8.ToArray()));
    }
    [Fact]public async Task IndependentParallelDecodesKeepExactValuesAndIsolateBadRows()
    {
        var good=Zip("{\"Amount\":0.1234567890123456789012345678}");
        await Parallel.ForEachAsync(Enumerable.Range(0,500),new ParallelOptions{MaxDegreeOfParallelism=8},(i,_)=>
        {
            if(i%19==0)Assert.Throws<InvalidDataException>(()=>StorageRecordCodec.Decode<object>(Zip("broken")));
            else Assert.Equal(.1234567890123456789012345678m,StorageRecordCodec.Decode<Dictionary<string,decimal>>(good)["Amount"]);
            return ValueTask.CompletedTask;
        });
    }
    [Fact]public void OneDataDirectoryCannotHaveTwoServerOwnersAndCanBeReopenedAfterExit()
    {
        var root=Path.Combine(Path.GetTempPath(),"PulletLease-"+Guid.NewGuid());
        try
        {
            using(var first=new ServerInstanceLease(root))Assert.Throws<InvalidOperationException>(()=>new ServerInstanceLease(root));
            using var next=new ServerInstanceLease(root);
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    [Fact]public async Task CurrentUserPipeRejectsInvalidRequestsThenRequestsGracefulHostStop()
    {
        var lifetime=new Lifetime();using var control=new LocalServerControl(lifetime,NullLogger<LocalServerControl>.Instance);
        await control.StartAsync(default);
        try
        {
            using(var wrong=new NamedPipeClientStream(".",LocalServerControl.PipeName(AppContext.BaseDirectory),PipeDirection.InOut,PipeOptions.Asynchronous))
            {await wrong.ConnectAsync(5000);await wrong.WriteAsync("noop\n"u8.ToArray());await wrong.FlushAsync();}
            Assert.False(lifetime.Stopped);
            using var client=new NamedPipeClientStream(".",LocalServerControl.PipeName(AppContext.BaseDirectory),PipeDirection.InOut,PipeOptions.Asynchronous);
            await client.ConnectAsync(5000);await client.WriteAsync("stop\n"u8.ToArray());await client.FlushAsync();
            var response=new byte[3];using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));await client.ReadExactlyAsync(response,timeout.Token);
            Assert.Equal("ok\n",Encoding.UTF8.GetString(response));await lifetime.Signal.Task.WaitAsync(TimeSpan.FromSeconds(5));Assert.True(lifetime.Stopped);
        }
        finally{await control.StopAsync(default);}
    }
    [Fact]public async Task GracefulShutdownCommitsEveryAcceptedWriteBeforeItReturns()
    {
        var root=Path.Combine(Path.GetTempPath(),"PulletDrain-"+Guid.NewGuid());var queue=new PersistenceQueue();
        var db=new SqliteDatabase(Options.Create(new StorageOptions{Directory=root}));
        using var worker=new PersistenceWorker(db,queue,new ProviderHealthStore(TimeProvider.System),NullLogger<PersistenceWorker>.Instance);
        try
        {
            await worker.StartAsync(default);await queue.Ready.Task;
            var id=(await queue.SyncAsync([new("TESTUSDT","TEST",DateTimeOffset.UtcNow)],default))[0].AssetId;
            var completions=new List<Task>();var now=DateTimeOffset.UtcNow;
            for(var i=0;i<150;i++)
            {
                var ack=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);completions.Add(ack.Task);
                var bar=new Candle("TESTUSDT",5,now.AddMinutes(-5*(i+1)),1,1,1,1,10,now,now);
                await queue.Channel.Writer.WriteAsync(new SaveSnapshotsCommand([(id,bar,null,null)],[],ack));
            }
            await worker.StopAsync(default);await Task.WhenAll(completions).WaitAsync(TimeSpan.FromSeconds(5));
            using var connection=db.Open(true);Assert.Equal(150,connection.ExecuteScalar<int>("SELECT count(*) FROM MarketSnapshot5m"));
            Assert.True(queue.Channel.Reader.Completion.IsCompletedSuccessfully);
        }
        finally{await worker.StopAsync(default);SqliteConnection.ClearAllPools();if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    [Fact]public void CorruptPaperAccountCannotBeRestoredAsANewAccountOrOverwriteTheStoredRecord()
    {
        var root=Path.Combine(Path.GetTempPath(),"PulletCorruptPaper-"+Guid.NewGuid());
        var db=new SqliteDatabase(Options.Create(new StorageOptions{Directory=root}));var payload=Zip("{\"Cash\": broken}");
        try
        {
            db.Initialize();
            using(var connection=db.Open())connection.Execute("INSERT INTO PaperAccount VALUES(1,42,@payload)",new{payload});
            var repository=new PaperRepository(db);
            for(var attempt=0;attempt<2;attempt++)
            {
                var error=Assert.Throws<InvalidDataException>(()=>repository.Restore());Assert.Contains("PaperState",error.Message);
                using var connection=db.Open();Assert.Equal(42,connection.ExecuteScalar<long>("SELECT Revision FROM PaperAccount WHERE Id=1"));
                Assert.Equal(payload,connection.QuerySingle<byte[]>("SELECT Payload FROM PaperAccount WHERE Id=1"));
                Assert.Equal(0,connection.ExecuteScalar<int>("SELECT count(*) FROM PaperTrade"));
            }
        }
        finally{SqliteConnection.ClearAllPools();if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    private sealed class Lifetime:IHostApplicationLifetime
    {
        public bool Stopped;public TaskCompletionSource Signal {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken ApplicationStarted=>default;public CancellationToken ApplicationStopping=>default;public CancellationToken ApplicationStopped=>default;
        public void StopApplication(){Stopped=true;Signal.TrySetResult();}
    }
}
