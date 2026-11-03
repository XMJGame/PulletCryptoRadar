using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.IntegrationTests;
public sealed class StrategyReviewTests
{
    private static readonly DateTimeOffset Now=new(2026,10,31,0,0,0,TimeSpan.Zero);
    [Theory][InlineData(null,true)][InlineData("HTTP 429",false)][InlineData("采集已预留，等待返回",false)]
    public void PromotingAWatchShortensOnlySuccessfulCadenceAndKeepsErrorBackoff(string? error,bool promoted)
    {
        var old=new ChainAttempt("bsc","contract",Now,Now.AddHours(6),error);
        var next=OnChainService.PromoteWatchAttempt(old,30);Assert.Equal(promoted?Now.AddMinutes(30):old.NextAttempt,next.NextAttempt);
        Assert.Equal(old.LastAttempt,next.LastAttempt);Assert.Equal(old.Error,next.Error);
    }
    private static PaperTrade Trade(Guid position,decimal net,bool final,string version=StructuralPaperEngine.Version)=>new(Guid.NewGuid(),"s",1,"TESTUSDT",Now.AddHours(-2),Now,100,101,1,10,10,net+.5m,.5m,0,net,"test",false,version,new("s",Guid.Empty,1,"TESTUSDT",Now,null)){PositionId=position,FinalExit=final};
    [Fact]public void PartialLegsAndOpenReductionsAreNotCountedAsSeparateCompletedWins()
    {
        var closed=Guid.NewGuid();var open=Guid.NewGuid();var rows=new[]{Trade(closed,4,false),Trade(closed,-6,true),Trade(open,3,false)};
        var review=StrategyReview.Build(new(){Enabled=true},rows,new Dictionary<int,MarketView>(),null,Now);
        var lane=review.Lanes.Single(l=>l.Key=="Trend");Assert.Equal(3,lane.SettlementLegs);Assert.Equal(1,lane.ClosedPositions);Assert.Equal(1,lane.RealizedNet);
        Assert.Equal(-2,lane.AverageClosedNet);Assert.Equal(0,lane.ClosedWinRate);Assert.Equal(1.5m,lane.ClosedFees);
        var tenfold=review.Lanes.Single(l=>l.Key=="Tenfold");Assert.Equal(0,tenfold.RealizedNet);Assert.Null(tenfold.AverageClosedNet);Assert.Null(tenfold.ClosedWinRate);
    }
    [Fact]public void OldTradesRemainLegacyAndStaleResearchIsNotShownAsCurrentCoverage()
    {
        var old=Trade(Guid.NewGuid(),5,true,"Paper-1.0");
        var snapshot=new OpportunitySnapshot(Guid.NewGuid(),Now.AddMinutes(-10),OpportunityEngine.Version,"h",60,[],[]);
        var review=StrategyReview.Build(new(),[old],new Dictionary<int,MarketView>(),snapshot,Now);
        Assert.Equal(5,review.Lanes.Single(l=>l.Key=="Legacy").RealizedNet);Assert.Equal(0,review.Lanes.Single(l=>l.Key=="Tenfold").RealizedNet);
        Assert.Equal(0,review.EarlyResearchObjects);
    }
    [Fact]public void AnUnpricedHoldingHasUnknownOpenPnlInsteadOfZeroProfit()
    {
        var evidence=new ForwardDecision("s","TESTUSDT","A",Now,Now,"Candidate","test",337,1,null,null,null,null,null,"unknown",null,PaperAnalysis.RulesHash);
        var p=new PaperPosition(Guid.NewGuid(),"s",1,"TESTUSDT",Now,100,1,10,10,.05m,0,Now,100,Now,95,110,new("s",Guid.Empty,1,"TESTUSDT",Now,null){StructuralEvidence=evidence});
        var review=StrategyReview.Build(new(){Rules=StructuralPaperEngine.Rules(),Positions=[p]},[],new Dictionary<int,MarketView>(),null,Now);
        Assert.Null(review.Lanes.Single(l=>l.Key=="Trend").OpenGrossPnl);
    }
}
