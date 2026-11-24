using System.Net;
using System.Text.Json;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;

namespace PulletCryptoRadar.Tests;

public sealed class HolderResearchTests:IDisposable
{
    private static readonly DateTimeOffset Now=new(2026,11,4,8,0,0,TimeSpan.Zero);
    private const string Contract="0x1111111111111111111111111111111111111111";
    private static readonly AssetIdentity Asset=new(BinanceIdentity.AssetId("TESTUSDT"),"TEST","Test","TESTUSDT",null,null,
        [new(ChainId.Bsc,Contract,18)],Now);
    private readonly string folder=Path.Combine(Path.GetTempPath(),"holder-research-"+Guid.NewGuid());
    private static string Body(int count=10,int holderCount=20,int offset=0,bool duplicate=false,bool reverse=false,string contract=Contract)
    {
        var rows=Enumerable.Range(0,count).Select(i=>new {address="0x"+(duplicate?2:i+2).ToString("x40"),balance=(1000-i*10+offset).ToString(),
            percent=((1000m-i*10+offset)/10000m).ToString(System.Globalization.CultureInfo.InvariantCulture),tag="",is_contract=i==0?1:0,is_locked=0}).ToArray();
        return JsonSerializer.Serialize(new {code=1,result=new Dictionary<string,object>{{contract,new {total_supply="10000",holder_count=holderCount.ToString(),holders=reverse?rows.Reverse().ToArray():rows}}}});
    }
    private static HolderRead Parse(string body,DateTimeOffset? at=null)=>GoPlusHolderProvider.Parse(Asset,body,at??Now,Guid.NewGuid(),GoPlusHolderProvider.Endpoint(Asset)!);

    [Fact]
    public void StoresRealAddressesWithoutInventingOriginalTimeOrCanonicalMetrics()
    {
        var read=Parse(Body());var snapshot=read.Snapshot!;
        Assert.Equal(95.5m,snapshot.ListedSupplyPercent);Assert.Equal(20,snapshot.HolderCount);
        Assert.Equal(10,snapshot.Addresses.Count);Assert.True(snapshot.Addresses[0].IsContract);
        Assert.Empty(read.Read.Observations);Assert.Null(read.Read.Evidence.ResponseDate);
        Assert.Equal(Now,snapshot.ReceivedAt);Assert.Equal("ProviderTop10Addresses",snapshot.Scope);
    }
    [Theory]
    [InlineData(9,20,false,false)]
    [InlineData(10,0,false,false)]
    [InlineData(10,20,true,false)]
    [InlineData(10,20,false,true)]
    public void RejectsIncompleteZeroCoverageDuplicateOrUnorderedLists(int count,int holders,bool duplicate,bool reverse)
        =>Assert.Throws<InvalidDataException>(()=>Parse(Body(count,holders,duplicate:duplicate,reverse:reverse)));
    [Fact]
    public void RejectsWrongTokenChainAndInconsistentBalancePercent()
    {
        Assert.Throws<InvalidDataException>(()=>Parse(Body(contract:"0x2222222222222222222222222222222222222222")));
        Assert.Throws<InvalidDataException>(()=>GoPlusHolderProvider.Parse(Asset,Body(),Now,Guid.NewGuid(),GoPlusHolderProvider.Endpoint(Asset)!.Replace("/56?","/1?")));
        Assert.Throws<InvalidDataException>(()=>Parse(Body().Replace("\"percent\":\"0.1\"","\"percent\":\"0.9\"")));
    }
    [Fact]
    public void SolanaKeepsAccountListAndDoesNotMergeCaseOrClaimVerifiedWallets()
    {
        const string mint="EKpQGSJtjMFqKZ9KQanSqYXRcF8fBopzLHYxdM65zcjm";
        const string address="51yZyDSnec4xnUv7XLRVYcDyV4x3wUtzrDcRaYbmQU5j";
        const string account="9HSgJUDbGigu6Qj1ya1yuwGqg8CAatEMra8sWwDRA7rH";
        var asset=Asset with {Chains=[new(ChainId.Solana,mint,6)]};
        var body=JsonSerializer.Serialize(new {code=1,result=new Dictionary<string,object>{{mint,new {total_supply="100",holder_count="1",
            holders=new[]{new {account=address,token_account=account,balance="100",percent="1",tag="",is_locked=0}}}}}});
        var read=GoPlusHolderProvider.Parse(asset,body,Now,Guid.NewGuid(),GoPlusHolderProvider.Endpoint(asset)!);
        Assert.Equal("ProviderTop10SolanaAccounts",read.Snapshot!.Scope);
        Assert.Equal(account,Assert.Single(Assert.Single(read.Snapshot.Addresses).TokenAccounts));
        Assert.Throws<InvalidDataException>(()=>GoPlusHolderProvider.Parse(asset,body.Replace(mint,mint.ToLowerInvariant()),Now,Guid.NewGuid(),GoPlusHolderProvider.Endpoint(asset)!));
    }
    [Fact]
    public void BaselineRequiresElapsedTimeAndMatchingIdentityAndReportsListChurnAsUnknownBalance()
    {
        var first=Parse(Body()).Snapshot!;var second=Parse(Body(offset:1),Now.AddHours(6)).Snapshot!;
        Assert.Equal("WaitingForBaseline",HolderResearch.Compare(first,null).Status);
        Assert.Equal("Incomparable",HolderResearch.Compare(second with {ReceivedAt=Now.AddHours(1)},first).Status);
        Assert.Equal("Incomparable",HolderResearch.Compare(second with {Chain=ChainId.Ethereum},first).Status);
        var comparison=HolderResearch.Compare(second,first);
        Assert.Equal(.1m,comparison.ListedShareChangePoints);Assert.All(comparison.Addresses,row=>Assert.Equal(1m,row.BalanceChange));
        var changed=second with {Addresses=second.Addresses.Select((row,i)=>i==9?row with {Address="0x9999999999999999999999999999999999999999"}:row).ToArray()};
        var churn=HolderResearch.Compare(changed,first);
        Assert.Null(Assert.Single(churn.Addresses,row=>row.Status=="EnteredList").PreviousBalance);
        Assert.Null(Assert.Single(churn.Addresses,row=>row.Status=="LeftList").CurrentBalance);
    }
    [Fact]
    public void HistorySurvivesRestartAndFailurePreservesLastGoodSnapshotAndAccount()
    {
        var store=new FoundationStore(folder);store.Initialize(Now);store.Register(Asset);
        var account=JsonSerializer.Serialize(store.Portfolio());
        store.SaveHolderRead(Parse(Body()).Read);store.SaveHolderRead(Parse(Body(offset:1),Now.AddHours(6)).Read);
        var failed=new ProviderRead(new(Guid.NewGuid(),"GoPlus",Asset.BinanceSymbol,GoPlusHolderProvider.Endpoint(Asset)!,Now.AddHours(7),429,
            CollectionFailure.RateLimited,null,FailureDetail:"CooldownAfterRateLimit",RetryAt:Now.AddHours(8)),[]);
        store.SaveHolderRead(failed);
        var restarted=new FoundationStore(folder);restarted.Initialize(Now.AddHours(7));
        Assert.Equal(2,restarted.HolderHistory(Asset.AssetId).Count);
        Assert.Equal(Now.AddHours(6),restarted.HolderHistory(Asset.AssetId)[0].ReceivedAt);
        Assert.Equal(CollectionFailure.RateLimited,Assert.Single(restarted.SourceHealth(Now.AddHours(7))).Failure);
        Assert.Equal(account,JsonSerializer.Serialize(restarted.Portfolio()));
    }
    [Fact]
    public void GoPlusBudgetAllowsThreePerMinuteAndSharesCooldownAcrossAssets()
    {
        var store=new FoundationStore(folder);store.Initialize(Now);
        for(var i=0;i<3;i++)Assert.True(store.TryReserve(Guid.NewGuid(),"GoPlus",Now,100,RequestPriority.Background).Allowed);
        var denied=store.TryReserve(Guid.NewGuid(),"GoPlus",Now,100,RequestPriority.Background);
        Assert.Equal("ProviderMinuteLimit",denied.Reason);
        var id=Guid.NewGuid();Assert.True(store.TryReserve(id,"GoPlus",Now.AddMinutes(2),100,RequestPriority.Background).Allowed);
        store.CompleteWithReason(id,0,Now.AddMinutes(20),"CooldownAfterRateLimit");
        Assert.Equal("CooldownAfterRateLimit",store.TryReserve(Guid.NewGuid(),"GoPlus",Now.AddMinutes(3),100,RequestPriority.Background).Reason);
    }
    [Fact]
    public void RadarDisplaysRealSupplementaryHoldingsButDoesNotGrantResearchQualification()
    {
        var snapshot=Parse(Body()).Snapshot!;
        var row=new PulletCryptoRadar.Features.RadarBoardBuilder().Row(Asset,null,Now,snapshot);
        var metric=Assert.Single(row.Metrics,m=>m.Metric=="Top10");
        Assert.Equal("GoPlus",metric.Provider);Assert.Equal(95.5m,metric.Value);Assert.Equal("UnverifiedTime",metric.Status);
        Assert.False(row.DataQualified);Assert.Null(Assert.Single(metric.Sources).SourceTime);
        var wrong=snapshot with {Chain=ChainId.Ethereum};
        Assert.Null(Assert.Single(new PulletCryptoRadar.Features.RadarBoardBuilder().Row(Asset,null,Now,wrong).Metrics,m=>m.Metric=="Top10").Value);
    }
    private sealed class ResponseHandler:HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {Calls++;return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));}
    }
    [Fact]
    public async Task RateLimitIsRecordedAndNextRequestDoesNotReachNetwork()
    {
        var store=new FoundationStore(folder);store.Initialize(DateTimeOffset.UtcNow);var handler=new ResponseHandler();
        using var http=new HttpClient(handler);var provider=new GoPlusHolderProvider(http,store,TimeProvider.System);
        var first=await provider.ReadAsync(Asset,CancellationToken.None);
        Assert.Equal(CollectionFailure.RateLimited,first.Read.Evidence.Failure);Assert.Null(first.Snapshot);
        var second=await provider.ReadAsync(Asset,CancellationToken.None);
        Assert.Equal(CollectionFailure.Quota,second.Read.Evidence.Failure);Assert.Equal(1,handler.Calls);
    }
    public void Dispose(){if(Directory.Exists(folder))Directory.Delete(folder,true);}
}
