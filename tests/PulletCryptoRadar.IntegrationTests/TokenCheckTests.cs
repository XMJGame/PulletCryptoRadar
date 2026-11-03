using System.Text.Json;
using Microsoft.Extensions.Options;
using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;
using PulletCryptoRadar.Providers.Market;

namespace PulletCryptoRadar.IntegrationTests;
public sealed class TokenCheckTests
{
    private static readonly DateTimeOffset Now=new(2026,10,31,1,30,0,TimeSpan.Zero);
    private const string Address="0x1111111111111111111111111111111111111111";
    private static MarketCoin Coin=>new("real-token","test","Real Token",new Dictionary<string,string>{{"binance-smart-chain",Address}});
    private static TokenProjectSnapshot Project=>new(Coin.Id,Now,Now,100,3,10_000_000,100_000_000,10_000_000,100_000,1_000_000,Coin.Platforms,[]);
    private static TokenVenueIdentity Venue=>new("TEST","Real Token",Now,Now,false,new Dictionary<string,string>{{"binance-smart-chain",Address}},"https://api.gateio.ws/");
    private static TokenCheckRequest Request=>new(Coin.Id,"binance-smart-chain",Address,1);
    private static IReadOnlyList<Candle> Bars()=>Enumerable.Range(0,169).Select(i=>new Candle("TESTUSDT",60,Now.AddMinutes(-30).AddHours(i-169),100+i*.05m,102+i*.05m,98+i*.05m,100+i*.05m,10000,Now.AddMinutes(-30).AddHours(i-168).AddMilliseconds(-1),Now)).ToArray();
    private static TokenCheckReport Report(bool identity=true,TokenProjectSnapshot? project=null,TickerEvent? ticker=null,IReadOnlyList<Candle>? bars=null,OnChainSample? sample=null)=>TokenCheckEngine.Build(Coin,Request,identity,"Fixture",project??Project,
        ticker??new("TESTUSDT",108,10_000_000,3,Now,Now),null,bars??Bars(),[],sample,[],[],Now,"TESTUSDT");
    [Fact]public void ContractMismatchDelistingAndStaleVenueCannotVerifyTheSameSymbol()
    {
        Assert.True(TokenCheckEngine.IdentityMatches(Coin,Request.Platform,Address,Venue,Now));
        Assert.False(TokenCheckEngine.IdentityMatches(Coin,Request.Platform,Address,Venue with{Delisted=true},Now));
        Assert.False(TokenCheckEngine.IdentityMatches(Coin,Request.Platform,Address,Venue with{SourceTime=Now.AddHours(-1)},Now));
        Assert.False(TokenCheckEngine.IdentityMatches(Coin,Request.Platform,Address,Venue with{Contracts=new Dictionary<string,string>{{Request.Platform,"0x2222222222222222222222222222222222222222"}}},Now));
    }
    [Fact]public void SolanaIdentityIsNotCaseFolded()
    {
        const string mint="DvjbEsdca43oQcw2h3HW1CT7N3x5vRcr3QrvTUHnXvgV";
        var coin=Coin with{Platforms=new Dictionary<string,string>{{"solana",mint}}};
        Assert.False(TokenCheckEngine.IdentityMatches(coin,"solana",mint,Venue with{Contracts=new Dictionary<string,string>{{"solana",mint.ToLowerInvariant()}}},Now));
    }
    [Fact]public void MissingOrStaleIdentityAndQuotesCannotProduceAnEntryVerdict()
    {
        Assert.Equal("资料不足",Report(identity:false).Verdict);
        var stale=Report(ticker:new("TESTUSDT",100,10_000_000,0,Now.AddMinutes(-5),Now));
        Assert.Equal("资料不足",stale.Verdict);Assert.Contains(stale.Missing,s=>s.Contains("90秒"));
        Assert.Equal(100,stale.Price);Assert.True(stale.EvidenceCoverage<100);
    }
    [Fact]public void IncompleteOrForeignCandlesNeverInventSupportAndTargets()
    {
        var gap=Bars().Where((_,i)=>i!=100).ToArray();var result=TokenCheckEngine.Technical("TESTUSDT",gap,Now,108);
        Assert.False(result.Continuous);Assert.Null(result.Support);Assert.Null(result.Resistance);
        var wrong=TokenCheckEngine.Technical("OTHERUSDT",Bars(),Now,108);Assert.Equal(0,wrong.Hours);
        var monotonic=TokenCheckEngine.Technical("TESTUSDT",Bars(),Now,108);Assert.True(monotonic.Continuous);
        Assert.Null(monotonic.Support);Assert.Null(monotonic.Invalidation);Assert.Null(monotonic.Resistance);Assert.Null(monotonic.RewardRiskToResistance);
    }
    [Fact]public void OnlyPivotsWithTwoClosedBarsOnTheRightAreUsed()
    {
        var rows=Bars().ToArray();rows[140]=rows[140] with{Low=90};rows[141]=rows[141] with{High=120};rows[^1]=rows[^1] with{Low=50,High=200};
        var t=TokenCheckEngine.Technical("TESTUSDT",rows,Now,108);
        Assert.Equal(90,t.Support);Assert.Equal(120,t.Resistance);Assert.Equal(rows[140].EndTime,t.SupportTime);Assert.Equal(rows[141].EndTime,t.ResistanceTime);
        Assert.True(t.Invalidation<90);Assert.NotEqual(50,t.Support);Assert.NotEqual(200,t.Resistance);
    }
    [Fact]public void OiUsesQuantityOfSameContractAndKnownObservationTimes()
    {
        DerivativeSample[] rows=[new("TESTUSDT",DerivativeKind.OpenInterest5m,Now.AddDays(-1),Now,100,10000),new("TESTUSDT",DerivativeKind.OpenInterest5m,Now,Now,110,30000)];
        Assert.Equal(10,TokenCheckEngine.OiChange("TESTUSDT",rows,Now,out _));
        Assert.Null(TokenCheckEngine.OiChange("OTHERUSDT",rows,Now,out _));
        Assert.Null(TokenCheckEngine.OiChange("TESTUSDT",[rows[0],rows[1] with{ReceivedTime=Now.AddMinutes(1)}],Now,out _));
        Assert.Null(TokenCheckEngine.OiChange("TESTUSDT",rows,Now.AddMinutes(12),out _));
    }
    [Fact]public void FreshHoneypotClaimBlocksEntryButStaleSecurityCannotBePresentedAsCurrent()
    {
        var evidence=new TokenSecurityEvidence("GoPlus","bsc",Address,"https://api.gopluslabs.io/",Now,Now,1000,new Dictionary<string,bool?>{{"is_honeypot",true}},[]);
        var sample=new OnChainSample(Guid.NewGuid(),"bsc",Address,"Binplorer",Now,Now,"fixture",1000,"1000",0,[],100,true,evidence);
        var report=Report(sample:sample);Assert.Equal("暂不考虑当前买入",report.Verdict);Assert.Contains(report.Risks,r=>r.Contains("is_honeypot"));
        var stale=Report(sample:sample with{Security=evidence with{SourceTime=Now.AddHours(-2)}});
        Assert.DoesNotContain(stale.Risks,r=>r.Contains("is_honeypot"));Assert.Contains(stale.Missing,r=>r.Contains("安全检测"));
    }
    [Fact]public void PotentialDoesNotEquateSmallCapOrFdvWithGuaranteedUpside()
    {
        var report=Report();Assert.Contains(report.Risks,r=>r.Contains("稀释"));Assert.Contains("不是胜率",report.EvidenceMeaning);
        Assert.Contains("尚无证据",report.Potential);Assert.NotEqual("买入",report.Verdict);
        Assert.Contains(report.Missing,r=>r.Contains("退市"));Assert.Contains("不修改妖币池",report.Isolation);
    }
    [Fact]public void ProjectParserRejectsWrongIdAndFiltersUnsafeHomepageLinks()
    {
        var json=JsonSerializer.SerializeToElement(new{id="real-token",last_updated=Now,platforms=Coin.Platforms,links=new{homepage=new[]{"javascript:alert(1)","https://example.org/","https://user:secret@example.org/"}},market_data=new{current_price=new{usd=1},market_cap=new{usd=0}}});
        var p=TokenCheckParser.Project(json,Coin.Id,Now);Assert.Null(p.MarketCapUsd);Assert.Equal("https://example.org/",Assert.Single(p.Websites));
        Assert.Throws<InvalidDataException>(()=>TokenCheckParser.Project(json,"fake-token",Now));
    }
    [Fact]public void FilesRetainSeparateOriginalReportTimeAndSurviveNewRepositoryInstance()
    {
        var root=Path.Combine(Path.GetTempPath(),"PulletQuickCheck-"+Guid.NewGuid());
        try
        {
            var settings=Options.Create(new StorageOptions{Directory=root});var repo=new TokenCheckRepository(settings);var report=Report();repo.Save(report);
            var reopened=new TokenCheckRepository(settings);var read=reopened.Get(report.Id)!;Assert.Equal(report.AsOf,read.AsOf);Assert.Equal(report.Verdict,read.Verdict);
            Assert.Single(reopened.Recent(12));Assert.False(File.Exists(Path.Combine(root,"PulletCryptoRadar.db")));
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
