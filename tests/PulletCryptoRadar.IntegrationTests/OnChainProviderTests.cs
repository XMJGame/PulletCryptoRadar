using System.Globalization;
using System.Numerics;
using System.Text.Json;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Providers.OnChain;

namespace PulletCryptoRadar.IntegrationTests;

public sealed class OnChainProviderTests
{
    private static readonly DateTimeOffset Now=new(2026,10,29,8,0,0,TimeSpan.Zero);
    private const string Contract="0x1111111111111111111111111111111111111111";
    private static ChainBinding Binding=>new(1,"bsc",Contract,"coin",true,true,1,Now,"Fixture",[]);
    private static JsonElement J(string text)=>JsonDocument.Parse(text).RootElement.Clone();
    private static JsonElement Info(string supply)=>J(JsonSerializer.Serialize(new{address=Contract,totalSupply=supply,decimals="18",holdersCount=1000,lastUpdated=Now.ToUnixTimeSeconds()}));
    [Fact] public void Uint256BalancesNeverPassThroughDoubleOrDecimalOverflow()
    {
        var supply=BigInteger.One<<200;var half=supply/2;
        var sample=OnChainParser.Explorer(Binding,Info(supply.ToString(CultureInfo.InvariantCulture)),J(JsonSerializer.Serialize(new{holders=new[]{new{address="0x2222222222222222222222222222222222222222",rawBalance=half.ToString(CultureInfo.InvariantCulture),share=99.99}}})),Now,Now);
        Assert.Equal(50,Assert.Single(sample.Holders).Percent);Assert.Equal(half.ToString(CultureInfo.InvariantCulture),sample.Holders[0].RawBalance);
    }
    [Theory] [InlineData("duplicate")] [InlineData("unordered")] [InlineData("oversupply")]
    public void InvalidRankedResponsesAreRejected(string kind)
    {
        var first="0x2222222222222222222222222222222222222222";var second=kind=="duplicate"?first:"0x3333333333333333333333333333333333333333";
        var rows=new[]{new{address=first,rawBalance=kind=="oversupply"?"1000":"10"},new{address=second,rawBalance=kind=="unordered"?"20":"1"}};
        Assert.Throws<InvalidDataException>(()=>OnChainParser.Explorer(Binding,Info("100"),J(JsonSerializer.Serialize(new{holders=rows})),Now,Now));
    }
    [Fact] public void MismatchedAddressOrFutureIndexTimestampIsRejected()
    {
        Assert.Throws<InvalidDataException>(()=>OnChainParser.Explorer(Binding,Info("100"),J("{\"holders\":[]}"),Now.AddMinutes(1),Now));
        Assert.Throws<InvalidDataException>(()=>OnChainParser.Explorer(Binding with{ContractAddress="0x2222222222222222222222222222222222222222"},Info("100"),J("{\"holders\":[]}"),Now,Now));
    }
    [Fact] public void GoPlusFractionsAreConvertedToPercentAndRetainTop10Coverage()
    {
        var sample=OnChainParser.GoPlus(Binding,J("""{"code":1,"result":{"0x1111111111111111111111111111111111111111":{"holder_count":"1000","holders":[{"address":"0x2222222222222222222222222222222222222222","percent":"0.3","tag":"Unverified LP hint"}]}}}"""),Now,Now);
        Assert.Equal(30,Assert.Single(sample.Holders).Percent);Assert.Equal(10,sample.RequestedCount);Assert.Equal("GoPlus",sample.Provider);
    }
    [Fact] public async Task RateLimitDeadlineIsReturnedWithoutRepeatingRequests()
    {
        var handler=new Handler();var provider=new OnChainProvider(new HttpClient(handler),TimeProvider.System);
        var error=await Assert.ThrowsAsync<OnChainProviderException>(()=>provider.FetchAsync(Binding,default));Assert.True(error.RetryAt>DateTimeOffset.UtcNow.AddMinutes(4));Assert.Equal(1,handler.Calls);
    }
    private sealed class Handler:HttpMessageHandler
    {public int Calls;protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){Calls++;return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests));}}
}
