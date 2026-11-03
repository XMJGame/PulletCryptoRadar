using Microsoft.Extensions.Options;
using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;

namespace PulletCryptoRadar.IntegrationTests;
public sealed class TokenResearchTests
{
    private static readonly DateTimeOffset Now=DateTimeOffset.Parse("2026-10-31T02:20:00Z");
    private static string A(int n)=>"0x"+n.ToString("x40");
    private static OnChainSample Sample()=>new(Guid.NewGuid(),"bsc",A(100),"Fixture",Now,Now,"Fixture",1000,"1000000",0,
        Enumerable.Range(1,10).Select(n=>new TokenHolder(A(n),"100",11-n)).ToArray(),10,true);
    private static AddressLabel Label(int n,WalletLabelType type,bool verified=true)=>new("bsc",A(n),type,verified,"https://bscscan.com/address/"+A(n),1,Now);
    private static TokenTransfer Transfer(int from,int to,int index)=>new("bsc",A(100),100,"0x"+new string('a',64),"0x"+index.ToString("x64"),index,Now.AddMinutes(-10),A(from),A(to),"100","fixture",Now);
    private static TokenHolderAnalysis Analyze(OnChainSample? sample=null,IReadOnlyList<AddressLabel>? labels=null,IReadOnlyList<TokenTransfer>? transfers=null,IReadOnlyList<OnChainSample>? history=null,bool identity=true)=>
        TokenHolderResearch.Analyze("bsc",A(100),identity,sample??Sample(),history??[],labels??[],transfers??[],null,Now);
    private static TokenCheckReport Report()
    {
        var coin=new MarketCoin("test-token","test","Test",new Dictionary<string,string>{{"binance-smart-chain",A(100)}});
        return TokenCheckEngine.Build(coin,new(coin.Id,"binance-smart-chain",A(100),1),true,"Fixture",
            new(coin.Id,Now,Now,100,0,10000,100000,10000,1000,10000,coin.Platforms,[]),new("TESTUSDT",100,10000000,0,Now,Now),null,[],[],null,
            [new("Binance永续报价","Observed",Now,Now,"https://www.binance.com/en/futures/TESTUSDT","Fixture")],[],Now,"TESTUSDT");
    }
    private static TokenResearchClaim Claim()=>new("test-token","binance-smart-chain",A(100),"Tokenomics","Terms","Project states...","https://project.example/docs","ProjectDocumentation",null,Now,Now.AddDays(7));
    [Fact]public void OnlyVerifiedCustodyAndInfrastructureAreSubtractedWithoutReorderingOrTreatingMarketMakerAsCustody()
    {
        var result=Analyze(labels:[Label(1,WalletLabelType.Exchange),Label(2,WalletLabelType.MarketMaker),Label(3,WalletLabelType.LP)]);
        Assert.Equal(55,result.RawTop10Percent);Assert.Equal(18,result.KnownCustodyOrInfrastructurePercent);Assert.Equal(37,result.RemainingObservedPercent);
        Assert.Equal("做市商",result.Holders[1].Role);Assert.Equal(A(1),result.Holders[0].Address);Assert.Equal(1,result.Holders[0].Rank);
    }
    [Theory][InlineData(false,0)][InlineData(true,-91)][InlineData(true,1)]
    public void UnverifiedExpiredOrFutureLabelsNeverReduceConcentration(bool verified,int days)
    {
        var label=Label(1,WalletLabelType.Exchange,verified) with{UpdatedAt=Now.AddDays(days)};
        var result=Analyze(labels:[label]);Assert.Equal(0,result.KnownCustodyOrInfrastructurePercent);Assert.Equal(55,result.RemainingObservedPercent);
    }
    [Fact]public void ProviderTagsNeverBecomeVerifiedExchangeLabelsAndDuplicateHoldersInvalidateTopTen()
    {
        var s=Sample();s=s with{Holders=s.Holders.Select((h,i)=>i==0?h with{ReportedTag="Binance Exchange"}:h).ToArray()};
        var result=Analyze(s);Assert.Equal(0,result.KnownCustodyOrInfrastructurePercent);Assert.Equal("第三方标签待核验",result.Holders[0].Confidence);
        Assert.DoesNotContain("交易所",result.Holders[0].Role);
        result=Analyze(s with{Holders=s.Holders.Append(s.Holders[0]).ToArray()});Assert.False(result.OrderedTop10);Assert.Null(result.RawTop10Percent);
    }
    [Fact]public void MissingRowsStaleSamplesAndUnverifiedIdentityCannotProduceCurrentTopTenMetrics()
    {
        Assert.Null(Analyze(Sample() with{Holders=Sample().Holders.Take(9).ToArray()}).RawTop10Percent);
        Assert.Null(Analyze(Sample() with{SourceTime=Now.AddHours(-1)}).RemainingObservedPercent);
        Assert.Null(Analyze(identity:false).RawTop10Percent);Assert.Empty(Analyze(transfers:[Transfer(1,2,1)],identity:false).Relationships);
    }
    [Fact]public void CuratedHistoricalAddressCluesNeverBecomeVerifiedCustodyOrCrossChains()
    {
        var clue=Claim() with{Category="AddressHint",Address=A(1),RoleHint="疑似交易所 · Gate（待复核）",SourceKind="ExplorerHistoricalRecord"};
        var r=TokenHolderResearch.Analyze("bsc",A(100),true,Sample(),[],[],[],null,Now,[clue]);
        Assert.Contains("疑似交易所",r.Holders[0].Role);Assert.Equal(0,r.KnownCustodyOrInfrastructurePercent);Assert.Equal(0,r.ClassifiedBalancePercent);
        Assert.Contains("未计入已核验",r.Holders[0].Confidence);
        r=TokenHolderResearch.Analyze("bsc",A(100),true,Sample(),[],[],[],null,Now,[clue with{Platform="ethereum"}]);
        Assert.DoesNotContain("交易所",r.Holders[0].Role);
        Assert.Equal("历史线索待复核",Assert.Single(TokenResearchEngine.Analyze(Report(),[clue]).Items).Status);
    }
    [Fact]public void SolanaSampleDoesNotClaimGlobalOwnerTopTenEvenWhenProviderFlagIsSet()
    {
        const string mint="DvjbEsdca43oQcw2h3HW1CT7N3x5vRcr3QrvTUHnXvgV";
        var s=Sample() with{Chain="solana",ContractAddress=mint,Holders=[new(mint,"100",10)],OrderedTopHolders=true};
        var r=TokenHolderResearch.Analyze("solana",mint,true,s,[],[],[],null,Now);
        Assert.False(r.OrderedTop10);Assert.Null(r.RawTop10Percent);Assert.Null(r.Holders[0].Rank);Assert.Contains(mint,r.Tools[1].Url);
    }
    [Fact]public void ChangeUsesSameProviderAndKnownAddressInTwentyFourHourBaseline()
    {
        var old=Sample() with{SourceTime=Now.AddHours(-24),ReceivedTime=Now.AddHours(-24),Holders=[new(A(1),"1",7)]};
        var r=Analyze(history:[old]);Assert.Equal(3,r.Holders[0].Change24hPercentagePoints);Assert.Null(r.Holders[1].Change24hPercentagePoints);
        Assert.Null(Analyze(history:[old with{Provider="Another"}]).Holders[0].Change24hPercentagePoints);
    }
    [Fact]public void RelationshipsKeepDirectEvidenceButDoNotClusterKnownExchangeOriginsOrMintEndpoints()
    {
        var transfers=new[]{Transfer(11,1,1),Transfer(11,2,2),Transfer(0,1,3),Transfer(0,2,4),Transfer(1,2,5)};
        var r=Analyze(transfers:transfers);Assert.Equal(2,r.Relationships.Count);Assert.All(r.Relationships,x=>Assert.Contains("不能",x.Meaning));
        var withExchange=Analyze(labels:[Label(11,WalletLabelType.Exchange)],transfers:transfers);Assert.Single(withExchange.Relationships);
        Assert.Equal("前十地址之间有转账",withExchange.Relationships[0].Kind);Assert.Contains("不完整",withExchange.RelationshipCoverage);
        Assert.Single(Analyze(transfers:[Transfer(1,2,1),Transfer(1,2,1)]).Relationships[0].TransactionUrls);
    }
    [Fact]public void TokenomicsDoesNotFabricateUpcomingUnlocksOrUpgradeCoverage()
    {
        var report=Report();var research=TokenResearchEngine.Analyze(report,[Claim()]);Assert.Single(research.Items);
        Assert.False(research.UnlockCalendarComplete);Assert.Null(research.DocumentedUnlockNext7Days);Assert.Equal(10,research.FdvToMarketCap);
        var enriched=TokenResearchEngine.Enrich(report,Analyze(),research);Assert.Equal(report.EvidenceCoverage,enriched.EvidenceCoverage);
        Assert.Contains(research.Missing,m=>m.Contains("真实")||m.Contains("实际用户"));
    }
    [Fact]public void WrongIdentityUnsafeUrlAndFutureEvidenceAreNotAcceptedAndOldEventsAreHistorical()
    {
        var report=Report();Assert.Empty(TokenResearchEngine.Analyze(report,[Claim() with{ContractAddress=A(200)},Claim() with{SourceUrl="javascript:alert(1)"},Claim() with{CheckedAt=Now.AddDays(1)}]).Items);
        Assert.Empty(TokenResearchEngine.Analyze(report with{IdentityVerified=false},[Claim()]).Items);
        Assert.Equal("待复核",Assert.Single(TokenResearchEngine.Analyze(report,[Claim() with{CheckedAt=Now.AddDays(-10),ReviewAfter=Now.AddDays(-3)}]).Items).Status);
        Assert.Equal("历史事件",Assert.Single(TokenResearchEngine.Analyze(report,[Claim() with{Category="Announcement",EventAt=Now.AddYears(-1)}]).Items).Status);
    }
    [Fact]public void OnlyDatedPositiveUnlockEventsWithinSevenDaysContributeToDocumentedPressure()
    {
        var c=Claim() with{Category="UnlockEvent",EventAt=Now.AddDays(2),TokenAmount=100};
        var r=TokenResearchEngine.Analyze(Report(),[c,c with{EventAt=Now.AddDays(10)},c with{TokenAmount=null}]);
        Assert.Equal(100,r.DocumentedUnlockNext7Days);Assert.Equal(10,r.UnlockToCirculatingPercent);Assert.False(r.UnlockCalendarComplete);
    }
    [Fact]public void FollowupNeverUsesFutureWrongSymbolOrOutOfWindowPricesAndKeepsCompletedPoint()
    {
        var report=Report();var due=Now.AddHours(24);var close=due.AddMinutes(40);
        var bar=new Candle("TESTUSDT",60,close.AddHours(-1),105,115,95,110,1000,close.AddMilliseconds(-1),close);
        Assert.Equal("未到观察时间",TokenFollowupEngine.Evaluate(report,[bar],Now).Points[0].Status);
        Assert.Equal("等待本地数据",TokenFollowupEngine.Evaluate(report,[bar with{Symbol="OTHERUSDT"},bar with{OpenTime=due.AddHours(2)}],due.AddHours(4)).Points[0].Status);
        var observed=TokenFollowupEngine.Evaluate(report,[bar],close);Assert.Equal(10,observed.Points[0].ReturnPercent);Assert.Equal(close,observed.Points[0].PriceAt);
        Assert.Equal(observed.Points[0],TokenFollowupEngine.Evaluate(report,[bar with{Close=108}],Now.AddDays(2),observed).Points[0]);
        Assert.All(TokenFollowupEngine.Evaluate(report with{Sources=[]},[bar],Now.AddDays(8)).Points,p=>Assert.Equal("无可比基准",p.Status));
    }
    [Fact]public void FollowupPersistenceDoesNotOverwriteReportsAndSelectionCoversMoreThanOneHundredReports()
    {
        var root=Path.Combine(Path.GetTempPath(),"PulletResearch-"+Guid.NewGuid());var settings=Options.Create(new StorageOptions{Directory=root});
        try
        {
            var reports=new TokenCheckRepository(settings);var followups=new TokenFollowupRepository(settings);var report=Report();reports.Save(report);
            var path=Path.Combine(root,"TokenChecks",report.Id.ToString("N")+".json");var bytes=File.ReadAllBytes(path);
            var observed=TokenFollowupEngine.Evaluate(report,[],Now);followups.Save(observed);Assert.Equal(observed.ReportId,followups.Get(report.Id)!.ReportId);
            Assert.Equal(bytes,File.ReadAllBytes(path));
            for(var i=0;i<120;i++)reports.Save(report with{Id=Guid.NewGuid(),AsOf=Now.AddMinutes(-i)});
            Assert.Equal(121,reports.ForFollowup(Now.AddDays(-8)).Count);
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
