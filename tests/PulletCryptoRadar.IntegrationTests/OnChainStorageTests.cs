using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;

namespace PulletCryptoRadar.IntegrationTests;

public sealed class OnChainStorageTests:IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"Pullet-Chain-"+Guid.NewGuid());
    private static readonly DateTimeOffset Now=new(2026,10,29,8,0,0,TimeSpan.Zero);
    private const string Contract="0x1111111111111111111111111111111111111111";
    private SqliteDatabase Db(){var db=new SqliteDatabase(Options.Create(new StorageOptions{Directory=_root}));db.Initialize();return db;}
    private static ChainBinding Binding(int id)=>new(id,"bsc",Contract,"coin",true,true,1,Now,"Fixture",[]);
    private static OnChainSample Sample()=>new(Guid.NewGuid(),"bsc",Contract,"Binplorer",Now,Now,"Fixture",1000,"1000000000000000000000000000",18,[new("0x2222222222222222222222222222222222222222","100000000000000000000000000",10)],100,true);
    [Fact] public void RequestBudgetAndNextAttemptSurviveRestartAndRejectDuplicates()
    {
        var db=Db();var attempt=new ChainAttempt("bsc",Contract,Now,Now.AddMinutes(30),null);var r=new ChainReservation(attempt,3,50,3,1000);
        Assert.True(db.ReserveOnChain(r));Assert.False(db.ReserveOnChain(r));Assert.False(db.ReserveOnChain(r with{Attempt=attempt with{LastAttempt=Now.AddMinutes(31),NextAttempt=Now.AddHours(1)}}));
        Assert.Equal(attempt.NextAttempt,Assert.Single(new OnChainRepository(db).Restore().Attempts).NextAttempt);
        using var c=db.Open();Assert.Equal(3,c.QuerySingle<int>("SELECT SUM(Requests) FROM OnChainBudget"));
    }
    [Fact] public void BindingAndLabelCasKeepAuditsAndRejectOldEditors()
    {
        var db=Db();var id=db.Synchronize([new("TESTUSDT","TEST",Now)])[0].AssetId;var b=Binding(id);db.SaveChainBinding(b,0);
        Assert.Throws<MappingConflictException>(()=>db.SaveChainBinding(b with{Revision=2},0));
        var label=new AddressLabel("bsc",Contract,WalletLabelType.Exchange,true,"https://example.com",1,Now);db.SaveAddressLabel(label,0);
        Assert.Throws<MappingConflictException>(()=>db.SaveAddressLabel(label with{Revision=2},0));using var c=db.Open();Assert.Equal(1,c.QuerySingle<int>("SELECT COUNT(*) FROM AddressLabelAudit"));
    }
    [Fact] public void SnapshotsStayExactAndProviderHistoryIsNotMerged()
    {
        var db=Db();var sample=Sample();var a=new ChainAttempt("bsc",Contract,Now,Now.AddMinutes(30),null);db.SaveOnChain(new(sample,a));db.SaveOnChain(new(sample,a));
        db.SaveOnChain(new(sample with{Id=Guid.NewGuid(),Provider="GoPlus",HolderCount=950,RequestedCount=10},a));
        var history=new OnChainRepository(db).History("bsc",Contract,10);Assert.Equal(2,history.Count);Assert.Equal(sample.Holders[0].RawBalance,history.Single(s=>s.Provider=="Binplorer").Holders[0].RawBalance);
        Assert.Throws<InvalidDataException>(()=>db.SaveOnChain(new(sample with{ContractAddress="wrong"},a)));
    }
    [Fact] public async Task SingleWriterServiceValidatesIdentityLabelsAndRestoresFeatures()
    {
        var db=Db();var queue=new PersistenceQueue();using var worker=new PersistenceWorker(db,queue,new ProviderHealthStore(new Clock()),NullLogger<PersistenceWorker>.Instance);await worker.StartAsync(default);await queue.Ready.Task;
        try
        {
            var market=new MarketRuntimeStore();market.Register(await queue.SyncAsync([new("TESTUSDT","TEST",Now)],default));var id=market.GetAll()[0].Instrument.AssetId;
            var capital=new CapitalRuntimeStore();capital.SetCatalog(new(Now,[new("coin","test","Test",new Dictionary<string,string>{{"binance-smart-chain",Contract}})]));
            OnChainService Service()=>new(new OnChainRepository(db),new OnChainPersistence(queue),market,capital,new(),new Clock());
            var service=Service();await service.RestoreAsync(default);
            await Assert.ThrowsAsync<ArgumentException>(()=>service.SaveBindingAsync(id,new("bsc",Contract,"coin",true,0,false),default));
            await service.SaveBindingAsync(id,new("bsc",Contract,"coin",true,0,true),default);
            await Assert.ThrowsAsync<ArgumentException>(()=>service.SaveLabelAsync(new("bsc",Contract,WalletLabelType.LP,"",0,false),default));
            await service.SaveLabelAsync(new("bsc",Contract,WalletLabelType.LP,"https://example.com",0,true),default);
            await service.SaveLabelAsync(new("bsc",Contract,WalletLabelType.Unknown,"",1,false),default);
            await service.SaveSampleAsync(new(Sample(),new("bsc",Contract,Now,Now.AddMinutes(30),null)),default);
            var restored=Service();await restored.RestoreAsync(default);Assert.True(restored.Features(id,Now).TrustedIdentity);Assert.Equal(2,Assert.Single(restored.Features(id,Now).Labels).Revision);
            Assert.Null(restored.Features(id,Now).HolderChange24h.Value);
        }
        finally{await worker.StopAsync(default);}
    }
    private sealed class Clock:TimeProvider{public override DateTimeOffset GetUtcNow()=>Now;}
    public void Dispose(){SqliteConnection.ClearAllPools();if(Directory.Exists(_root))Directory.Delete(_root,true);}
}
