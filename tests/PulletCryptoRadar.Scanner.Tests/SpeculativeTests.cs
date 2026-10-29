using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Scanner.Tests;

public sealed class SpeculativeTests
{
    private static readonly DateTimeOffset Now=Samples.Now;
    private static FeatureValue<T> F<T>(T value)where T:struct=>Samples.Value(value);
    private static SpeculativeInput Input()
    {
        var m=Samples.Market() with{Features=Samples.Market().Features with{Capital=new(){TrustedIdentity=true,MarketCapUsd=F(50_000_000m),FdvUsd=F(100_000_000m)}}};
        return new(m,"Binance","TESTUSDT",true,F(1m),F(100_000_000m),F(3m),F(10m),F(0m),F(10_000_000m),F(5m),F(3d),F(.6m),F(3m),F(3d),F(3d),"test");
    }
    [Fact] public void IndependentScoresKeepFullWeightsAndMissingChainCoverage()
    {
        var result=new SpeculativeDetector(new()).Detect(Input(),Now);
        Assert.Equal(100,result.AnomalyComponents.Sum(c=>c.Weight));Assert.Equal(100,result.RiskComponents.Sum(c=>c.Weight));
        Assert.Equal(80,result.AnomalyCoverage);Assert.Equal(70,result.RiskCoverage);Assert.True(result.IsCandidate);
        Assert.True(result.AnomalyScore>result.RiskScore);Assert.True(result.AnomalyScore<=80);
        Assert.Contains("Wallet",result.MissingFamilies);Assert.Contains("ProjectOutflow",result.MissingFamilies);
        Assert.Equal(result.AnomalyScore,Math.Round(result.AnomalyComponents.Sum(c=>c.Weight*(c.Strength??0)),2));
    }
    [Fact] public void VerifiedCapitalAndVenueIdentityAreBothRequired()
    {
        var input=Input();var engine=new SpeculativeDetector(new());
        Assert.Null(engine.Detect(input with{VenueIdentityVerified=false},Now).AnomalyScore);
        Assert.Null(engine.Detect(input with{Market=input.Market with{Features=input.Market.Features with{Capital=input.Market.Features.Capital with{TrustedIdentity=false}}}},Now).RiskScore);
    }
    [Fact] public void ExtremeFundingIsRiskAndSuppressesCandidates()
    {
        var result=new SpeculativeDetector(new()).Detect(Input() with{FundingRate8h=F(.003m)},Now);
        Assert.Equal("Overheated",result.Stage);Assert.False(result.IsCandidate);
        Assert.Equal(1,result.RiskComponents.Single(c=>c.Family=="ExtremeFunding").Strength);
    }
    [Theory] [InlineData(-1)] [InlineData(661)]
    public void FutureOrStaleOiBlocksBothScores(int age)
    {
        var input=Input() with{OpenInterestUsd=F(10_000_000m) with{SourceTime=Now.AddSeconds(-age)}};
        var result=new SpeculativeDetector(new()).Detect(input,Now);Assert.Null(result.AnomalyScore);Assert.Null(result.RiskScore);Assert.False(result.IsCandidate);
    }
    [Fact] public void StaleOrZeroCapitalCannotProduceRatios()
    {
        foreach(var cap in new[]{F(0m),F(50_000_000m)with{SourceTime=Now.AddHours(-2)}})
        {
            var i=Input();i=i with{Market=i.Market with{Features=i.Market.Features with{Capital=i.Market.Features.Capital with{MarketCapUsd=cap}}}};
            var result=new SpeculativeDetector(new()).Detect(i,Now);Assert.Null(result.AnomalyScore);Assert.Null(result.AnomalyComponents.Single(c=>c.Family=="OiMarketCap").Strength);
        }
    }
    [Fact] public void GateMissingPriceBaselineNeverReusesBinanceEvidence()
    {
        var i=Input() with{Exchange="Gate",NativeSymbol="TEST_USDT",PriceRobustZ=FeatureValue<double>.Missing(),VolumeRobustZ=FeatureValue<double>.Missing()};
        var result=new SpeculativeDetector(new()).Detect(i,Now);Assert.Equal(70,result.AnomalyCoverage);
        Assert.Null(result.AnomalyComponents.Single(c=>c.Family=="PriceVolume").Strength);Assert.NotNull(result.AnomalyScore);
    }
    [Fact] public void ScoreThresholdDoesNotOverrideLiquidityAndCapFilters()
    {
        var result=new SpeculativeDetector(new(){MinVolume24hUsd=200_000_000}).Detect(Input(),Now);
        Assert.NotNull(result.AnomalyScore);Assert.False(result.IsCandidate);
    }
    [Fact] public void InvalidNumericsCannotProduceNanOrOverflowScores()
    {
        var i=Input() with{OiRobustZ=F(double.NaN),OpenInterestUsd=F(decimal.MaxValue)};
        i=i with{Market=i.Market with{Features=i.Market.Features with{Capital=i.Market.Features.Capital with{MarketCapUsd=F(.00000000000000000000001m)}}}};
        var result=new SpeculativeDetector(new()).Detect(i,Now);Assert.Null(result.AnomalyComponents.Single(c=>c.Family=="OiMarketCap").Strength);
        Assert.Null(result.AnomalyComponents.Single(c=>c.Family=="OiAnomaly").Strength);
    }
}
