using PulletCryptoRadar.Core;
using PulletCryptoRadar.DataFoundation;
using PulletCryptoRadar.Host;
using PulletCryptoRadar.Infrastructure;

namespace PulletCryptoRadar.Tests;

public sealed class ResearchMarketTests:IDisposable
{
    private static readonly DateTimeOffset Now=new(2026,11,4,8,0,0,TimeSpan.Zero);
    private readonly string folder=Path.Combine(Path.GetTempPath(),"research-market-"+Guid.NewGuid());
    private readonly FoundationStore store;
    private readonly AssetIdentity[] samples;
    public ResearchMarketTests()
    {
        store=new(folder);store.Initialize(Now);
        samples=Enumerable.Range(0,3).Select(i=>new AssetIdentity(BinanceIdentity.AssetId($"S{i}USDT"),$"S{i}",$"Sample {i}",$"S{i}USDT",
            $"sample-{i}",null,[new(ChainId.Ethereum,"0x"+(i+1).ToString("x40"),18)],Now)).ToArray();
        foreach(var asset in samples)store.Register(asset);
    }
    [Fact]
    public void NoCapacityDoesNotConsumeScheduleAndOneSlotPrioritizesOi()
    {
        var ids=samples.Select(asset=>asset.AssetId).ToArray();
        Assert.Null(store.ReserveResearchMarket(ids,Now,0));Assert.Empty(store.ResearchMarketSchedules());
        var work=store.ReserveResearchMarket(ids,Now,1)!;
        Assert.True(work.OpenInterest);Assert.False(work.Candles);
    }
    [Fact]
    public void InitialSamplesRotateAndRestartRetainsFiveMinuteAndHourBoundaries()
    {
        var ids=samples.Select(asset=>asset.AssetId).ToArray();var selected=new HashSet<Guid>();
        for(var i=0;i<3;i++)
        {
            var work=store.ReserveResearchMarket(ids,Now,2)!;Assert.True(selected.Add(work.AssetId));
            store.CompleteResearchMarket(work,true,true,Now);store.CompleteResearchMarket(work,false,true,Now);store.ReleaseResearchMarket(work);
        }
        Assert.Null(store.ReserveResearchMarket(ids,Now.AddMinutes(4),2));
        var restarted=new FoundationStore(folder);restarted.Initialize(Now.AddMinutes(4));
        Assert.All(restarted.ResearchMarketSchedules(),row=>{Assert.Equal(Now.AddMinutes(5),row.NextOiAt);Assert.Equal(Now.AddHours(1).AddMinutes(1),row.NextCandlesAt);});
        var oi=restarted.ReserveResearchMarket(ids,Now.AddMinutes(5),2)!;
        Assert.True(oi.OpenInterest);Assert.False(oi.Candles);
    }
    [Fact]
    public async Task LeaseIsAtomicAndAbandonedWorkExpiresWithoutStaleCompletionOverwritingNewLease()
    {
        var id=samples[0].AssetId;
        var works=await Task.WhenAll(Enumerable.Range(0,6).Select(_=>Task.Run(()=>store.ReserveResearchMarket([id],Now,2))));
        var original=Assert.Single(works,work=>work is not null)!;
        Assert.Null(store.ReserveResearchMarket([id],Now.AddMinutes(1),2));
        var recovered=store.ReserveResearchMarket([id],Now.AddMinutes(2),2)!;
        store.CompleteResearchMarket(original,true,true,Now.AddHours(1));store.ReleaseResearchMarket(original);
        Assert.Null(Assert.Single(store.ResearchMarketSchedules()).NextOiAt);
        store.CompleteResearchMarket(recovered,true,true,Now.AddMinutes(2));store.ReleaseResearchMarket(recovered);
        Assert.Equal(Now.AddMinutes(7),Assert.Single(store.ResearchMarketSchedules()).NextOiAt);
    }
    [Fact]
    public void FailureHonorsCooldownAndNeverBecomesSuccess()
    {
        var work=store.ReserveResearchMarket([samples[0].AssetId],Now,2)!;
        store.CompleteResearchMarket(work,true,false,Now,Now.AddMinutes(15));store.ReleaseResearchMarket(work);
        var state=Assert.Single(store.ResearchMarketSchedules());Assert.Null(state.LastOiSuccessAt);Assert.Equal(Now.AddMinutes(15),state.NextOiAt);
        var remaining=store.ReserveResearchMarket([samples[0].AssetId],Now.AddMinutes(1),1)!;
        Assert.False(remaining.OpenInterest);Assert.True(remaining.Candles);
    }
    [Fact]
    public void DelayedOiSourceBringsNextRefreshForwardInsteadOfTreatingReceiptAsDataTime()
    {
        var work=store.ReserveResearchMarket([samples[0].AssetId],Now,2)!;
        store.CompleteResearchMarket(work,true,true,Now,sourceTime:Now.AddMinutes(-7));store.ReleaseResearchMarket(work);
        Assert.Equal(Now.AddMinutes(1),Assert.Single(store.ResearchMarketSchedules()).NextOiAt);
    }
    [Fact]
    public void RejectsUnboundedUnreviewedAndBenchmarkCandidates()
    {
        Assert.Throws<ArgumentException>(()=>store.ReserveResearchMarket(Enumerable.Range(0,5).Select(_=>Guid.NewGuid()).ToArray(),Now,2));
        Assert.Throws<InvalidDataException>(()=>store.ReserveResearchMarket([Guid.NewGuid()],Now,2));
        var btc=samples[0] with {AssetId=BinanceIdentity.AssetId("BTCUSDT"),BinanceSymbol="BTCUSDT"};store.Register(btc with {Chains=[]});
        Assert.Throws<InvalidDataException>(()=>store.ReserveResearchMarket([btc.AssetId],Now,2));
    }
    private sealed class Clock:TimeProvider
    {
        public DateTimeOffset At=Now;
        public override DateTimeOffset GetUtcNow()=>At;
    }
    private sealed class Provider(Clock clock,FoundationStore ledger):IMarketDataProvider
    {
        public readonly List<(string Capability,RequestPriority Priority)> Calls=[];
        public bool Fail;
        public bool Incomplete;
        public Task<ProviderRead> ReadAsync(AssetIdentity asset,string capability,CancellationToken token)
        {
            var now=clock.At=clock.At.AddMilliseconds(10);var id=Guid.NewGuid();var priority=CollectionRequestScope.Current;
            Calls.Add((capability,priority));var decision=ledger.TryReserve(id,"Binance",now,262144,priority);
            var failed=Fail?CollectionFailure.RateLimited:!decision.Allowed?CollectionFailure.Quota:CollectionFailure.None;
            var retry=Fail?now.AddMinutes(15):decision.RetryAt;
            if(decision.Allowed)ledger.CompleteWithReason(id,100,retry,Fail?"CooldownAfterRateLimit":null);
            var evidence=new RawEvidence(id,"Binance",asset.BinanceSymbol,capability=="Candles"?"/fapi/v1/klines?symbol="+asset.BinanceSymbol:
                "/futures/data/openInterestHist?symbol="+asset.BinanceSymbol,now,failed==CollectionFailure.None?200:429,failed,null,RetryAt:retry,Priority:priority);
            if(failed!=CollectionFailure.None)return Task.FromResult(new ProviderRead(evidence,[]));
            if(capability=="OpenInterestHistory")
            {
                var latest=DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds()/300000*300000);
                var points=Enumerable.Range(0,Incomplete?1:289).Select(i=>new OpenInterestPoint(asset.AssetId,latest.AddMinutes((i-288)*5),now,1000+i,100000+i*100)).ToArray();
                return Task.FromResult(new ProviderRead(evidence,[],OpenInterestHistory:points));
            }
            var close=new DateTimeOffset(now.Year,now.Month,now.Day,now.Hour,0,0,TimeSpan.Zero);
            var candles=Enumerable.Range(0,Incomplete?10:300).Select(i=>new HourCandle(asset.AssetId,close.AddHours(i-300),close.AddHours(i-299),
                now,100,102,99,101,1000,600)).ToArray();
            return Task.FromResult(new ProviderRead(evidence,[],Candles:candles));
        }
    }
    [Fact]
    public async Task CollectorBuildsAllFiveMarketMetricsWithoutOpeningPaperPositionsAndDoesNotRepeatCandleDownloads()
    {
        var clock=new Clock();var provider=new Provider(clock,store);var pipeline=new CollectionPipeline(provider,store,clock);
        var collector=new ResearchMarketCollector(store,pipeline,clock);
        Assert.Equal(samples[0].AssetId,await collector.CollectNextAsync([samples[0]],default));
        Assert.Equal(new[]{"OpenInterestHistory","Candles"},provider.Calls.Select(call=>call.Capability));
        Assert.All(provider.Calls,call=>Assert.Equal(RequestPriority.Background,call.Priority));
        var metrics=store.Latest(samples[0].AssetId)!.Metrics;
        foreach(var metric in new[]{Metric.Return24hClosed,Metric.VolumeRatioH1,Metric.TakerBuyShareH1,Metric.OiQuantityChange1h,Metric.OiQuantityChange4h})
            Assert.True(Assert.Single(metrics,row=>row.Metric==metric).UsableForScoring);
        var restarted=new FoundationStore(folder);restarted.Initialize(clock.At);
        clock.At=Now.AddMinutes(2);Assert.Null(await new ResearchMarketCollector(restarted,new(provider,restarted,clock),clock).CollectNextAsync([samples[0]],default));
        clock.At=Now.AddMinutes(6);await collector.CollectNextAsync([samples[0]],default);
        Assert.Equal(3,provider.Calls.Count);Assert.Equal("OpenInterestHistory",provider.Calls[^1].Capability);
        Assert.Equal(0,store.Portfolio().Revision);Assert.Empty(store.LatestEntries());
    }
    [Fact]
    public async Task CollectorStopsAfterProviderCooldownAndRetainsFailureInsteadOfClaimingCoverage()
    {
        var clock=new Clock();var provider=new Provider(clock,store){Fail=true};var pipeline=new CollectionPipeline(provider,store,clock);
        await new ResearchMarketCollector(store,pipeline,clock).CollectNextAsync([samples[0]],default);
        Assert.Single(provider.Calls);Assert.Null(Assert.Single(store.ResearchMarketSchedules()).LastOiSuccessAt);
        Assert.DoesNotContain(store.Latest(samples[0].AssetId)!.Metrics,row=>row.Metric==Metric.OiQuantityChange1h&&row.UsableForScoring);
    }
    [Fact]
    public async Task HttpSuccessWithIncompleteOrStaleHistoryIsNotSuccessfulFeatureCoverage()
    {
        var clock=new Clock();var provider=new Provider(clock,store){Incomplete=true};var pipeline=new CollectionPipeline(provider,store,clock);
        await new ResearchMarketCollector(store,pipeline,clock).CollectNextAsync([samples[0]],default);
        var schedule=Assert.Single(store.ResearchMarketSchedules());
        Assert.Null(schedule.LastOiSuccessAt);Assert.Null(schedule.LastCandlesSuccessAt);
        Assert.Equal(clock.At.AddMinutes(15),schedule.NextCandlesAt);
    }
    public void Dispose(){if(Directory.Exists(folder))Directory.Delete(folder,true);}
}
