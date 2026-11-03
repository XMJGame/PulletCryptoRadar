using System.Globalization;
using System.Numerics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;
using PulletCryptoRadar.Providers.OnChain;

namespace PulletCryptoRadar.IntegrationTests;
public sealed class TokenMonitorTests
{
    private static readonly DateTimeOffset Now=DateTimeOffset.Parse("2026-10-31T02:30:00Z");
    private static string A(int n)=>"0x"+n.ToString("x40");private static string H(int n)=>"0x"+n.ToString("x64");
    private static string Word(BigInteger n)=>n.ToString("x",CultureInfo.InvariantCulture).PadLeft(64,'0');
    private static TokenMonitorTarget Target=>new("aio-bsc","olaxbt","AIO","binance-smart-chain",A(100),A(200),Convert.ToHexStringLower(SHA256.HashData(new byte[]{0x60,0x00})),"https://source.example/","Fixture code review");
    private sealed class Clock:TimeProvider
    {public override DateTimeOffset GetUtcNow()=>Now;public override ITimer CreateTimer(TimerCallback callback,object? state,TimeSpan dueTime,TimeSpan period)=>System.CreateTimer(callback,state,TimeSpan.Zero,Timeout.InfiniteTimeSpan);}
    private static TokenMonitorSnapshot Sample()=>new(Guid.NewGuid(),Target.Id,Target.ContractAddress,100,H(100),Now,Now,0,"1000",[new(A(1),"100"),new(A(200),"200")],
        new(A(200),Target.VestingRuntimeSha256!,A(100),A(99),true,true,"200",[Allocation()],"https://source.example/","Fixture"),new(1,101,H(100),Now,Now,"same"),[],30);
    private static VestingAllocation Allocation()=>new(0,"投资者","100","10",A(30),30*86400,Now.AddDays(1),"20");
    private static TokenMonitorState State(TokenMonitorSnapshot? snapshot=null)=>new(Target.Id,new(Now,Now,Now.AddDays(7),"https://evidence.example/","Fixture"),false,Now.AddHours(6),Now.AddMinutes(30),Now.AddHours(6),null,null,false,snapshot??Sample());
    private static TokenMonitorView View(TokenMonitorSnapshot latest,IReadOnlyList<TokenMonitorSnapshot>? history=null)=>TokenMonitorEngine.View(Target,State(latest),history??[],[],[],Now);
    [Fact]public void ExactRawUnitsAndPercentageDoNotUseFloatingPointOrOverflow()
    {
        Assert.Equal("1.000000000000000001",TokenMonitorEngine.Amount(BigInteger.Parse("1000000000000000001",CultureInfo.InvariantCulture),18));
        Assert.Equal("-0.000000000000000001",TokenMonitorEngine.Amount(-1,18));Assert.Equal(10,TokenMonitorEngine.Percent(100,1000));
        Assert.Null(TokenMonitorEngine.Percent(1001,1000));Assert.Throws<InvalidDataException>(()=>MonitorAbi.UInt("0x01"));
        Assert.Throws<InvalidDataException>(()=>MonitorAbi.Bool("0x"+Word(2)));Assert.Throws<InvalidDataException>(()=>MonitorAbi.Address("0x"+Word(BigInteger.One<<200)));
    }
    [Fact]public void OverdueReleaseDoesNotInventCatchupBatchesAndZeroPeriodIsCapBounded()
    {
        var a=Allocation() with{NextRelease=Now.AddDays(-100)};Assert.Equal(new BigInteger(20),TokenMonitorEngine.Permitted7Days(a,Now));
        Assert.Equal(new BigInteger(80),TokenMonitorEngine.Permitted7Days(a with{PeriodSeconds=0},Now));
        Assert.Equal(BigInteger.Zero,TokenMonitorEngine.Permitted7Days(a with{RawReleased="90"},Now));
        Assert.Equal(BigInteger.Zero,TokenMonitorEngine.Permitted7Days(a with{NextRelease=Now.AddDays(8)},Now));
        Assert.Equal(new BigInteger(80),TokenMonitorEngine.Permitted7Days(a with{PeriodSeconds=86400},Now));
    }
    [Fact]public void MissingBaselineChangedScopeAndInactiveIdentityNeverInventCurrentAccumulation()
    {
        var current=Sample();var prior=current with{Id=Guid.NewGuid(),SourceTime=Now.AddHours(-24),ReceivedTime=Now.AddHours(-24),Balances=[new(A(1),"80"),new(A(200),"200")]};
        var view=View(current,[prior]);Assert.Equal("20",view.Balances[0].Change24hAmount);Assert.Equal(2,view.Balances[0].Change24hPercentagePoints);Assert.Equal(30,view.TrackedSupplyPercent);
        Assert.Null(View(current,[prior with{Window=prior.Window with{AddressSetHash="different"}}]).BaselineTime);
        Assert.Null(View(current).Balances[0].Change24hAmount);
        var invalid=TokenMonitorEngine.View(Target,State(current) with{IdentityRevoked=true},[prior],[],[],Now);Assert.False(invalid.Fresh);Assert.Null(invalid.TrackedSupplyPercent);
    }
    [Fact]public void UnreleasedTgeOrNoSnapshotDoesNotPublishAZeroUnlockCalendar()
    {
        var current=Sample();Assert.Null(View(current with{Vesting=current.Vesting! with{TgeReleased=false}}).EarliestPermitted7Days);
        var empty=TokenMonitorEngine.View(Target,State() with{Latest=null},[],[],[],Now);Assert.Empty(empty.Releases);Assert.Null(empty.EarliestPermitted7Days);
        Assert.Null(empty.ObservedTransfers);Assert.Null(empty.ConfirmedExchangeInflows);
        Assert.Contains(View(current).Caveats,c=>c.Contains("不是确定解锁日历"));Assert.Contains("不足24h",View(current).TransferCoverage);
    }
    [Fact]public void ResearchFindingIsFrozenAlongsideRawEvidenceAndKeepsItsVersion()
    {
        var snapshot=Sample();var frozen=TokenMonitorEngine.FreezeFinding(snapshot,View(snapshot),Now);
        Assert.Null(snapshot.Finding);Assert.Equal("Token-Monitor-1",frozen.FindingVersion);Assert.Contains("基线不足",frozen.Finding);
        Assert.Equal(snapshot.Id,frozen.Id);Assert.Equal(snapshot.BlockHash,frozen.BlockHash);Assert.Equal(Now,frozen.AnalyzedAt);
        Assert.Equal(frozen.Finding,View(frozen).Finding);
    }
    [Fact]public void HistoricalCandidateExchangeTransfersStaySeparateFromConfirmedClassification()
    {
        var current=Sample();var transfer=new TokenTransfer("bsc",Target.ContractAddress,100,H(100),H(10),0,Now,A(1),A(2),"10","Fixture",Now);
        current=current with{Transfers=[transfer,transfer]};
        var hint=new TokenResearchClaim(Target.CoinGeckoId,Target.Platform,Target.ContractAddress,"AddressHint","Gate hint","historical","https://source.example/","ExplorerHistoricalRecord",null,Now,Now.AddDays(1),Address:A(2),RoleHint:"疑似交易所");
        var view=TokenMonitorEngine.View(Target,State(current),[current],[],[hint],Now);Assert.Equal(1,view.ObservedTransfers);Assert.Equal(1,view.CandidateExchangeInflows);Assert.Equal(0,view.ConfirmedExchangeInflows);
        var label=new AddressLabel("bsc",A(2),WalletLabelType.Exchange,true,"https://source.example/",1,Now);
        view=TokenMonitorEngine.View(Target,State(current),[current],[label],[hint],Now);Assert.Equal(1,view.ConfirmedExchangeInflows);Assert.Equal(0,view.CandidateExchangeInflows);
    }
    [Fact]public void AtomicResearchHistoryAndConservativeBudgetSurviveRestartAndCorruptionFailsClosed()
    {
        var root=Path.Combine(Path.GetTempPath(),"PulletMonitor-"+Guid.NewGuid());Directory.CreateDirectory(root);var manifest=Path.Combine(root,"targets.json");File.WriteAllText(manifest,JsonSerializer.Serialize(new[]{Target}));
        try
        {
            var repo=new TokenMonitorRepository(root,manifest);var s=Sample();repo.Save(State(s),s);
            for(var i=0;i<4;i++)Assert.True(repo.Reserve(Now,64));Assert.False(repo.Reserve(Now,1));
            var restored=new TokenMonitorRepository(root,manifest);Assert.Equal(s.Id,restored.State(Target.Id)!.Latest!.Id);Assert.Single(restored.History(Target.Id,400));Assert.Equal(256,restored.Budget(Now).ReservedHour);
            Assert.False(restored.Reserve(Now,64));Assert.True(restored.Reserve(Now.AddHours(1),64));Assert.Throws<InvalidOperationException>(()=>repo.Save(State(s),s));
            File.WriteAllText(Path.Combine(root,"TokenMonitoring",Target.Id,"state.json"),"broken");Assert.Throws<JsonException>(()=>repo.State(Target.Id));
        }
        finally{Directory.Delete(root,true);}
    }
    private sealed class Handler:HttpMessageHandler
    {
        public bool WrongChain,WrongToken,ChangedCode,WrongId;public readonly List<string> Methods=[];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            using var doc=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));var root=doc.RootElement;var method=root.GetProperty("method").GetString()!;Methods.Add(method);var p=root.GetProperty("params");object result;
            if(method=="eth_chainId")result=WrongChain?"0x1":"0x38";
            else if(method=="eth_blockNumber")result="0xa4";
            else if(method=="eth_getBlockByNumber") {var n=TransferLogParser.Number(p[0].GetString()!);result=new{number="0x"+n.ToString("x"),hash=H((int)n),timestamp="0x"+(Now.ToUnixTimeSeconds()-1).ToString("x")};}
            else if(method=="eth_getCode")result=ChangedCode?"0x6001":"0x6000";
            else if(method=="eth_getLogs")result=new[]{new{address=Target.ContractAddress,topics=new[]{TransferLogParser.Topic,"0x"+A(1)[2..].PadLeft(64,'0'),"0x"+A(2)[2..].PadLeft(64,'0')},transactionHash=H(10),blockHash=H(100),blockNumber="0x64",logIndex="0x0",data="0x"+Word(10),removed=false}};
            else if(method=="eth_call")
            {
                var data=p[0].GetProperty("data").GetString()!;var selector=data[..10];
                result=selector switch{"0x313ce567"=>"0x"+Word(0),"0x18160ddd"=>"0x"+Word(1000),"0x70a08231"=>"0x"+Word(100),"0xfc0c546a"=>"0x"+A(WrongToken?101:100)[2..].PadLeft(64,'0'),"0xf851a440"=>"0x"+A(99)[2..].PadLeft(64,'0'),"0x40624824" or "0xdf90b6fb"=>"0x"+Word(1),
                    "0x51d1eb8b"=>"0x"+Word(100)+Word(10)+A(30)[2..].PadLeft(64,'0')+Word(30*86400)+Word(Now.AddDays(1).ToUnixTimeSeconds())+Word(20),_=>throw new InvalidOperationException("Non-whitelisted read")};
                Assert.Equal("0x64",p[1].GetString());
            }
            else throw new InvalidOperationException("Unexpected RPC method");
            return new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new{jsonrpc="2.0",id=WrongId?-1:root.GetProperty("id").GetInt32(),result}),Encoding.UTF8,"application/json")};
        }
    }
    [Fact]public async Task SameConfirmedBlockBindsBalanceCodeTokenVestingAndDeduplicatedFilteredTransfers()
    {
        var handler=new Handler();using var http=new HttpClient(handler){BaseAddress=new Uri("https://fixture.example/")};
        var result=await new TokenMonitorProvider(http,new Clock()).FetchAsync(Target,[A(1),A(200)],null,default);
        Assert.Equal(100,result.BlockNumber);Assert.Equal(Target.ContractAddress,result.Vesting!.Token);Assert.Equal(6,result.Vesting.Allocations.Count);Assert.Single(result.Transfers);Assert.Equal(101,result.Window.NextBlock);
        Assert.InRange(result.RpcCalls,1,64);Assert.DoesNotContain(handler.Methods,m=>m.Contains("send",StringComparison.OrdinalIgnoreCase));
    }
    [Theory][InlineData("chain")][InlineData("token")][InlineData("code")][InlineData("id")]
    public async Task WrongChainTokenCodeOrResponseIdNeverCommitsAnObservation(string kind)
    {
        var handler=new Handler{WrongChain=kind=="chain",WrongToken=kind=="token",ChangedCode=kind=="code",WrongId=kind=="id"};using var http=new HttpClient(handler){BaseAddress=new Uri("https://fixture.example/")};
        await Assert.ThrowsAsync<InvalidDataException>(()=>new TokenMonitorProvider(http,new Clock()).FetchAsync(Target,[A(1)],null,default));Assert.DoesNotContain("eth_getLogs",handler.Methods);
    }
    [Fact]public async Task ChangedCursorAnchorHaltsBeforeLogsAndPreservesPriorCheckpoint()
    {
        var previous=Sample() with{Window=Sample().Window with{EndBlockHash=H(99)}};var handler=new Handler();using var http=new HttpClient(handler){BaseAddress=new Uri("https://fixture.example/")};
        await Assert.ThrowsAsync<FlowReorgException>(()=>new TokenMonitorProvider(http,new Clock()).FetchAsync(Target,[A(1)],previous,default));Assert.DoesNotContain("eth_getLogs",handler.Methods);Assert.Equal(H(99),previous.Window.EndBlockHash);
    }
    private static TokenCheckReport Proof(bool verified=true)
    {
        var coin=new MarketCoin(Target.CoinGeckoId,"aio","OlaXBT",new Dictionary<string,string>{{Target.Platform,Target.ContractAddress}});
        return TokenCheckEngine.Build(coin,new(coin.Id,Target.Platform,Target.ContractAddress,1),verified,"Fixture",null,null,null,[],[],null,
            [new("Gate链合约元数据","Observed",Now,Now,"https://identity.example/","Fixture")],[],Now,"AIOUSDT");
    }
    [Fact]public void IdentityPinUsesLatestReportAndNeverCherryPicksAnOlderPositiveOrRenewsOldProof()
    {
        var positive=Proof();Assert.NotNull(TokenMonitorService.ReportIdentity(Target,[positive],Now));
        Assert.Null(TokenMonitorService.ReportIdentity(Target,[positive,Proof(false) with{AsOf=Now.AddMinutes(1)}],Now.AddMinutes(2)));
        Assert.Null(TokenMonitorService.ReportIdentity(Target,[positive],Now.AddDays(2)));
        Assert.Null(TokenMonitorService.ReportIdentity(Target,[positive with{ContractAddress=A(999)}],Now));
    }
    private sealed class Local:ITokenMonitorRepository,ITokenMonitorProvider,ITokenResearchRepository,ITokenCheckRepository,IOnChainRepository,IOnChainPersistence,IOnChainProvider,ITokenCheckProvider
    {
        public TokenMonitorState Current=TokenMonitorTests.State() with{NextRpcAttempt=Now};public bool AllowBudget;public int Fetches,Reservations,Bindings,Labels,IdentityCalls;public bool IdentityFails;
        public IReadOnlyList<TokenMonitorTarget> Targets()=>[Target];public TokenMonitorState? State(string id)=>Current;
        public IReadOnlyList<TokenMonitorSnapshot> History(string id,int limit)=>[];
        public void Save(TokenMonitorState state,TokenMonitorSnapshot? snapshot=null)=>Current=state;
        public bool Reserve(DateTimeOffset now,int requests){Assert.Equal(64,requests);Reservations++;return AllowBudget;}
        public MonitorBudgetView Budget(DateTimeOffset now)=>new(0,0);
        public Task<TokenMonitorSnapshot> FetchAsync(TokenMonitorTarget t,IReadOnlyList<string> addresses,TokenMonitorSnapshot? previous,CancellationToken ct){Fetches++;return Task.FromResult(Sample());}
        public IReadOnlyList<TokenResearchClaim> Claims()=>[];public void Save(TokenCheckReport r){}public TokenCheckReport? Get(Guid id)=>null;public IReadOnlyList<TokenCheckReport> Recent(int n)=>[];
        public OnChainState Restore()=>new([],[],[],[]);
        public IReadOnlyList<OnChainSample> History(string chain,string contract,int limit)=>[new(Guid.NewGuid(),"bsc",Target.ContractAddress,"fixture",Now,Now,"fixture",100,"1000",0,[new(A(1),"100",10)],10,false)];
        public Task SaveBindingAsync(ChainBinding b,long e,CancellationToken ct){Bindings++;return Task.CompletedTask;}
        public Task SaveLabelAsync(AddressLabel l,long e,CancellationToken ct){Labels++;return Task.CompletedTask;}
        public Task<bool> ReserveAsync(ChainReservation r,CancellationToken ct)=>throw new InvalidOperationException("Holder cooldown should be preserved");
        public Task SaveSampleAsync(OnChainWrite w,CancellationToken ct)=>Task.CompletedTask;
        public Task<OnChainSample> FetchAsync(ChainBinding b,CancellationToken ct)=>throw new InvalidOperationException("No unreserved holder fetch");
        public Task<TokenProjectSnapshot> ProjectAsync(string id,CancellationToken ct)=>throw new InvalidOperationException("No market/price collection");
        public Task<TokenVenueIdentity> IdentityAsync(string symbol,CancellationToken ct)
        {IdentityCalls++;if(IdentityFails)throw new InvalidDataException("fixture invalid HTTP source date");return Task.FromResult(new TokenVenueIdentity(symbol,"OlaXBT",Now,Now,false,new Dictionary<string,string>{{Target.Platform,Target.ContractAddress}},"https://identity.example/"));}
    }
    private static TokenMonitorService Service(Local local)
    {
        var market=new MarketRuntimeStore();var capital=new CapitalRuntimeStore();capital.SetCatalog(new(Now,[Proof().Coin]));
        var chain=new OnChainService(local,local,market,capital,new(),new Clock());
        return new(local,local,capital,local,local,local,chain,local,new(),local,new Clock());
    }
    [Fact]public async Task ReservedBudgetAndPersistedDueTimeGateRpcWithoutChangingBindingsOrLabels()
    {
        var denied=new Local();await Service(denied).TickAsync(default);Assert.Equal(1,denied.Reservations);Assert.Equal(0,denied.Fetches);Assert.Equal(0,denied.Bindings);Assert.Equal(0,denied.Labels);
        var allowed=new Local{AllowBudget=true};var service=Service(allowed);await service.TickAsync(default);await service.TickAsync(default);
        Assert.Equal(1,allowed.Fetches);Assert.Equal(1,allowed.Reservations);Assert.Null(allowed.Current.RpcError);Assert.Equal(0,allowed.Bindings);Assert.Equal(0,allowed.Labels);
    }
    [Fact]public async Task UnconfirmedIdentityNeverSpendsRpcBudgetAndKeepsPriorObservation()
    {
        var local=new Local{IdentityFails=true,AllowBudget=true};var old=local.Current.Latest;
        local.Current=local.Current with{Identity=null,NextIdentityAttempt=DateTimeOffset.MinValue};await Service(local).TickAsync(default);
        Assert.Equal(1,local.IdentityCalls);Assert.Equal(0,local.Reservations);Assert.Equal(0,local.Fetches);Assert.Equal(old,local.Current.Latest);Assert.NotNull(local.Current.IdentityError);
    }
}
