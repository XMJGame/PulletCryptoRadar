using System.Globalization;
using System.Numerics;
using System.Text.Json;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Application;
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
    private const string SecurityJson="""{"code":1,"result":{"0x1111111111111111111111111111111111111111":{"holder_count":"900","is_honeypot":"0","is_mintable":"1","is_proxy":"","holders":[{"address":"0x2222222222222222222222222222222222222222","percent":"0.3","tag":"PancakeV3","is_contract":1,"is_locked":0}]}}}""";
    [Fact] public void SecurityClaimsRetainUnknownsAndNeverVerifyAttribution()
    {
        var evidence=OnChainParser.Security(Binding,J(SecurityJson),Now,Now);
        Assert.False(evidence.Flags["is_honeypot"]);Assert.True(evidence.Flags["is_mintable"]);
        Assert.Null(evidence.Flags["is_proxy"]);Assert.Null(evidence.Flags["hidden_owner"]);
        Assert.True(evidence.Holders[0].IsContract);Assert.False(evidence.Holders[0].IsLocked);
        var primary=OnChainParser.Explorer(Binding,Info("100"),J("""{"holders":[{"address":"0x2222222222222222222222222222222222222222","rawBalance":"40"}]}"""),Now,Now) with{Security=evidence};
        var features=new OnChainFeatureEngine().Calculate(Binding,primary,[],null,true,Now);
        Assert.Equal(1000,features.HolderCount.Value);Assert.Equal(0,features.ClassificationCoveragePercent);
        Assert.False(features.ConcentrationUsable);Assert.Empty(features.Labels);Assert.Equal(900,evidence.HolderCount);
    }
    [Theory]
    [InlineData("{\"code\":0,\"result\":{}}")]
    [InlineData("{\"code\":1,\"result\":[]}")]
    [InlineData("{\"code\":1,\"result\":{}}")]
    [InlineData("{\"code\":1,\"result\":{\"0x1111111111111111111111111111111111111111\":{}}}")]
    public void InvalidSecurityResponsesAreRejected(string response)=>Assert.Throws<InvalidDataException>(()=>OnChainParser.Security(Binding,J(response),Now,Now));
    [Fact] public void StaleSecuritySourceIsRejected()=>Assert.Throws<InvalidDataException>(()=>OnChainParser.Security(Binding,J(SecurityJson),Now.AddMinutes(-46),Now));
    [Fact] public async Task SupplementIsThirdRequestWithSeparateSourceAndPrimaryTotals()
    {
        var handler=new SequenceHandler(false);var sample=await new OnChainProvider(new HttpClient(handler),TimeProvider.System).FetchAsync(Binding,default);
        Assert.Equal(3,handler.Urls.Count);Assert.Contains("api.gopluslabs.io",handler.Urls[2]);Assert.Equal("Binplorer",sample.Provider);
        Assert.Equal(1000,sample.HolderCount);Assert.Equal(900,sample.Security!.HolderCount);Assert.Null(sample.Security.Error);
        Assert.True(sample.SourceTime<sample.Security.SourceTime);Assert.Equal(40,sample.Holders[0].Percent);
    }
    [Fact] public async Task SupplementalRateLimitPreservesPrimaryAndDeadlineWithoutRetry()
    {
        var handler=new SequenceHandler(true);var sample=await new OnChainProvider(new HttpClient(handler),TimeProvider.System).FetchAsync(Binding,default);
        Assert.Equal(3,handler.Urls.Count);Assert.Equal(1000,sample.HolderCount);Assert.NotNull(sample.Security!.Error);
        Assert.Empty(sample.Security.Holders);Assert.Null(sample.Security.SourceTime);Assert.True(sample.Security.RetryAt>DateTimeOffset.UtcNow.AddMinutes(89));
    }
    private sealed class SequenceHandler(bool limited):HttpMessageHandler
    {
        public List<string> Urls {get;}=[];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Urls.Add(request.RequestUri!.AbsoluteUri);var now=DateTimeOffset.UtcNow;
            var response=new HttpResponseMessage(Urls.Count==3&&limited?System.Net.HttpStatusCode.TooManyRequests:System.Net.HttpStatusCode.OK);
            response.Headers.Date=now.AddSeconds(-1);response.Headers.RetryAfter=new(TimeSpan.FromMinutes(90));
            response.Content=new StringContent(Urls.Count switch{1=>JsonSerializer.Serialize(new{address=Contract,totalSupply="100",decimals="0",holdersCount=1000,lastUpdated=now.AddMinutes(-5).ToUnixTimeSeconds()}),
                2=>"""{"holders":[{"address":"0x2222222222222222222222222222222222222222","rawBalance":"40"}]}""",_=>SecurityJson});
            return Task.FromResult(response);
        }
    }
}
