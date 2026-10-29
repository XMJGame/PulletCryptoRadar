using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Scanner.Tests;

public sealed class WatchFlowTests
{
    private static readonly DateTimeOffset Now=Samples.Now;
    private static WatchFlowStrategyEngine Engine()=>new(new(new(new())));
    private static FlowSummary Evidence=>new(){TrustedIdentity=true,Fresh=true,ThroughTime=Now,ProjectOutflowSupplyPercent=.1m,ProjectToExchangeSupplyPercent=.01m};
    [Fact] public void ConfirmedDirectFlowRaisesRiskWithoutClaimingSale()
    {var r=Engine().Evaluate(WatchTests.Input() with{Flow=Evidence},Now);Assert.Equal("WatchTower-1.2",r.Version);Assert.Equal(SignalRisk.High,r.Risk);Assert.Equal(StrategyState.RiskIncreasing,r.State);Assert.Equal(80,r.WalletRiskScore);Assert.Contains(r.RiskReasons,s=>s.Contains("不证明卖出"));}
    [Theory] [InlineData("stale")] [InlineData("identity")] [InlineData("future")] [InlineData("empty")]
    public void MissingEvidenceDoesNotBecomeLowWalletRisk(string issue)
    {
        var f=issue switch{"stale"=>Evidence with{Fresh=false},"identity"=>Evidence with{TrustedIdentity=false},"future"=>Evidence with{ThroughTime=Now.AddMinutes(1)},_=>new FlowSummary()};
        var result=Engine().Evaluate(WatchTests.Input() with{Flow=f},Now);Assert.Null(result.WalletRiskScore);Assert.Equal(StrategyState.PositiveObserve,result.State);
    }
    [Fact] public void FlowCannotDowngradeCriticalOrBypassInvalidPrice()
    {
        var input=WatchTests.Input();var bad=input with{Flow=Evidence,Quote=input.Quote with{Return1h=Samples.Value(-30m),OiChange15m=Samples.Value(-10m)}};
        Assert.Equal(StrategyState.CriticalRisk,Engine().Evaluate(bad,Now).State);
        var missing=input with{Flow=Evidence,Quote=input.Quote with{Price=FeatureValue<decimal>.Missing()}};Assert.Null(Engine().Evaluate(missing,Now).WalletRiskScore);
    }
}
