using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.IntegrationTests;

public sealed class TenfoldTests
{
    private static readonly DateTimeOffset Now=new(2026,10,31,0,0,0,TimeSpan.Zero);
    private static MarketView Market(bool trusted=true,decimal cap=50_000_000)=>new(new(1,"TESTUSDT","TEST",Now.AddDays(-100),true),1,10_000_000,0,1,0,Now,Now,Now,0,0,0,
        new(){Capital=new(){TrustedIdentity=trusted,CoinGeckoId="test",MarketCapUsd=new(cap,DataStatus.Valid,Now,"Fixture")}});
    [Theory][InlineData("BTC")][InlineData("ETH")][InlineData("PAXG")][InlineData("USDC")]
    public void BenchmarksAndAnchorsNeverEnterTenfoldPool(string symbol)
    {
        var m=Market() with{Instrument=new(1,symbol+"USDT",symbol,Now.AddYears(-1),true)};
        Assert.Equal(-1,TenfoldEngine.DiscoveryTier(m,Now));
        Assert.DoesNotContain(OpportunityEngine.Select([m],Now),r=>r.Tracks.Contains("Early"));
    }
    [Fact]public void UnverifiedCheapReferenceNeverBecomesConfirmedSmallCap()
    {
        var m=Market(false);Assert.Equal(1,TenfoldEngine.DiscoveryTier(m,Now));
        var p=TenfoldEngine.Evaluate(m,[],Now);Assert.False(p.VerifiedSmallCap);Assert.Null(p.MarketCapUsd);Assert.Equal(50_000_000,p.ReferenceMarketCapUsd);
        Assert.Equal("小市值身份待核验",p.Status);Assert.Null(p.Probability30d10x);Assert.Null(p.ImpliedMarketCap10x);
    }
    [Fact]public void LargeCapAndFutureCapCannotPassSmallCapResearch()
    {
        var m=Market(cap:1_000_000_000);Assert.Equal(-1,TenfoldEngine.DiscoveryTier(m,Now));Assert.Equal("市值超范围",TenfoldEngine.Evaluate(m,[],Now).Status);
        m=Market();m=m with{Features=m.Features with{Capital=m.Features.Capital with{MarketCapUsd=new(10_000_000,DataStatus.Valid,Now.AddMinutes(1),"future")}}};
        Assert.False(TenfoldEngine.Evaluate(m,[],Now).VerifiedSmallCap);
    }
    [Fact]public void AConcentratedSnapshotAloneIsNotAccumulationOrAForecast()
    {
        var m=Market();m=m with{Features=m.Features with{OnChain=new(){TrustedIdentity=true,ConcentrationUsable=true,Chain="bsc",ContractAddress="contract",Provider="same",
            NonCustodialTop10Percent=new(60,DataStatus.Valid,Now,"Fixture")}}};
        var p=TenfoldEngine.Evaluate(m,[],Now);Assert.Equal("月度基线待补",p.Status);Assert.Null(p.Top10Change24hPoints);Assert.Null(p.Probability30d10x);
        Assert.Contains(p.Risks,r=>r.Contains("托管"));Assert.Equal(10,p.TargetPrice10x);Assert.Equal(500_000_000,p.ImpliedMarketCap10x);
        var yesterday=p with{ConcentrationSourceTime=Now.AddDays(-1),AdjustedTop10Percent=55};
        Assert.Equal(5,TenfoldEngine.Evaluate(m,[],Now,yesterday).Top10Change24hPoints);
        Assert.Null(TenfoldEngine.Evaluate(m,[],Now,yesterday with{ContractAddress="another"}).Top10Change24hPoints);
        Assert.Null(TenfoldEngine.Evaluate(m,[],Now,yesterday with{Provider="different"}).Top10Change24hPoints);
        Assert.Null(TenfoldEngine.Evaluate(m,[],Now,yesterday with{ConcentrationSourceTime=Now.AddHours(-2)}).Top10Change24hPoints);
        Assert.Null(TenfoldEngine.Evaluate(m,[],Now,yesterday with{ClassificationFingerprint="changed labels"}).Top10Change24hPoints);
    }
    [Fact]public void NonCustodialTopTenExcludesVerifiedExchangeWithoutChangingLegacyMetric()
    {
        var holders=Enumerable.Range(0,12).Select(i=>new TokenHolder($"address{i}","1",i==0?40:5)).ToArray();
        var labels=holders.Select((h,i)=>new AddressLabel("bsc",h.Address,i==0?WalletLabelType.Exchange:WalletLabelType.Whale,true,"fixture",1,Now)).ToArray();
        var binding=new ChainBinding(1,"bsc","contract","test",true,true,1,Now,"fixture",[]);
        var sample=new OnChainSample(Guid.NewGuid(),"bsc","contract","fixture",Now,Now,"fixture",12,"100",18,holders,12,true);
        var result=new OnChainFeatureEngine().Calculate(binding,sample,labels,null,true,Now);
        Assert.True(result.ConcentrationUsable);Assert.Equal(85,result.AdjustedTop10Percent.Value);Assert.Equal(50,result.NonCustodialTop10Percent.Value);
    }
}
