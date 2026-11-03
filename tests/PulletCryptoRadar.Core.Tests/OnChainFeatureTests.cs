using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Core.Tests;

public sealed class OnChainFeatureTests
{
    private static readonly DateTimeOffset Now=new(2026,10,29,8,0,0,TimeSpan.Zero);
    private static string Address(int id)=>"0x"+id.ToString("x40");
    private static ChainBinding Binding=>new(1,"bsc",Address(999),"coin",true,true,1,Now,"Fixture",[]);
    private static OnChainSample Sample(int rows=100)=>new(Guid.NewGuid(),"bsc",Address(999),"Binplorer",Now,Now,"fixture",1000,"1000000000000000000000000000",18,
        Enumerable.Range(1,rows).Select(i=>new TokenHolder(Address(i),"1",i<=10?11-i:.1m)).ToArray(),100,true);
    private static AddressLabel Label(int id,WalletLabelType type)=>new("bsc",Address(id),type,true,"https://example.com/evidence",1,Now);
    [Theory][InlineData(WalletLabelType.Custodian,45.1)][InlineData(WalletLabelType.Broker,45.1)][InlineData(WalletLabelType.MarketMaker,55)]
    public void CustodialBusinessRolesAreExcludedButMarketMakingDoesNotImplyCustody(WalletLabelType type,double expected)
    {
        var result=new OnChainFeatureEngine().Calculate(Binding,Sample(),[Label(1,type)],null,true,Now);
        Assert.Equal((decimal)expected,result.NonCustodialTop10Percent.Value);
        Assert.Equal(55,result.RawTopPercent[10].Value);Assert.Equal(0,result.ObservedExchangePercent.Value);
    }
    [Fact] public void AdjustedTop10ReranksAfterExclusionAndKeepsTotalSupplyDenominator()
    {
        var result=new OnChainFeatureEngine().Calculate(Binding,Sample(),[Label(1,WalletLabelType.Burn),Label(2,WalletLabelType.LP),Label(3,WalletLabelType.Contract)],null,true,Now);
        Assert.Equal(55,result.RawTopPercent[10].Value);Assert.Equal(36.2m,result.AdjustedTop10Percent.Value);
        Assert.Equal(64,result.RawTopPercent[100].Value);Assert.False(result.ConcentrationUsable);
    }
    [Fact] public void TruncatedTop10CannotPretendToBeTop25OrAdjustedAfterRemovingAnAddress()
    {
        var result=new OnChainFeatureEngine().Calculate(Binding,Sample(10) with{RequestedCount=10,Provider="GoPlus"},[Label(1,WalletLabelType.Burn)],null,true,Now);
        Assert.Equal(55,result.RawTopPercent[10].Value);Assert.Null(result.RawTopPercent[25].Value);Assert.Null(result.RawTopPercent[100].Value);Assert.Null(result.AdjustedTop10Percent.Value);
    }
    [Fact] public void ClassificationThresholdAndUnknownLabelsControlScoring()
    {
        var labels=Enumerable.Range(1,10).Select(i=>Label(i,WalletLabelType.Exchange)).ToArray();
        var engine=new OnChainFeatureEngine();var good=engine.Calculate(Binding,Sample(),labels,null,true,Now);
        Assert.True(good.ConcentrationUsable);Assert.Equal(55,good.ObservedExchangePercent.Value);Assert.Equal(0,good.ObservedProjectPercent.Value);
        Assert.False(engine.Calculate(Binding,Sample(),labels,null,false,Now).ConcentrationUsable);
        Assert.False(engine.Calculate(Binding,Sample(),labels.Select(l=>l with{Verified=false}).ToArray(),null,true,Now).ConcentrationUsable);
    }
    [Fact] public void ChangeRequiresSameProviderContractAndTwentyFourHourBaseline()
    {
        var current=Sample()with{HolderCount=950};var prior=Sample()with{HolderCount=1000,SourceTime=Now.AddHours(-24),ReceivedTime=Now.AddHours(-24)};
        var engine=new OnChainFeatureEngine();var result=engine.Calculate(Binding,current,[],prior,true,Now);
        Assert.Equal(-50,result.HolderChange24h.Value);Assert.Equal(-5,result.HolderChange24hPercent.Value);
        Assert.Null(engine.Calculate(Binding,current,[],prior with{Provider="GoPlus"},true,Now).HolderChange24h.Value);
        Assert.Null(engine.Calculate(Binding,current,[],prior with{SourceTime=Now.AddHours(-22)},true,Now).HolderChange24h.Value);
    }
    [Fact] public void FirstSampleAndDifferentContractDoNotInventGrowth()
    {
        var engine=new OnChainFeatureEngine();Assert.Null(engine.Calculate(Binding,Sample(),[],null,true,Now).HolderChange24h.Value);
        var result=engine.Calculate(Binding,Sample()with{ContractAddress=Address(1)},[],null,true,Now);Assert.Null(result.HolderCount.Value);Assert.Null(result.RawTopPercent[10].Value);
    }
    [Theory] [InlineData(-1,DataStatus.Invalid)] [InlineData(46,DataStatus.Stale)]
    public void FutureAndStaleSamplesNeverEnterConcentration(int ageMinutes,DataStatus expected)
    {
        var result=new OnChainFeatureEngine().Calculate(Binding,Sample()with{SourceTime=Now.AddMinutes(-ageMinutes)},Enumerable.Range(1,10).Select(i=>Label(i,WalletLabelType.Exchange)).ToArray(),null,true,Now);
        Assert.Equal(expected,result.RawTopPercent[10].Status);Assert.False(result.ConcentrationUsable);
    }
    [Fact] public void EmptyHolderResponseCannotTurnIntoZeroConcentration()
    {
        var result=new OnChainFeatureEngine().Calculate(Binding,Sample(0)with{HolderCount=0},[],null,true,Now);Assert.Null(result.RawTopPercent[10].Value);Assert.Null(result.AdjustedTop10Percent.Value);
    }
    [Fact] public void RevokedLabelRevisionIsPreservedForNextEdit()
    {
        var label=Label(1,WalletLabelType.Unknown)with{Verified=false,Revision=2};var result=new OnChainFeatureEngine().Calculate(Binding,Sample(),[label],null,true,Now);
        Assert.Equal(2,Assert.Single(result.Labels).Revision);Assert.Equal(0,result.ClassificationCoveragePercent);
    }
}
