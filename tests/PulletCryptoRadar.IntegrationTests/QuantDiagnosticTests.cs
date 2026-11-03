using Microsoft.Extensions.Options;
using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;
namespace PulletCryptoRadar.IntegrationTests;

public sealed class QuantDiagnosticTests
{
    private static readonly DateTimeOffset Hour=new(2026,10,20,0,0,0,TimeSpan.Zero);
    private static readonly DateTimeOffset Now=Hour.AddDays(10);
    private static readonly QuantStudyParameters Costs=new();
    private static readonly QuantStudySeries Source=new(new(1,"TESTUSDT","TEST",Hour.AddYears(-1),true),Now,"fixture",[],null);
    private static readonly string Digest=QuantRules.HashValue(Source);
    private static Candle Bar(decimal open,decimal high,decimal low,decimal close)=>new("TESTUSDT",60,Hour,open,high,low,close,1_000_000,Hour.AddHours(1).AddMilliseconds(-1),Now);
    private static QuantStudyTrade Trade(string direction="Long",string policy="ScaleTail")
    {
        var sign=direction=="Long"?1m:-1m;
        var seed=new QuantExitSeed("TESTUSDT",direction,Hour,100*(1+sign*Costs.SlippagePerSide),1,100-sign*5,100+sign*10,100+sign*20,1);
        var exit=QuantExitEngine.Replay(seed,[Bar(100,125,75,100)],Now,policy,1,Costs,true);
        return new(Guid.NewGuid().ToString(),"Holdout","TESTUSDT","TrendPullback",direction,policy,1,Hour,Now,Digest,seed,exit,exit);
    }
    private static QuantStudyReport Report(IReadOnlyList<QuantStudyTrade> trades,IReadOnlyList<QuantStudySeries>? extra=null)
    {
        var request=QuantStudyEngine.Request([Source.Instrument],new(),Now);var (report,_)=QuantStudyEngine.Run(request,[],new(0,0,0,0,0,[]),Now,null,default);
        var summaries=report.Summaries.Select(s=>QuantStudyEngine.Summary(s.Window,s.Strategy,s.Policy,s.CostMultiplier,trades.Where(t=>t.Window==s.Window&&t.Strategy==s.Strategy&&t.Policy==s.Policy&&t.CostMultiplier==s.CostMultiplier).ToArray(),Costs)).ToArray();
        var sources=new[]{Source}.Concat(extra??[]).Select(s=>new QuantStudySource(s.Instrument.Symbol,QuantRules.HashValue(s),s.Candles.Count,null,null,s.Source,null)).ToArray();
        return report with{Summaries=summaries,Sources=sources};
    }
    private static QuantDiagnosticReport Build(IReadOnlyList<QuantStudyTrade> trades)=>QuantDiagnosticEngine.Build(Report(trades),trades,_=>null,"Holdout","TrendPullback","ScaleTail",1);
    [Theory][InlineData("Long")][InlineData("Short")]
    public void SamePathCostBridgeReconcilesGrossSlippageFeesFundingAndNetForBothSides(string direction)
    {
        var trade=Trade(direction);var result=Build([trade]);var m=result.Metrics;
        Assert.Equal(1,m.Completed);Assert.Equal(0,m.InvalidAccounting);Assert.True(m.MeanSlippageR>0);Assert.True(m.MeanFeeR>0);Assert.True(m.MeanFundingR>0);
        Assert.True(Math.Abs((m.MeanRawGrossR-m.MeanSlippageR-m.MeanFeeR-m.MeanFundingR-m.MeanNetR)!.Value)<.0000000001m);
        Assert.Equal(trade.Conservative.Net/trade.Conservative.InitialRisk,m.MeanNetR);
        Assert.Equal(1,result.Groups.Single(g=>g.Dimension=="Direction"&&g.Key==direction).Metrics.Completed);
    }
    [Fact]public void MissingAndCorruptClosedPathsAreExcludedAndCountedSeparatelyRatherThanZeroProfit()
    {
        var valid=Trade();var missing=valid with{Id="missing",Conservative=valid.Conservative with{Status="MissingPath",Net=null}};
        var corrupt=valid with{Id="corrupt",Conservative=valid.Conservative with{Net=999}};
        var result=Build([valid,missing,corrupt]);Assert.Equal(3,result.Metrics.Samples);Assert.Equal(1,result.Metrics.Completed);Assert.Equal(1,result.Metrics.Unfinished);Assert.Equal(1,result.Metrics.InvalidAccounting);
        Assert.Equal(valid.Conservative.Net/valid.Conservative.InitialRisk,result.Metrics.MeanNetR);
    }
    [Fact]public void QuantityAndFeeMismatchesCannotEnterDiagnosticProfit()
    {
        var valid=Trade();var badQuantity=valid with{Conservative=valid.Conservative with{Legs=valid.Conservative.Legs.Select((l,i)=>i==0?l with{Quantity=l.Quantity+1}:l).ToArray()}};
        Assert.Equal(1,Build([badQuantity]).Metrics.InvalidAccounting);
        Assert.Equal(1,Build([valid with{Conservative=valid.Conservative with{Fees=0}}]).Metrics.InvalidAccounting);
    }
    [Fact]public void NoSamplesAndNoRemainingConcentrationSamplesStayUnknown()
    {
        var empty=Build([]);Assert.Null(empty.Metrics.MeanNetR);Assert.Null(empty.Metrics.MeanFeeR);Assert.Null(empty.Pairing.MeanTailMinusFullR);
        var positive=Trade();Assert.True(positive.Conservative.Net>0);var one=Build([positive]);
        Assert.All(one.Concentration,c=>{Assert.Equal(0,c.RemainingCompleted);Assert.Null(c.MeanWithoutTopThree);Assert.Equal(100,c.TopThreeShareOfPositiveGroupNet);});
    }
    [Fact]public void PairingRequiresTheSameEntryCountsUnmatchedAndDoesNotInferFromAggregateAverages()
    {
        var tail=Trade();var full=Trade(policy:"FullAtFirst");var unmatched=tail with{Id="tailOnly",Direction="Short",Seed=tail.Seed with{Direction="Short"}};
        var paired=Build([tail,full,unmatched]);Assert.Equal(1,paired.Pairing.Matched);Assert.Equal(1,paired.Pairing.TailOnly);Assert.Equal(0,paired.Pairing.FullOnly);
        Assert.Equal((tail.Conservative.Net-full.Conservative.Net)/tail.Conservative.InitialRisk,paired.Pairing.MeanTailMinusFullR);
        var changed=full with{Seed=full.Seed with{Target1=111}};Assert.Equal(1,Build([tail,changed]).Pairing.InvalidPairs);
        var missing=full with{Conservative=full.Conservative with{Status="MissingPath",Net=null}};Assert.Equal(1,Build([tail,missing]).Pairing.UnfinishedPairs);
    }
    [Fact]public void DatesAreGroupedInUtcAndUnavailableMarketContextRemainsUnknown()
    {
        var trade=Trade();var local=Hour.ToOffset(TimeSpan.FromHours(-5));Assert.NotEqual(Hour.Date,local.Date);trade=trade with{SignalHour=local,Seed=trade.Seed with{EntryAt=local}};
        var result=Build([trade]);Assert.Equal("2026-10-20",Assert.Single(result.Groups,g=>g.Dimension=="UtcDate").Key);
        Assert.Equal(1,result.Groups.Single(g=>g.Dimension=="Environment"&&g.Key=="Unknown").Metrics.Completed);
    }
    [Fact]public void EnvironmentUsesOnlyClosedHistoricalBarsAndVerifiesFrozenInputs()
    {
        QuantStudySeries Series(string symbol)
        {
            var bars=Enumerable.Range(0,337).Select(i=>{var end=Hour.AddHours(i-336);var close=100+i;
                return new Candle(symbol,60,end.AddHours(-1),close-1,close+1,close-2,close,1_000_000,end.AddMilliseconds(-1),Now);}).ToArray();
            var future=bars[^1] with{OpenTime=Hour,Close=1,Low=.1m,SourceTime=Hour.AddHours(1).AddMilliseconds(-1)};
            return new(new(symbol=="BTCUSDT"?2:3,symbol,symbol[..3],Hour.AddYears(-1),true),Now,"fixture",bars.Append(future).ToArray(),null);
        }
        var btc=Series("BTCUSDT");var eth=Series("ETHUSDT");var trade=Trade();var report=Report([trade],[btc,eth]);var map=new[]{btc,eth}.ToDictionary(QuantRules.HashValue);
        var result=QuantDiagnosticEngine.Build(report,[trade],hash=>map.GetValueOrDefault(hash),"Holdout","TrendPullback","ScaleTail",1);
        Assert.Equal(1,result.Groups.Single(g=>g.Dimension=="Environment"&&g.Key=="BothUp").Metrics.Completed);
        Assert.Throws<InvalidDataException>(()=>QuantDiagnosticEngine.Build(report,[trade],_=>btc with{Source="changed"},"Holdout","TrendPullback","ScaleTail",1));
    }
    [Fact]public void GrossWinnersTurnedNegativeByModelCostsAreCountedWithoutPretendingTheyAreNetWinners()
    {
        var trade=Trade(policy:"FullAtFirst");var seed=trade.Seed with{Target1=100.1m,Target2=100.2m};
        var exit=QuantExitEngine.Replay(seed,[Bar(100,100.11m,99,100.1m)],Now,"FullAtFirst",1,Costs);
        trade=trade with{Seed=seed,Conservative=exit,TargetFirst=exit};
        var result=QuantDiagnosticEngine.Build(Report([trade]),[trade],_=>null,"Holdout","TrendPullback","FullAtFirst",1);
        Assert.Equal(1,result.Metrics.CostFlipped);Assert.True(result.Metrics.MeanRawGrossR>0);Assert.True(result.Metrics.MeanNetR<0);
    }
    [Fact]public void NegativeContributionsDoNotProduceNegativeOrInfiniteConcentrationPercentages()
    {
        var trade=Trade();var exit=QuantExitEngine.Replay(trade.Seed,[Bar(100,101,94,95)],Now,"ScaleTail",1,Costs);
        var result=Build([trade with{Conservative=exit,TargetFirst=exit}]);
        Assert.All(result.Concentration,c=>{Assert.Empty(c.RemovedPositiveGroups);Assert.Equal(1,c.RemainingCompleted);Assert.Null(c.TopThreeShareOfPositiveGroupNet);Assert.Equal(result.Metrics.MeanNetR,c.MeanWithoutTopThree);});
    }
    [Fact]public void TruncatedDuplicatedForeignOrUnrecognizedStudyCannotSilentlyBecomeASmallerPositiveSample()
    {
        var trade=Trade();var report=Report([trade]);
        Assert.Throws<InvalidDataException>(()=>QuantDiagnosticEngine.Build(report,[],_=>null,"Holdout","TrendPullback","ScaleTail",1));
        Assert.Throws<InvalidDataException>(()=>QuantDiagnosticEngine.Build(Report([trade,trade]),[trade,trade],_=>null,"Holdout","TrendPullback","ScaleTail",1));
        Assert.Throws<InvalidDataException>(()=>QuantDiagnosticEngine.Build(report,[trade with{SourceHash="foreign"}],_=>null,"Holdout","TrendPullback","ScaleTail",1));
        Assert.Throws<ArgumentException>(()=>QuantDiagnosticEngine.Build(report,[trade],_=>null,"Holdout","TrendPullback","ScaleTail",0));
        Assert.Throws<InvalidDataException>(()=>QuantDiagnosticEngine.Build(report with{Request=report.Request with{StudyHash="changed"}},[trade],_=>null,"Holdout","TrendPullback","ScaleTail",1));
    }
    [Fact]public void ReadOnlyDiagnosticsUseTheNamedArchivedRunAndDoNotChangeStoredTradesOrReports()
    {
        var root=Path.Combine(Path.GetTempPath(),"Pullet-Diagnostic-"+Guid.NewGuid().ToString("N"));
        try
        {
            var repo=new QuantStudyRepository(Options.Create(new StorageOptions{Directory=root}));var a=Trade();var first=Report([a]);repo.SaveReport(first,[a]);
            var second=Report([]);repo.SaveReport(second,[]);var service=new QuantDiagnosticService(repo);var before=QuantRules.HashValue(repo.ReadTrades(first.Request.Id));
            var result=service.Read(first.Request.Id,"Holdout","TrendPullback","ScaleTail",1)!;Assert.Equal(1,result.Metrics.Completed);Assert.Same(result,service.Read(first.Request.Id,"Holdout","TrendPullback","ScaleTail",1));
            Assert.Null(service.Read(Guid.NewGuid(),"Holdout","TrendPullback","ScaleTail",1));Assert.Equal(before,QuantRules.HashValue(repo.ReadTrades(first.Request.Id)));Assert.Equal(second.Request.Id,repo.LatestReport()!.Request.Id);
            Assert.Equal(result.TradeHash,QuantRules.HashValue(repo.ReadTrades(first.Request.Id)));
        }
        finally{var full=Path.GetFullPath(root);if(!full.StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException();if(Directory.Exists(full))Directory.Delete(full,true);}
    }
}
