using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;

namespace PulletCryptoRadar.Tests;

public sealed class ResearchIdentityTests : IDisposable
{
    private static readonly DateTimeOffset Now=new(2026,11,3,15,0,0,TimeSpan.Zero);
    private const string Address="0x1111111111111111111111111111111111111111";
    private readonly string folder=Path.Combine(Path.GetTempPath(),"radar-identity-"+Guid.NewGuid());
    private readonly FoundationStore store;
    private static readonly ResearchIdentitySample Sample=new("TESTUSDT","Test","test-id",new(ChainId.Bsc,Address,18),"https://example.org/official/token");
    public ResearchIdentityTests()
    {
        store=new(folder);store.Initialize(Now);
        store.Register(new(BinanceIdentity.AssetId("TESTUSDT"),"TEST","TEST","TESTUSDT",null,null,[],Now));
    }
    private static ProviderRead Read(string address=Address,int decimals=18,string id="test-id") => new(
        new(Guid.NewGuid(),"CoinGecko","TESTUSDT","https://api.coingecko.com/api/v3/coins/test-id",Now,200,CollectionFailure.None,
        System.Text.Json.JsonSerializer.Serialize(new {id,symbol="test",platforms=new Dictionary<string,string>{{"binance-smart-chain",address}},
            detail_platforms=new Dictionary<string,object>{{"binance-smart-chain",new {contract_address=address,decimal_place=decimals}}}})),[]);
    [Fact]
    public void VerifiedEnrichmentPreservesAccountAndPersistsProofAndOriginalEvidence()
    {
        var account=store.Portfolio();var read=Read();
        var proof=store.ApplyResearchIdentity(Sample,read,Now);
        var restarted=new FoundationStore(folder);restarted.Initialize(Now.AddHours(1));
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(account),System.Text.Json.JsonSerializer.Serialize(restarted.Portfolio()));
        Assert.Equal("test-id",Assert.Single(restarted.Assets()).CoinGeckoId);
        Assert.Empty(proof.Before.Chains);Assert.Equal(Address,Assert.Single(proof.After.Chains).Address);
        Assert.Equal(read.Evidence.Id,Assert.Single(restarted.ResearchProofs()).EvidenceId);
        Assert.Throws<InvalidOperationException>(()=>restarted.Register(proof.Before));
    }
    [Theory]
    [InlineData("0x2222222222222222222222222222222222222222",18,"test-id")]
    [InlineData(Address,6,"test-id")]
    [InlineData(Address,18,"same-symbol-impostor")]
    public void WrongAddressDecimalsOrProviderIdNeverUpdatesIdentity(string address,int decimals,string id)
    {
        Assert.Throws<InvalidDataException>(()=>store.ApplyResearchIdentity(Sample,Read(address,decimals,id),Now));
        Assert.Empty(Assert.Single(store.Assets()).Chains);Assert.Empty(store.ResearchProofs());
    }
    [Fact]
    public void ExistingProviderMappingIsNotSilentlyReplaced()
    {
        store.ApplyResearchIdentity(Sample,Read(),Now);
        var different=Sample with {CoinGeckoId="different"};
        Assert.Throws<InvalidDataException>(()=>store.ApplyResearchIdentity(different,Read(id:"different"),Now));
        Assert.Equal("test-id",Assert.Single(store.Assets()).CoinGeckoId);Assert.Single(store.ResearchProofs());
    }
    [Fact]
    public void ContractCollisionRollsBackIdentityAmendment()
    {
        store.Register(new(Guid.NewGuid(),"OTHER","Other","OTHERUSDT",null,null,[new(ChainId.Bsc,Address,18)],Now));
        Assert.Throws<InvalidDataException>(()=>store.ApplyResearchIdentity(Sample,Read(),Now));
        Assert.Null(store.Assets().Single(asset=>asset.BinanceSymbol=="TESTUSDT").CoinGeckoId);
        Assert.Empty(store.ResearchProofs());
    }
    [Fact]
    public void UnreviewedMultipleChainsCannotSelectTheFirstNetworkImplicitly()
    {
        var asset=store.Assets()[0] with {Chains=[new(ChainId.Bsc,Address,18),new(ChainId.Ethereum,Address,18)]};
        Assert.Null(ResearchNetworks.Single(asset));
        Assert.Equal("eth",ResearchNetworks.Insight(ChainId.Ethereum));
        Assert.Equal("sol",ResearchNetworks.Insight(ChainId.Solana));
        Assert.Equal("solana",ResearchNetworks.Dex(ChainId.Solana));
        Assert.False(ResearchNetworks.SameAddress(ChainId.Solana,"Abc","abc"));
        Assert.True(ResearchNetworks.SameAddress(ChainId.Ethereum,"0xAbC","0xabc"));
    }
    [Fact]
    public void SolanaDexPoolRequiresExactMintAndKeepsUnverifiedOriginalTime()
    {
        const string mint="EKpQGSJtjMFqKZ9KQanSqYXRcF8fBopzLHYxdM65zcjm";
        var asset=store.Assets()[0] with {Chains=[new(ChainId.Solana,mint,6)]};
        var body="""[{"chainId":"solana","pairAddress":"EP2ib6dYdEeqD8MfE2ezHCxX3kP3K2eLKkirfPm5eyMx","dexId":"raydium","baseToken":{"address":"__MINT__"},"quoteToken":{"address":"So11111111111111111111111111111111111111112"},"liquidity":{"usd":1000000},"priceUsd":"0.2"}]""".Replace("__MINT__",asset.Chains[0].Address).Replace("__ADDRESS__",Address);
        var result=SupplementaryProvider.Parse(asset,"Dex",body,Now,Now,Guid.NewGuid());
        var observation=Assert.Single(result.Observations);
        Assert.Equal(ChainId.Solana,observation.Chain);Assert.Equal(mint,observation.Contract);
        Assert.Equal(1000000,observation.Value);Assert.Equal(TimeQuality.HttpDate,observation.TimeQuality);
        Assert.Empty(SupplementaryProvider.Parse(asset,"Dex",body.Replace("solana","bsc"),Now,Now,Guid.NewGuid()).Pools??[]);
    }
    [Fact]
    public void UnsupportedPoolIdDoesNotDiscardValidAddressPoolAndFutureHttpDateDoesNotBecomeDataTime()
    {
        var asset=store.Assets()[0] with {Chains=[new(ChainId.Ethereum,Address,18)]};
        var body="""[{"chainId":"ethereum","pairAddress":"0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","dexId":"uniswap","baseToken":{"address":"__ADDRESS__"},"quoteToken":{"address":"0x0000000000000000000000000000000000000000"}},{"chainId":"ethereum","pairAddress":"0x2222222222222222222222222222222222222222","dexId":"uniswap","baseToken":{"address":"__ADDRESS__"},"quoteToken":{"address":"0xc02aaa39b223fe8d0a0e5c4f27ead9083c756cc2"},"liquidity":{"usd":5000},"priceUsd":"16"}]""".Replace("__MINT__",asset.Chains[0].Address).Replace("__ADDRESS__",Address);
        var result=SupplementaryProvider.Parse(asset,"Dex",body,Now,Now.AddSeconds(1),Guid.NewGuid());
        Assert.Single(result.Pools!);var metric=Assert.Single(result.Observations);
        Assert.Equal(5000,metric.Value);Assert.Null(metric.SourceTime);Assert.Equal(TimeQuality.ReceivedOnly,metric.TimeQuality);
    }
    public void Dispose(){foreach(var file in Directory.GetFiles(folder))File.Delete(file);Directory.Delete(folder);}
}
