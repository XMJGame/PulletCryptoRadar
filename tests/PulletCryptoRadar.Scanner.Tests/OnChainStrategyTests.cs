using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Scanner.Tests;

public sealed class OnChainStrategyTests
{
    private static readonly DateTimeOffset Now=Samples.Now;
    private static OnChainFeatures Chain=>new(){TrustedIdentity=true,ConcentrationUsable=true,AdjustedTop10Percent=Samples.Value(85m),HolderChange24hPercent=Samples.Value(-6m)};
    private static MarketView Market()=>Samples.Market()with{Features=Samples.Market().Features with{OnChain=Chain,Capital=new(){TrustedIdentity=true,MarketCapUsd=Samples.Value(50_000_000m),FdvUsd=Samples.Value(100_000_000m)}}};
    private static SpeculativeInput Input()=>new(Market(),"Binance","TESTUSDT",true,Samples.Value(1m),Samples.Value(100_000_000m),Samples.Value(3m),Samples.Value(10m),Samples.Value(0m),Samples.Value(10_000_000m),Samples.Value(5m),Samples.Value(3d),Samples.Value(.6m),Samples.Value(3m),Samples.Value(3d),Samples.Value(3d),"fixture");
    [Fact] public void NewSpeculativeVersionAddsOnlyCoveredChainWeights()
    {
        var options=new SpeculativeOptions();var old=new SpeculativeDetector(options).Detect(Input(),Now);var upgraded=new SpeculativeOnChainDetector(options,new(options)).Detect(Input(),Now);
        Assert.Equal("Speculative-1.0",old.StrategyVersion);Assert.Equal("Speculative-1.1",upgraded.StrategyVersion);Assert.Equal(95,upgraded.AnomalyCoverage);Assert.Equal(85,upgraded.RiskCoverage);
        Assert.Equal(old.AnomalyScore+15,upgraded.AnomalyScore);Assert.Contains("Wallet",upgraded.MissingFamilies);
    }
    [Fact] public void UnclassifiedOrUnverifiedConcentrationDoesNotEnterScore()
    {
        var input=Input();input=input with{Market=input.Market with{Features=input.Market.Features with{OnChain=Chain with{ConcentrationUsable=false,HolderChange24hPercent=FeatureValue<decimal>.Missing()}}}};
        var result=new SpeculativeOnChainDetector(new(),new(new())).Detect(input,Now);Assert.Equal(80,result.AnomalyCoverage);Assert.Null(result.AnomalyComponents.Single(c=>c.Family=="Concentration").Strength);
    }
    [Fact] public void ChainDataCannotBypassMissingTrustedMarketCap()
    {
        var i=Input();i=i with{Market=i.Market with{Features=i.Market.Features with{Capital=new()}}};
        Assert.Null(new SpeculativeOnChainDetector(new(),new(new())).Detect(i,Now).AnomalyScore);
    }
    [Fact] public void ChainRiskOverridesPositiveWatchButNeverDowngradesCritical()
    {
        var i=WatchTests.Input();i=i with{Quote=i.Quote with{Market=Market()}};
        var engine=new WatchOnChainStrategyEngine(new(new()));var result=engine.Evaluate(i,Now);Assert.Equal("WatchTower-1.1",result.Version);Assert.Equal(StrategyState.ReduceRisk,result.State);Assert.Equal(SignalRisk.High,result.Risk);Assert.NotNull(result.OnChainScore);Assert.Null(result.WalletRiskScore);
        i=i with{Quote=i.Quote with{Return1h=Samples.Value(-30m),OiChange15m=Samples.Value(-10m)}};
        Assert.Equal(StrategyState.CriticalRisk,engine.Evaluate(i,Now).State);
    }
    [Fact] public void StaleChainInputsDoNotTurnIntoFreshRisk()
    {
        var i=WatchTests.Input();i=i with{Quote=i.Quote with{Market=Market()with{Features=Market().Features with{OnChain=Chain with{AdjustedTop10Percent=Samples.Value(85m,Now.AddHours(-2)),HolderChange24hPercent=Samples.Value(-6m,Now.AddHours(-2))}}}}};
        var result=new WatchOnChainStrategyEngine(new(new())).Evaluate(i,Now);Assert.Null(result.OnChainScore);Assert.Equal(StrategyState.PositiveObserve,result.State);
    }
}
