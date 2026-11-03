using System.Text.Json;
using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Providers.OnChain;

namespace PulletCryptoRadar.IntegrationTests;
public sealed class SolanaOnChainTests
{
    private const string Mint="DvjbEsdca43oQcw2h3HW1CT7N3x5vRcr3QrvTUHnXvgV";
    private const string Owner="Dtg461zxsxWukZbKRMPoa4BDXtdcLTaPvSKdbt3Xrk23";
    private const string Account="BLKPZPRM7WzqQzeJ9SdB2pzEKjRTZZQsXgf4hzV6doBS";
    private const string OtherAccount="G7cBwZgSKy5wYMo7jvSpurwX5VizQ72W69Bw1f29r6Hr";
    private static readonly DateTimeOffset Now=new(2026,10,31,1,0,0,TimeSpan.Zero);
    private static ChainBinding Binding=>new(1,"solana",Mint,"doodles",true,true,1,Now,"Fixture",[]);
    private static JsonElement Response(string? mint=null,string secondAccount=OtherAccount)=>JsonSerializer.SerializeToElement(new{code=1,result=new Dictionary<string,object>{[mint??Mint]=new{
        holder_count="1000",mintable=new{status="0"},freezable=new{status="1"},metadata_mutable=new{status=""},trusted_token=1,
        metadata=new{name="Identical names prove nothing",symbol="DOOD"},holders=new[]{
            new{account=Owner,token_account=Account,percent="0.30",is_locked=0,tag="Unverified exchange"},
            new{account=Owner,token_account=secondAccount,percent="0.20",is_locked=1,tag=""}}}}});
    [Theory][InlineData(Mint)][InlineData(Owner)][InlineData(Account)][InlineData("11111111111111111111111111111111")]
    public void Base58PublicKeysMustDecodeToExactlyThirtyTwoBytes(string value)=>Assert.True(ChainIdentity.Address("solana",value));
    [Theory][InlineData("0x1111111111111111111111111111111111111111")][InlineData("111111111111111111111111111111111")][InlineData("O0Il")]
    public void InvalidOrWrongChainAddressesAreRejected(string value)=>Assert.False(ChainIdentity.Address("solana",value));
    [Fact]public void MintIdentityAndScheduleRemainCaseSensitive()
    {
        Assert.Equal(Mint,ChainIdentity.Normalize("solana",Mint));Assert.False(ChainIdentity.Same("solana",Mint,Mint.ToLowerInvariant()));
        Assert.True(ChainIdentity.Same("bsc","0xAbC","0xabc"));
        Assert.Throws<InvalidDataException>(()=>OnChainParser.Solana(Binding,Response(Mint.ToLowerInvariant()),Now,Now));
        var attempt=new ChainAttempt("solana",Mint.ToLowerInvariant(),Now,Now.AddHours(6),null);
        Assert.Single(OnChainService.DueBatch([Binding],[attempt],new HashSet<int>(),Now,5));
    }
    [Fact]public void AccountAndOwnerArePreservedAndLocalGroupingNeverBecomesGlobalOwnerTopTen()
    {
        var sample=OnChainParser.Solana(Binding,Response(),Now,Now);Assert.Equal("GoPlus Solana",sample.Provider);
        Assert.Equal(Mint,sample.ContractAddress);Assert.False(sample.OrderedTopHolders);
        var row=Assert.Single(sample.Holders);Assert.Equal(Owner,row.Address);Assert.Equal(50,row.Percent);Assert.Equal("",row.RawBalance);
        Assert.Equal(2,sample.Security!.Holders.Count);Assert.Equal(Account,sample.Security.Holders[0].TokenAccount);
        Assert.False(sample.Security.Flags["mintable"]);Assert.True(sample.Security.Flags["freezable"]);Assert.Null(sample.Security.Flags["metadata_mutable"]);
        Assert.False(sample.Security.Flags.ContainsKey("trusted_token"));
        var features=new OnChainFeatureEngine().Calculate(Binding,sample,[],null,true,Now);
        Assert.Equal(1000,features.HolderCount.Value);Assert.Null(features.RawTopPercent[10].Value);
        Assert.False(features.ConcentrationUsable);Assert.Equal(0,features.ClassificationCoveragePercent);
    }
    [Fact]public void DuplicateTokenAccountsAndStaleOrFutureObservationsAreRejected()
    {
        Assert.Throws<InvalidDataException>(()=>OnChainParser.Solana(Binding,Response(secondAccount:Account),Now,Now));
        Assert.Throws<InvalidDataException>(()=>OnChainParser.Solana(Binding,Response(),Now.AddMinutes(-46),Now));
        Assert.Throws<InvalidDataException>(()=>OnChainParser.Solana(Binding,Response(),Now.AddMinutes(1),Now));
    }
    [Fact]public void MissingOptionalTokenAccountStaysUnknownAndCannotDoubleCountAnOwnerAggregate()
    {
        var sample=OnChainParser.Solana(Binding,Response(secondAccount:""),Now,Now);
        Assert.Null(sample.Security!.Holders[1].TokenAccount);Assert.Equal(30,Assert.Single(sample.Holders).Percent);
        Assert.False(sample.OrderedTopHolders);
    }
    [Fact]public void WrongCaseMintAndAddressLabelCannotContributeToFeatures()
    {
        var sample=OnChainParser.Solana(Binding,Response(),Now,Now);
        var label=new AddressLabel("solana",Owner.ToLowerInvariant(),WalletLabelType.Exchange,true,"https://source.example/",1,Now);
        var feature=new OnChainFeatureEngine().Calculate(Binding,sample,[label],null,true,Now);
        Assert.Equal(0,feature.ClassificationCoveragePercent);
        Assert.Null(new OnChainFeatureEngine().Calculate(Binding,sample with{ContractAddress=Mint.ToLowerInvariant()},[],null,true,Now).HolderCount.Value);
    }
    [Fact]public void CopiedSymbolAndDifferentMintRemainAmbiguousUntilExplicitIdentityCheck()
    {
        var instrument=new Instrument(1,"DOODUSDT","DOOD",Now,true);
        var catalog=new CoinCatalog(Now,[new("doodles","dood","Doodles",new Dictionary<string,string>{{"solana",Mint}}),
            new("fake-doodles","dood","Doodles",new Dictionary<string,string>{{"solana",OtherAccount}})]);
        var engine=new AssetMappingEngine();var suggested=engine.Suggest(instrument,catalog,null,Now);
        Assert.Equal(MappingStatus.Ambiguous,suggested.Status);Assert.Null(suggested.CoinGeckoId);
        Assert.Throws<ArgumentException>(()=>engine.Manual(instrument,catalog,suggested,new("doodles",1,true,"solana",OtherAccount),Now));
        Assert.Throws<ArgumentException>(()=>engine.Manual(instrument,catalog,suggested,new("doodles",1,true,"solana",Mint.ToLowerInvariant()),Now));
    }
    [Fact]public async Task ProviderUsesSolanaEndpointWithoutEvmChainIdOrLowercasingMint()
    {
        var handler=new SolanaHandler();var sample=await new OnChainProvider(new HttpClient(handler),TimeProvider.System).FetchAsync(Binding,default);
        Assert.Single(handler.Urls);Assert.Contains("/solana/token_security?",handler.Urls[0]);Assert.EndsWith(Mint,handler.Urls[0]);Assert.Equal(Mint,sample.ContractAddress);
    }
    private sealed class SolanaHandler:HttpMessageHandler
    {
        public List<string> Urls {get;}=[];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {Urls.Add(request.RequestUri!.AbsoluteUri);var r=new HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new StringContent(Response().GetRawText())};r.Headers.Date=DateTimeOffset.UtcNow.AddSeconds(-1);return Task.FromResult(r);}
    }
}
