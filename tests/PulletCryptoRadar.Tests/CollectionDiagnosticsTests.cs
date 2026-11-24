using System.Net;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;

namespace PulletCryptoRadar.Tests;

public sealed class CollectionDiagnosticsTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026,11,3,10,0,0,TimeSpan.Zero);
    private readonly string folder = Path.Combine(Path.GetTempPath(),"radar-diagnostics-"+Guid.NewGuid());
    private readonly FoundationStore store;
    public CollectionDiagnosticsTests() { store = new(folder); store.Initialize(Now); }
    private static AssetIdentity Asset(string symbol) => new(Guid.NewGuid(),symbol,symbol,symbol+"USDT",null,null,[],Now);
    private void Setup(AssetIdentity asset,DateTimeOffset expires,DateTimeOffset? evaluated = null)
    {
        store.Register(asset);
        var setup = new EntrySetup(Guid.NewGuid(),asset.AssetId,EntryStrategy.TrendPullback,TradeSide.Long,
            SetupPhase.AwaitingConfirmation,Now.AddHours(-1),Now.AddHours(-1),expires,100,101,99,1);
        store.SaveEntry(new("test",asset.AssetId,setup.Strategy,setup.Side,evaluated??Now,"Waiting",false,
            setup,["AwaitNextClosedBar"],Now,Now,null));
    }
    private void Universe(params string[] symbols)
    {
        var read = new ProviderRead(new(Guid.NewGuid(),"Binance","*","Universe",Now,200,CollectionFailure.None,"{}"),[],
            Universe:symbols.Select(symbol=>new MarketInstrument(symbol,symbol[..^4],Now)).ToArray());
        store.SaveMarketContext(new("test",Now,MarketState.Bull,symbols.Length,symbols.Length,1,1,1,[]),[read]);
    }
    [Fact]
    public void BackgroundLeavesSixRequestsForManualChecksAndExecution()
    {
        Assert.Equal(18,store.BackgroundRequestHeadroom(Now));
        for (var index = 0; index < 24; index++) Assert.True(store.Reserve(Guid.NewGuid(),"Binance",Now,1));
        Assert.Equal(0,store.BackgroundRequestHeadroom(Now));
        for (var index = 0; index < 6; index++) Assert.True(store.Reserve(Guid.NewGuid(),"Binance",Now,1));
        Assert.False(store.Reserve(Guid.NewGuid(),"Binance",Now,1));
        Assert.Equal(18,store.BackgroundRequestHeadroom(Now.AddSeconds(61)));
    }
    [Fact]
    public void FourSymbolRotationKeepsUnseenSymbolsAheadOfRevisits()
    {
        var universe = Enumerable.Range(0,9).Select(index=>new MarketInstrument("T"+index+"USDT","T"+index,Now)).ToArray();
        var first = store.ReserveDepthBatch(universe,4,Now);
        var second = new FoundationStore(folder).ReserveDepthBatch(universe,4,Now.AddMinutes(1));
        var third = store.ReserveDepthBatch(universe,4,Now.AddMinutes(2));
        Assert.Equal(9,first.Concat(second).Concat(third).Select(item=>item.Symbol).Distinct().Count());
    }
    [Fact]
    public void ActivePatternsGetFairFollowupAndExpiredPatternsDropOut()
    {
        var assets = Enumerable.Range(0,5).Select(index=>Asset("T"+index)).ToArray();
        foreach (var asset in assets) Setup(asset,Now.AddHours(2));
        Setup(Asset("Expired"),Now.AddSeconds(-1));
        var first = store.CollectionFocus(Now).ToArray();
        foreach (var asset in first) store.ReserveJob("Focus:"+asset.AssetId,Now,TimeSpan.FromMinutes(2));
        var second = new FoundationStore(folder).CollectionFocus(Now.AddMinutes(1));
        Assert.Empty(first.Select(item=>item.AssetId).Intersect(second.Select(item=>item.AssetId)));
        foreach (var asset in second) store.ReserveJob("Focus:"+asset.AssetId,Now.AddMinutes(1),TimeSpan.FromMinutes(2));
        var third = store.CollectionFocus(Now.AddMinutes(2),1);
        Assert.DoesNotContain(third[0].AssetId,first.Concat(second).Select(item=>item.AssetId));
        Assert.Empty(store.CollectionFocus(Now.AddHours(3)));
    }
    [Fact]
    public void DiagnosticsSeparateMissingCoverageStaleEvaluationAndExecutionHistory()
    {
        var a = Asset("A"); var b = Asset("B");
        Setup(a,Now.AddHours(1)); Setup(b,Now.AddHours(1),Now.AddMinutes(-4));
        Universe("AUSDT","BUSDT","CUSDT");
        store.SavePaperAdmission(a.AssetId,Guid.NewGuid(),Guid.NewGuid(),Now.AddMinutes(-1),"RiskRejected","test");
        store.SavePaperAdmission(a.AssetId,Guid.NewGuid(),Guid.NewGuid(),Now.AddMinutes(1),"FutureRejected","test");
        var report = store.CandidateDiagnostics(Now);
        Assert.True(report.UniverseFresh);
        Assert.Equal(3,report.EligibleContracts);
        Assert.Equal("Waiting",report.Candidates.Single(item=>item.Symbol=="AUSDT").State);
        Assert.Equal("RiskRejected",report.Candidates.Single(item=>item.Symbol=="AUSDT").LastAdmission);
        Assert.Equal("Stale",report.Candidates.Single(item=>item.Symbol=="BUSDT").State);
        Assert.Equal("NotEvaluated",report.Candidates.Single(item=>item.Symbol=="CUSDT").State);
        Assert.False(store.CandidateDiagnostics(Now.AddHours(9)).UniverseFresh);
    }
    private sealed class Clock : TimeProvider
    {
        private int calls;
        public override DateTimeOffset GetUtcNow() => ++calls < 3 ? Now : Now.AddMilliseconds(300);
    }
    private sealed class Handler(int lead) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                $$"""{"symbol":"TESTUSDT","markPrice":"100","lastFundingRate":"0.001","time":{{Now.AddMilliseconds(lead).ToUnixTimeMilliseconds()}}}""") });
    }
    [Fact]
    public async Task SmallClockLeadWaitsForActualTimeAndPreservesProviderTimestamp()
    {
        using var http = new HttpClient(new Handler(200));
        var read = await new BinanceMarketProvider(http,store,new Clock()).ReadAsync(Asset("TEST"),"Mark",default);
        Assert.Equal(CollectionFailure.None,read.Evidence.Failure);
        Assert.All(read.Observations,item=>Assert.Equal(Now.AddMilliseconds(200),item.SourceTime));
        Assert.All(read.Observations,item=>Assert.True(item.SourceTime<=item.ReceivedTime));
    }
    [Fact]
    public async Task LargeFutureClockLeadStillFailsAndNeverBecomesScoringEvidence()
    {
        using var http = new HttpClient(new Handler(2000));
        var read = await new BinanceMarketProvider(http,store,new Clock()).ReadAsync(Asset("TEST"),"Mark",default);
        Assert.Equal(CollectionFailure.Invalid,read.Evidence.Failure);
        Assert.Empty(read.Observations);
    }
    public void Dispose()
    {
        foreach (var file in Directory.GetFiles(folder)) File.Delete(file);
        Directory.Delete(folder);
    }
}
