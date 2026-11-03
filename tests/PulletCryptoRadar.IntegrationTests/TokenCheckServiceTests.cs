using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;

namespace PulletCryptoRadar.IntegrationTests;
public sealed class TokenCheckServiceTests
{
    private static readonly DateTimeOffset Now=new(2026,10,31,1,30,0,TimeSpan.Zero);
    private const string Contract="0x1111111111111111111111111111111111111111";
    private const string Other="0x2222222222222222222222222222222222222222";
    private static MarketCoin Coin=>new("real-token","aio","OlaXBT fixture",new Dictionary<string,string>{{"binance-smart-chain",Contract}});
    private sealed class Clock:TimeProvider{public override DateTimeOffset GetUtcNow()=>Now;}
    private sealed class Reports:ITokenCheckRepository
    {public List<TokenCheckReport> Rows {get;}=[];public void Save(TokenCheckReport r)=>Rows.Add(r);public TokenCheckReport? Get(Guid id)=>Rows.FirstOrDefault(r=>r.Id==id);public IReadOnlyList<TokenCheckReport> Recent(int n)=>Rows.OrderByDescending(r=>r.AsOf).Take(n).ToArray();}
    private sealed class Chain:IOnChainRepository,IOnChainPersistence,IOnChainProvider
    {
        public int Reserves,Fetches,Bindings,Labels;public List<OnChainWrite> Writes {get;}=[];public bool Allow;
        public OnChainState Restore()=>new([],[],[],[]);public IReadOnlyList<OnChainSample> History(string chain,string contractAddress,int limit)=>[];
        public Task SaveBindingAsync(ChainBinding binding,long expectedRevision,CancellationToken ct){Bindings++;return Task.CompletedTask;}
        public Task SaveLabelAsync(AddressLabel label,long expectedRevision,CancellationToken ct){Labels++;return Task.CompletedTask;}
        public Task<bool> ReserveAsync(ChainReservation reservation,CancellationToken ct){Reserves++;Assert.Equal(3,reservation.Requests);return Task.FromResult(Allow);}
        public Task SaveSampleAsync(OnChainWrite write,CancellationToken ct){Writes.Add(write);return Task.CompletedTask;}
        public Task<OnChainSample> FetchAsync(ChainBinding binding,CancellationToken ct){Fetches++;throw new OnChainProviderException("HTTP 429 fixture",Now.AddHours(3));}
    }
    private sealed class Data:ITokenCheckProvider,IMarketRestProvider,IPriorityQuoteProvider,IDerivativeRestProvider
    {
        public bool Match;public int Projects,Identities,Quotes,Candles,Oi;
        public Task<TokenProjectSnapshot> ProjectAsync(string coinId,CancellationToken ct){Projects++;return Task.FromResult(new TokenProjectSnapshot(coinId,Now,Now,1,0,1_000_000,10_000_000,1_000_000,1_000_000,10_000_000,Coin.Platforms,[]));}
        public Task<TokenVenueIdentity> IdentityAsync(string symbol,CancellationToken ct){Identities++;return Task.FromResult(new TokenVenueIdentity(symbol,"Fixture",Now,Now,false,new Dictionary<string,string>{{"binance-smart-chain",Match?Contract:Other}},"https://gate.example/"));}
        public Task<IReadOnlyList<MarketEvent>> GetPriorityQuotesAsync(string symbol,CancellationToken ct){Quotes++;return Task.FromResult<IReadOnlyList<MarketEvent>>([new TickerEvent(symbol,1,10_000_000,0,Now,Now)]);}
        public Task<IReadOnlyList<Candle>> GetCandlesAsync(string symbol,int intervalMinutes,int limit,DateTimeOffset? endTime,CancellationToken ct){Candles++;return Task.FromResult<IReadOnlyList<Candle>>([]);}
        public Task<IReadOnlyList<DerivativeSample>> GetSeriesAsync(string symbol,DerivativeKind kind,int limit,CancellationToken ct){Oi++;return Task.FromResult<IReadOnlyList<DerivativeSample>>([]);}
        public Task<DerivativeSample> GetOpenInterestAsync(string symbol,CancellationToken ct)=>throw new NotSupportedException();
        public Task<IReadOnlyList<SymbolDefinition>> GetSymbolsAsync(CancellationToken ct)=>throw new NotSupportedException();
        public Task<IReadOnlyList<TickerEvent>> GetTickersAsync(CancellationToken ct)=>throw new NotSupportedException();
    }
    private static (TokenCheckService Service,Data Data,Chain Chain,Reports Reports,CapitalRuntimeStore Capital) Setup(bool match=false,bool reserve=false)
    {
        var market=new MarketRuntimeStore();market.Register([new(1,"AIOUSDT","AIO",Now,true)]);
        var capital=new CapitalRuntimeStore();capital.SetCatalog(new(Now,[Coin,new("other-aio","aio","Unrelated AIO",new Dictionary<string,string>{{"ethereum",Other}})]));
        var chain=new Chain{Allow=reserve};var data=new Data{Match=match};var reports=new Reports();
        var service=new OnChainService(chain,chain,market,capital,new(),new Clock());
        return(new(capital,market,data,data,data,data,chain,service,chain,new(),reports,new Clock()),data,chain,reports,capital);
    }
    [Fact]public void ExactSymbolSearchKeepsAllSameNamedCandidatesAndDoesNotCorrectAoiToAio()
    {
        var s=Setup();Assert.Equal(2,s.Service.Search("AIO").Count);Assert.Empty(s.Service.Search("AOI"));
        Assert.Equal("real-token",Assert.Single(s.Service.Search(Contract)).Coin.Id);
    }
    [Fact]public async Task WrongContractStopsBeforeAllNetworkAccess()
    {
        var s=Setup();await Assert.ThrowsAsync<ArgumentException>(()=>s.Service.CheckAsync(new(Coin.Id,"binance-smart-chain",Other,1),default));
        await Assert.ThrowsAsync<ArgumentException>(()=>s.Service.CheckAsync(new(Coin.Id,"binance-smart-chain",null!,1),default));
        Assert.Equal(0,s.Data.Projects);Assert.Empty(s.Reports.Rows);Assert.Equal(0,s.Chain.Reserves);
    }
    [Fact]public async Task SameSymbolWithoutIndependentContractMatchNeverFetchesOrCombinesMarketSeries()
    {
        var s=Setup();var r=await s.Service.CheckAsync(new(Coin.Id,"binance-smart-chain",Contract,1),default);
        Assert.False(r.IdentityVerified);Assert.Equal("资料不足",r.Verdict);Assert.Equal(0,s.Data.Quotes);Assert.Equal(0,s.Data.Candles);Assert.Equal(0,s.Chain.Reserves);
        Assert.Null(s.Capital.GetMapping(1));Assert.Equal(0,s.Chain.Bindings);Assert.Equal(0,s.Chain.Labels);
    }
    [Fact]public async Task DuplicateRequestsReuseOriginalReportAndSharedChainCooldownCannotBeBypassed()
    {
        var s=Setup(match:true);var request=new TokenCheckRequest(Coin.Id,"binance-smart-chain",Contract,1);
        var first=await s.Service.CheckAsync(request,default);var second=await s.Service.CheckAsync(request,default);
        Assert.Equal(first.Id,second.Id);Assert.Equal(first.AsOf,second.AsOf);Assert.Single(s.Reports.Rows);Assert.Equal(1,s.Data.Projects);
        Assert.Equal(1,s.Chain.Reserves);Assert.Equal(0,s.Chain.Fetches);Assert.Contains(first.Missing,m=>m.Contains("冷却"));
        Assert.Null(s.Capital.GetMapping(1));Assert.Equal(0,s.Chain.Bindings);Assert.Equal(0,s.Chain.Labels);
    }
    [Fact]public async Task SupplementalRateLimitKeepsLongerRetryAfterWhenRecordingFailure()
    {
        var s=Setup(match:true,reserve:true);await s.Service.CheckAsync(new(Coin.Id,"binance-smart-chain",Contract,1),default);
        var write=Assert.Single(s.Chain.Writes);Assert.Null(write.Sample);Assert.Equal(Now.AddHours(3),write.Attempt.NextAttempt);
        Assert.Equal(0,s.Chain.Bindings);Assert.Equal(0,s.Chain.Labels);
    }
}
