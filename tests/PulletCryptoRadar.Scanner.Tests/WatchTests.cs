using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Scanner.Tests;

public sealed class WatchTests
{
    private static readonly DateTimeOffset Now=Samples.Now;
    private static FeatureValue<decimal> F(decimal value)=>Samples.Value(value);
    public static WatchInput Input(decimal price=.088m)
    {
        var m=Samples.Market();var config=new WatchAsset(1,true,.4m,.1m,null,.05m,.12m,true,[],1,Now,Now);
        var q=new SpeculativeInput(m,"Binance","TESTUSDT",true,F(price),F(1_000_000),F(3),F(10),F(0),F(1_000_000),F(4),Samples.Value(3d),F(.6m),F(2),Samples.Value(3d),Samples.Value(3d),"fixture");
        return new(config,q,null,null,null);
    }
    [Fact] public void CostReturnAndRequiredRecoveryUseDifferentDenominators()
    {
        var r=new WatchStrategyEngine(new()).Evaluate(Input(),Now);
        Assert.Equal(-78,r.CostReturnPercent);Assert.Equal(354.54545454545454545454545455m,r.ToCostPercent);
        Assert.Equal(StrategyState.PositiveObserve,r.State);Assert.Equal(SignalRisk.Unknown,r.Risk);Assert.Null(r.OnChainScore);Assert.Null(r.WalletRiskScore);
    }
    [Theory] [InlineData(-1)] [InlineData(31)]
    public void FutureOrStalePriceDisablesCostAndStrategy(int age)
    {
        var i=Input();i=i with{Quote=i.Quote with{Price=F(.088m)with{SourceTime=Now.AddSeconds(-age)}}};
        var r=new WatchStrategyEngine(new()).Evaluate(i,Now);Assert.Null(r.CostReturnPercent);Assert.Equal(StrategyState.WaitConfirmation,r.State);Assert.Equal(DataStatus.Missing,r.Status);
    }
    [Fact] public void UnverifiedGateCannotComparePersonalCost()
    {
        var i=Input();i=i with{Quote=i.Quote with{Exchange="Gate",VenueIdentityVerified=false}};
        var r=new WatchStrategyEngine(new()).Evaluate(i,Now);Assert.Null(r.ToCostPercent);Assert.Equal(DataStatus.Missing,r.Status);
    }
    [Fact] public void SevereDropWithOiRiskOverridesPositiveEvidence()
    {
        var i=Input();i=i with{Quote=i.Quote with{Return1h=F(-30),OiChange15m=F(-10)}};
        var r=new WatchStrategyEngine(new()).Evaluate(i,Now);Assert.Equal(StrategyState.CriticalRisk,r.State);Assert.Equal(SignalRisk.Critical,r.Risk);
    }
    [Fact] public void SupportAndOiDeteriorationOverrideBullishPrice()
    {
        var i=Input(.04m);i=i with{Quote=i.Quote with{OiChange15m=F(-5)}};
        Assert.Equal(StrategyState.ReduceRisk,new WatchStrategyEngine(new()).Evaluate(i,Now).State);
    }
    [Fact] public void CrowdedFundingIsAvoidChaseNotPositiveObserve()
    {
        var i=Input();i=i with{Quote=i.Quote with{FundingRate8h=F(.003m)}};
        Assert.Equal(StrategyState.AvoidChase,new WatchStrategyEngine(new()).Evaluate(i,Now).State);
    }
    [Fact] public void TargetCrossNeedsBaselineAndSameVenueAndConfig()
    {
        var engine=new WatchStrategyEngine(new());var first=Input(.09m);var old=engine.Advance(first,engine.Evaluate(first,Now),null,Now).Checkpoint;
        var next=Input(.11m);var result=engine.Evaluate(next,Now);
        Assert.NotNull(engine.Advance(next,result,old,Now).Notification);
        Assert.Null(engine.Advance(next,result,null,Now).Notification);
        Assert.Null(engine.Advance(next with{Config=next.Config with{Revision=2}},result,old,Now).Notification);
        Assert.Null(engine.Advance(next,result with{Exchange="Gate"},old,Now).Notification);
    }
    [Fact] public void CriticalFlappingAndUnknownIntervalsDoNotBypassCooldown()
    {
        var engine=new WatchStrategyEngine(new());var i=Input();var normal=engine.Evaluate(i,Now);var critical=normal with{Risk=SignalRisk.Critical,State=StrategyState.CriticalRisk};
        var first=engine.Advance(i,critical,null,Now);Assert.NotNull(first.Notification);
        Assert.Null(engine.Advance(i,critical,first.Checkpoint,Now.AddSeconds(10)).Notification);
        var reset=engine.Advance(i,normal,first.Checkpoint,Now.AddSeconds(20)).Checkpoint;
        Assert.Null(engine.Advance(i,critical,reset,Now.AddSeconds(30)).Notification);
        var gap=engine.Advance(i,normal with{Status=DataStatus.Stale},first.Checkpoint,Now.AddSeconds(20)).Checkpoint;
        Assert.Null(engine.Advance(i,critical,gap,Now.AddSeconds(30)).Notification);
    }
    [Fact] public void FirstCriticalEscalationBypassesPersonalCooldown()
    {
        var engine=new WatchStrategyEngine(new());var i=Input();var normal=engine.Evaluate(i,Now);
        var risk=engine.Advance(i,normal with{State=StrategyState.RiskIncreasing},null,Now);
        Assert.Equal(NotificationKind.WatchListPersonal,risk.Notification!.Kind);
        var critical=engine.Advance(i,normal with{Risk=SignalRisk.Critical,State=StrategyState.CriticalRisk},risk.Checkpoint,Now.AddSeconds(5));
        Assert.Equal(NotificationKind.WatchListCritical,critical.Notification!.Kind);
    }
    [Fact] public void DisabledPersonalAlertsNeverReserveNotifications()
    {
        var engine=new WatchStrategyEngine(new());var i=Input();i=i with{Config=i.Config with{EnableAlerts=false}};
        Assert.Null(engine.Advance(i,engine.Evaluate(i,Now) with{Risk=SignalRisk.Critical},null,Now).Notification);
        Assert.Equal(DataStatus.Missing,engine.Evaluate(i with{Config=i.Config with{Enabled=false}},Now).Status);
    }
}
