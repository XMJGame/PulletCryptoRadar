using System.Net;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;

namespace PulletCryptoRadar.Tests;

public sealed class SharedFundingInfoTests : IDisposable
{
    private static readonly DateTimeOffset Now=new(2026,11,4,1,0,0,TimeSpan.Zero);
    private readonly string folder=Path.Combine(Path.GetTempPath(),"radar-funding-cache-"+Guid.NewGuid());
    private readonly FoundationStore store;
    private sealed class Clock:TimeProvider {public DateTimeOffset At=Now; public override DateTimeOffset GetUtcNow()=>At;}
    private sealed class Handler:HttpMessageHandler
    {
        public int Calls;public bool Fail;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new HttpResponseMessage(Fail?HttpStatusCode.BadGateway:HttpStatusCode.OK)
            {Content=new StringContent(Fail?"{}":"""[{"symbol":"AUSDT","fundingIntervalHours":4},{"symbol":"BUSDT","fundingIntervalHours":1}]""")});
        }
    }
    public SharedFundingInfoTests(){store=new(folder);store.Initialize(Now);}
    private static AssetIdentity Asset(string symbol)=>new(Guid.NewGuid(),symbol,symbol,symbol+"USDT",null,null,[],Now);
    [Fact]
    public async Task ConcurrentAssetsShareOneDownloadAndOriginalEvidenceAcrossRestart()
    {
        var clock=new Clock();var handler=new Handler();using var http=new HttpClient(handler);
        var cache=new SharedFundingInfoCache(store,clock);
        var provider=new BinanceMarketProvider(http,store,clock,cache);
        var assets=new[]{Asset("A"),Asset("B"),Asset("C")};foreach(var asset in assets)store.Register(asset);
        var reads=await Task.WhenAll(assets.Select(asset=>provider.ReadAsync(asset,"FundingInfo",default)));
        Assert.Equal(1,handler.Calls);Assert.Single(reads.Select(item=>item.Evidence.Id).Distinct());
        Assert.Equal(new decimal?[]{4,1,8},reads.Select(item=>Assert.Single(item.Observations).Value));
        for(var index=0;index<reads.Length;index++)
        {
            Assert.Equal(assets[index].AssetId,Assert.Single(reads[index].Observations).AssetId);
            store.Save(new("test",assets[index].AssetId,Now,MarketState.Unknown,[]),[reads[index]]);
        }
        clock.At=Now.AddMinutes(4);
        var reopened=new FoundationStore(folder);
        var next=await new BinanceMarketProvider(http,reopened,clock,new(reopened,clock)).ReadAsync(assets[1],"FundingInfo",default);
        Assert.Equal(1,handler.Calls);Assert.Equal(Now,next.Evidence.ReceivedTime);
        Assert.Equal(Now,Assert.Single(next.Observations).ReceivedTime);
        Assert.Null(Assert.Single(next.Observations).SourceTime);
        Assert.Empty(reopened.SharedFundingInfo()!.Observations);
    }
    [Fact]
    public async Task ExpiredCacheFailureDoesNotReturnStaleScheduleAndRecoveryRefetches()
    {
        var clock=new Clock();var handler=new Handler();using var http=new HttpClient(handler);
        var provider=new BinanceMarketProvider(http,store,clock,new(store,clock));
        var first=await provider.ReadAsync(Asset("A"),"FundingInfo",default);
        clock.At=Now.AddMinutes(5);handler.Fail=true;
        var failed=await provider.ReadAsync(Asset("B"),"FundingInfo",default);
        Assert.Equal(CollectionFailure.Transport,failed.Evidence.Failure);Assert.Empty(failed.Observations);
        handler.Fail=false;
        var recovered=await provider.ReadAsync(Asset("B"),"FundingInfo",default);
        Assert.Equal(3,handler.Calls);Assert.NotEqual(first.Evidence.Id,recovered.Evidence.Id);
        Assert.Equal(clock.At,recovered.Evidence.ReceivedTime);
    }
    [Fact]
    public async Task ClockRollbackCannotTreatFutureCacheAsFresh()
    {
        var clock=new Clock();var handler=new Handler();using var http=new HttpClient(handler);
        var provider=new BinanceMarketProvider(http,store,clock,new(store,clock));
        await provider.ReadAsync(Asset("A"),"FundingInfo",default);
        clock.At=Now.AddSeconds(-1);
        await provider.ReadAsync(Asset("B"),"FundingInfo",default);
        Assert.Equal(2,handler.Calls);
    }
    public void Dispose(){foreach(var file in Directory.GetFiles(folder))File.Delete(file);Directory.Delete(folder);}
}
