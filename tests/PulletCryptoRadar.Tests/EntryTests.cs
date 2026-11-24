using System.Text.Json;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;
using PulletCryptoRadar.Trading;

namespace PulletCryptoRadar.Tests;

public sealed class EntryTests
{
    private static readonly Guid Asset = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026,11,3,8,0,0,TimeSpan.Zero);
    private static HourCandle Bar(DateTimeOffset close,decimal open,decimal high,decimal low,decimal price) =>
        new(Asset,close.AddHours(-1),close,close,open,high,low,price,100,60);
    private static HourCandle[] History(decimal close = 101) => Enumerable.Range(0,61).Select(i => i == 60
        ? Bar(Now,100,close+1,99,close)
        : Bar(Now.AddHours(i-60),100,101,97,100)).ToArray();
    private static HourCandle[] Mirror(IReadOnlyList<HourCandle> bars) => bars.Select(bar => bar with
    {
        Open = 200-bar.Open, High = 200-bar.Low, Low = 200-bar.High, Close = 200-bar.Close,
        TakerBuyQuoteVolume = 40
    }).ToArray();
    private static ProviderRead Read(IReadOnlyList<HourCandle> bars,DateTimeOffset at) => new(new(Guid.NewGuid(),"Binance","TESTUSDT",
        "/fapi/v1/klines?symbol=TESTUSDT&interval=1h",at,200,CollectionFailure.None,"[]"),[],Candles:bars);
    private static FeatureSnapshot Features(DateTimeOffset at,TradeSide side)
    {
        var direction = side == TradeSide.Long ? 1 : -1;
        var pairs = new[] { (Metric.Ema20H1,100m),(Metric.Ema60H1,100m-2*direction),(Metric.Atr14H1,2m),
            (Metric.VolumeRatioH1,2m),(Metric.TakerBuyShareH1,side == TradeSide.Long ? .6m : .4m),
            (Metric.OiQuantityChange1h,2m),(Metric.Ema20H4,100m),(Metric.Ema60H4,100m-2*direction) };
        return new("test",Asset,at,MarketState.Mixed,pairs.Select(pair => new ResolvedMetric(pair.Item1,
            ResolutionStatus.SingleSource,pair.Item2,"Binance",true,
            [new(Asset,pair.Item1,pair.Item2,"Binance","test",pair.Item1 is Metric.Ema20H4 or Metric.Ema60H4
                ? DateTimeOffset.FromUnixTimeMilliseconds(at.ToUnixTimeMilliseconds()/14400000*14400000) : at,
                at,ObservationStatus.Valid,TimeQuality.ProviderTime)])).ToArray());
    }
    private static MarketContext Context(DateTimeOffset at) => new("test",at,MarketState.Mixed,100,100,1,.5m,0,[]);
    private static EntryEvaluation Evaluate(EntryStrategy strategy,TradeSide side,ProviderRead read,EntrySetup? previous = null,
        DateTimeOffset? now = null,FeatureSnapshot? snapshot = null,MarketContext? context = null) =>
        new EntryEngine().Evaluate(Asset,strategy,side,snapshot ?? Features(now ?? Now,side),read,
            context ?? Context(now ?? Now),previous,now ?? Now);

    [Theory]
    [InlineData(TradeSide.Long)]
    [InlineData(TradeSide.Short)]
    public void PullbackArmsThenRequiresDifferentClosedBarToConfirm(TradeSide side)
    {
        var bars = History();
        if (side == TradeSide.Short) bars = Mirror(bars);
        var first = Evaluate(EntryStrategy.TrendPullback,side,Read(bars,Now));
        Assert.Equal("Waiting",first.Status);
        Assert.NotNull(first.Setup);
        Assert.Equal(side == TradeSide.Long ? 96.8m : 103.2m,first.Setup.StructuralStop);
        var sameBar = Evaluate(EntryStrategy.TrendPullback,side,Read(bars,Now),first.Setup);
        Assert.Equal("Waiting",sameBar.Status);
        Assert.Contains("AwaitNextClosedBar",sameBar.Reasons);
        var next = History().Concat([Bar(Now.AddHours(1),101,104,100,103)]).ToArray();
        if (side == TradeSide.Short) next = Mirror(next);
        var confirmed = Evaluate(EntryStrategy.TrendPullback,side,Read(next,Now.AddHours(1)),first.Setup,Now.AddHours(1));
        Assert.Equal("PatternConfirmed",confirmed.Status);
        Assert.Equal(first.Setup.SetupId,confirmed.Setup!.SetupId);
        Assert.Equal(first.Setup.StructuralStop,confirmed.Setup.StructuralStop);
        Assert.Equal(Now.AddHours(1),confirmed.Setup.ConfirmedAt);
        Assert.False(confirmed.ExecutionAllowed);
        Assert.Contains("PortfolioRiskNotConnected",confirmed.Reasons);
    }

    [Theory]
    [InlineData(TradeSide.Long)]
    [InlineData(TradeSide.Short)]
    public void BreakoutNeedsLaterRetestAndRejectsChasingExtendedClose(TradeSide side)
    {
        var bars = History(102);
        if (side == TradeSide.Short) bars = Mirror(bars);
        var first = Evaluate(EntryStrategy.BreakoutRetest,side,Read(bars,Now));
        Assert.Equal("Waiting",first.Status);
        Assert.Equal(side == TradeSide.Long ? 101m : 99m,first.Setup!.Level);
        var next = History(102).Concat([Bar(Now.AddHours(1),101.2m,102,100.8m,101.5m)]).ToArray();
        if (side == TradeSide.Short) next = Mirror(next);
        var confirmed = Evaluate(EntryStrategy.BreakoutRetest,side,Read(next,Now.AddHours(1)),first.Setup,Now.AddHours(1));
        Assert.Equal("PatternConfirmed",confirmed.Status);
        var extended = History(102).Concat([Bar(Now.AddHours(1),104,106,104,105)]).ToArray();
        if (side == TradeSide.Short) extended = Mirror(extended);
        Assert.Equal("Waiting",Evaluate(EntryStrategy.BreakoutRetest,side,Read(extended,Now.AddHours(1)),first.Setup,Now.AddHours(1)).Status);
    }

    [Fact]
    public void StopBreachInAnyInterveningBarInvalidatesEvenAfterRecovery()
    {
        var first = Evaluate(EntryStrategy.TrendPullback,TradeSide.Long,Read(History(),Now));
        var bars = History().Concat([Bar(Now.AddHours(1),98,100,96,99),Bar(Now.AddHours(2),101,104,100,103)]).ToArray();
        var invalid = Evaluate(EntryStrategy.TrendPullback,TradeSide.Long,Read(bars,Now.AddHours(2)),first.Setup,Now.AddHours(2));
        Assert.Equal("Invalidated",invalid.Status);
        Assert.Contains("StructuralStopBreachedBeforeConfirmation",invalid.Reasons);
    }

    [Fact]
    public void SetupExpiresWithoutDataAndCanRearmOnlyAfterExpiryWasRecorded()
    {
        var first = Evaluate(EntryStrategy.TrendPullback,TradeSide.Long,Read(History(),Now));
        var expired = new EntryEngine().Evaluate(Asset,EntryStrategy.TrendPullback,TradeSide.Long,null,null,null,
            first.Setup,Now.AddHours(4));
        Assert.Equal("Expired",expired.Status);
        Assert.Equal(SetupPhase.Expired,expired.Setup!.Phase);
        var moved = History().Select(bar => bar with { OpenAt = bar.OpenAt.AddHours(4),ClosedAt = bar.ClosedAt.AddHours(4),
            ReceivedAt = bar.ReceivedAt.AddHours(4) }).ToArray();
        var rearmed = Evaluate(EntryStrategy.TrendPullback,TradeSide.Long,Read(moved,Now.AddHours(4)),expired.Setup,Now.AddHours(4));
        Assert.Equal("Waiting",rearmed.Status);
        Assert.NotEqual(first.Setup!.SetupId,rearmed.Setup!.SetupId);
    }

    [Theory]
    [InlineData("future")]
    [InlineData("foreign")]
    [InlineData("gap")]
    [InlineData("otherProvider")]
    [InlineData("old")]
    public void InvalidCandleEvidenceBlocksEntry(string condition)
    {
        var bars = History();
        var read = Read(bars,Now);
        if (condition == "future") bars[^1] = bars[^1] with { ReceivedAt = Now.AddSeconds(1) };
        if (condition == "foreign") bars[^1] = bars[^1] with { AssetId = Guid.NewGuid() };
        if (condition == "gap") bars[5] = bars[5] with { OpenAt = bars[5].OpenAt.AddHours(1),ClosedAt = bars[5].ClosedAt.AddHours(1) };
        if (condition == "otherProvider") read = read with { Evidence = read.Evidence with { Provider = "Other" } };
        var now = condition == "old" ? Now.AddHours(2) : Now;
        var result = Evaluate(EntryStrategy.TrendPullback,TradeSide.Long,read,now:now);
        Assert.Equal("Blocked",result.Status);
        Assert.Contains("ClosedBinanceCandlesUnavailable",result.Reasons);
    }

    [Fact]
    public void MissingParticipationAndFutureOrOpposingContextNeverConfirm()
    {
        var snapshot = Features(Now,TradeSide.Long);
        snapshot = snapshot with { Metrics = snapshot.Metrics.Where(metric => metric.Metric != Metric.OiQuantityChange1h).ToArray() };
        var blocked = Evaluate(EntryStrategy.TrendPullback,TradeSide.Long,Read(History(),Now),snapshot:snapshot);
        Assert.Contains("MissingOrStale:OiQuantityChange1h",blocked.Reasons);
        foreach (var context in new[] { Context(Now) with { State = MarketState.StrongBear },Context(Now.AddSeconds(1)),
            Context(Now.AddMinutes(-4)),Context(Now) with { Coverage = .5m } })
            Assert.Equal("Blocked",Evaluate(EntryStrategy.TrendPullback,TradeSide.Long,Read(History(),Now),context:context).Status);
    }

    [Fact]
    public void HourAndFourHourFeaturesMustMatchTheirClosedCandleBoundaries()
    {
        foreach (var metric in new[] { Metric.Ema20H1,Metric.Ema60H4 })
        {
            var snapshot = Features(Now,TradeSide.Long);
            snapshot = snapshot with { Metrics = snapshot.Metrics.Select(item => item.Metric == metric
                ? item with { Evidence = item.Evidence.Select(source => source with { SourceTime = Now.AddHours(-1) }).ToArray() } : item).ToArray() };
            var result = Evaluate(EntryStrategy.TrendPullback,TradeSide.Long,Read(History(),Now),snapshot:snapshot);
            Assert.Equal("Blocked",result.Status);
            Assert.Contains("MissingOrStale:"+metric,result.Reasons);
        }
    }

    [Fact]
    public void WeakParticipationDoesNotArmAndMismatchedPreviousStateIsRejected()
    {
        var snapshot = Features(Now,TradeSide.Long);
        snapshot = snapshot with { Metrics = snapshot.Metrics.Select(metric => metric.Metric == Metric.VolumeRatioH1
            ? metric with { Value = 1, Evidence = metric.Evidence.Select(source => source with { Value = 1 }).ToArray() } : metric).ToArray() };
        Assert.Equal("NoSetup",Evaluate(EntryStrategy.TrendPullback,TradeSide.Long,Read(History(),Now),snapshot:snapshot).Status);
        var first = Evaluate(EntryStrategy.TrendPullback,TradeSide.Long,Read(History(),Now));
        Assert.Throws<ArgumentException>(() => Evaluate(EntryStrategy.TrendPullback,TradeSide.Short,Read(History(),Now),first.Setup));
    }

    [Theory]
    [InlineData(TradeSide.Long,Metric.VolumeRatioH1,1.19,"VolumeRatioBelowThreshold")]
    [InlineData(TradeSide.Short,Metric.VolumeRatioH1,1.19,"VolumeRatioBelowThreshold")]
    [InlineData(TradeSide.Long,Metric.TakerBuyShareH1,.51,"DirectionalTakerBelow52Percent")]
    [InlineData(TradeSide.Short,Metric.TakerBuyShareH1,.49,"DirectionalTakerBelow52Percent")]
    [InlineData(TradeSide.Long,Metric.OiQuantityChange1h,.99,"OiGrowthBelow1Percent")]
    [InlineData(TradeSide.Short,Metric.OiQuantityChange1h,.99,"OiGrowthBelow1Percent")]
    public void ParticipationDiagnosticsIdentifyOnlyTheFailedComponent(TradeSide side,Metric failed,double value,string reason)
    {
        var snapshot=Features(Now,side);
        snapshot=snapshot with {Metrics=snapshot.Metrics.Select(item=>item.Metric==failed?item with
        {Value=(decimal)value,Evidence=item.Evidence.Select(source=>source with {Value=(decimal)value}).ToArray()}:item).ToArray()};
        var bars=side==TradeSide.Long?History():Mirror(History());
        var result=Evaluate(EntryStrategy.TrendPullback,side,Read(bars,Now),snapshot:snapshot);
        Assert.Equal("NoSetup",result.Status);Assert.Null(result.Setup);
        Assert.Contains("ParticipationInsufficient",result.Reasons);Assert.Contains(reason,result.Reasons);
        Assert.Single(result.Reasons,item=>item is "VolumeRatioBelowThreshold" or "DirectionalTakerBelow52Percent" or "OiGrowthBelow1Percent");
    }

    [Fact]
    public void CandleAndEvaluationEvidenceSurviveRestartAndReplayIdentically()
    {
        var folder = Path.Combine(Path.GetTempPath(),"v2-entry-"+Guid.NewGuid());
        try
        {
            var store = new FoundationStore(folder);
            store.Initialize(Now);
            store.Register(new(Asset,"TEST","Test","TESTUSDT",null,null,[],Now));
            var read = Read(History(),Now);
            var snapshot = Features(Now,TradeSide.Long);
            store.Save(snapshot,[read]);
            var engine = new EntryEngine();
            var result = engine.Evaluate(Asset,EntryStrategy.TrendPullback,TradeSide.Long,snapshot,read,Context(Now),null,Now);
            store.SaveEntry(result);
            store.SaveEntry(result);
            store = new FoundationStore(folder);
            Assert.Single(store.Entries(Asset));
            Assert.Equal(result,store.LatestEntry(Asset,EntryStrategy.TrendPullback,TradeSide.Long)! with { Reasons = result.Reasons });
            var replay = engine.Evaluate(Asset,EntryStrategy.TrendPullback,TradeSide.Long,store.Latest(Asset),store.LatestCandles(Asset),Context(Now),null,Now);
            Assert.Equal(JsonSerializer.Serialize(result),JsonSerializer.Serialize(replay));
            Assert.Throws<ArgumentException>(() => store.SaveEntry(result with { ExecutionAllowed = true }));
            Assert.Single(store.LatestEntries());
            store.SaveEntry(result with { AsOf = Now.AddMinutes(1), Status = "Blocked" });
            Assert.Equal("Blocked",Assert.Single(store.LatestEntries()).Status);
            Assert.Equal(2,store.Entries(Asset).Count);
        }
        finally
        {
            foreach (var file in Directory.GetFiles(folder)) File.Delete(file);
            Directory.Delete(folder);
        }
    }

    [Fact]
    public void ResearchThresholdsAreIsolatedAndDefaultEvaluationStaysIdentical()
    {
        var snapshot=Features(Now,TradeSide.Long);
        snapshot=snapshot with {Metrics=snapshot.Metrics.Select(item=>item.Metric==Metric.OiQuantityChange1h?item with
        {Value=.75m,Evidence=item.Evidence.Select(source=>source with {Value=.75m}).ToArray()}:item).ToArray()};
        var read=Read(History(),Now);var context=Context(Now);
        var normal=new EntryEngine().Evaluate(Asset,EntryStrategy.TrendPullback,TradeSide.Long,snapshot,read,context,null,Now);
        var explicitDefault=new EntryEngine(EntryThresholds.Production).Evaluate(Asset,EntryStrategy.TrendPullback,TradeSide.Long,snapshot,read,context,null,Now);
        Assert.Equal(JsonSerializer.Serialize(normal),JsonSerializer.Serialize(explicitDefault));
        var research=new EntryEngine(new("Test-Oi",OiGrowthPercent:.5m)).Evaluate(Asset,EntryStrategy.TrendPullback,TradeSide.Long,snapshot,read,context,null,Now);
        Assert.Equal("NoSetup",normal.Status);Assert.Equal("Waiting",research.Status);
        Assert.NotEqual(normal.Version,research.Version);Assert.False(research.ExecutionAllowed);
        Assert.Equal(1m,EntryThresholds.Production.OiGrowthPercent);
        Assert.Equal(JsonSerializer.Serialize(normal),JsonSerializer.Serialize(new EntryEngine().Evaluate(Asset,EntryStrategy.TrendPullback,TradeSide.Long,snapshot,read,context,null,Now)));
        Assert.Throws<ArgumentException>(()=>new EntryEngine(new("bad",DirectionalTaker:.49m)));
        Assert.Throws<ArgumentException>(()=>new EntryEngine(new("bad",OiGrowthPercent:-1)));
    }
    [Fact]
    public void ParticipationDiagnosticsSeparateArmingFromConfirmationWithoutChangingRules()
    {
        var first=Evaluate(EntryStrategy.TrendPullback,TradeSide.Long,Read(History(),Now));
        Assert.NotNull(first.Setup);
        var later=Now.AddHours(1);
        var weak=Features(later,TradeSide.Long);
        weak=weak with {Metrics=weak.Metrics.Select(item=>item.Metric==Metric.VolumeRatioH1?item with
        {Value=1,Evidence=item.Evidence.Select(source=>source with {Value=1}).ToArray()}:item).ToArray()};
        var bars=History().Concat([Bar(later,101,104,100,103)]).ToArray();
        var waiting=Evaluate(EntryStrategy.TrendPullback,TradeSide.Long,Read(bars,later),first.Setup,later,weak);
        Assert.Equal("Waiting",waiting.Status);Assert.Contains("ConfirmationParticipationInsufficient",waiting.Reasons);
        Assert.DoesNotContain("ArmingParticipationInsufficient",waiting.Reasons);Assert.Equal(first.Setup.SetupId,waiting.Setup!.SetupId);
        var unarmed=Evaluate(EntryStrategy.TrendPullback,TradeSide.Long,Read(bars,later),now:later,snapshot:weak);
        Assert.Equal("NoSetup",unarmed.Status);Assert.Contains("ArmingParticipationInsufficient",unarmed.Reasons);
        Assert.DoesNotContain("ConfirmationParticipationInsufficient",unarmed.Reasons);
    }
    [Theory]
    [InlineData(TradeSide.Long)]
    [InlineData(TradeSide.Short)]
    public void ResearchStagePolicyRetainsStrongArmingAndQualityButCanConfirmWithoutSecondImpulse(TradeSide side)
    {
        var engine=new EntryEngine(confirmationMode:EntryConfirmationMode.ArmedParticipationThenPrice);
        var bars=side==TradeSide.Long?History():Mirror(History());
        var armed=engine.Evaluate(Asset,EntryStrategy.TrendPullback,side,Features(Now,side),Read(bars,Now),Context(Now),null,Now);
        Assert.Equal("Waiting",armed.Status);Assert.NotNull(armed.Setup);
        var later=Now.AddHours(1);var next=History().Concat([Bar(later,101,104,100,103)]).ToArray();
        if(side==TradeSide.Short)next=Mirror(next);
        var weak=Features(later,side);
        weak=weak with {Metrics=weak.Metrics.Select(item=>
        {
            var value=item.Metric switch {Metric.VolumeRatioH1=>.8m,Metric.OiQuantityChange1h=>.1m,
                Metric.TakerBuyShareH1=>.5m,_=>item.Value};
            return item with {Value=value,Evidence=item.Evidence.Select(source=>source with {Value=value}).ToArray()};
        }).ToArray()};
        var confirmed=engine.Evaluate(Asset,EntryStrategy.TrendPullback,side,weak,Read(next,later),Context(later),armed.Setup,later);
        Assert.Equal("PatternConfirmed",confirmed.Status);Assert.False(confirmed.ExecutionAllowed);
        Assert.Contains("ConfirmationUsesArmedParticipation",confirmed.Reasons);Assert.NotEqual(EntryEngine.Version,confirmed.Version);
        var baseline=new EntryEngine().Evaluate(Asset,EntryStrategy.TrendPullback,side,weak,Read(next,later),Context(later),armed.Setup,later);
        Assert.Equal("Waiting",baseline.Status);Assert.Contains("ConfirmationParticipationInsufficient",baseline.Reasons);
        var unarmed=engine.Evaluate(Asset,EntryStrategy.TrendPullback,side,weak,Read(next,later),Context(later),null,later);
        Assert.Equal("NoSetup",unarmed.Status);Assert.Contains("ArmingParticipationInsufficient",unarmed.Reasons);
        var missing=weak with {Metrics=weak.Metrics.Where(item=>item.Metric!=Metric.OiQuantityChange1h).ToArray()};
        var blocked=engine.Evaluate(Asset,EntryStrategy.TrendPullback,side,missing,Read(next,later),Context(later),armed.Setup,later);
        Assert.Equal("Blocked",blocked.Status);Assert.Contains("MissingOrStale:OiQuantityChange1h",blocked.Reasons);
        var breached=next.ToArray();var stop=armed.Setup.StructuralStop;
        breached[^1]=side==TradeSide.Long?breached[^1] with {Low=stop-.1m}:breached[^1] with {High=stop+.1m};
        Assert.Equal("Invalidated",engine.Evaluate(Asset,EntryStrategy.TrendPullback,side,weak,Read(breached,later),Context(later),armed.Setup,later).Status);
        Assert.Throws<ArgumentException>(()=>new EntryEngine(confirmationMode:(EntryConfirmationMode)99));
    }
    [Theory]
    [InlineData(SetupPhase.PatternConfirmed)]
    [InlineData(SetupPhase.Invalidated)]
    public void FinishedPatternRearmIsNotCountedAsFailedWaitingConfirmation(SetupPhase phase)
    {
        var first=Evaluate(EntryStrategy.TrendPullback,TradeSide.Long,Read(History(),Now));
        var done=first.Setup! with {Phase=phase};
        var result=new EntryEngine().Evaluate(Asset,EntryStrategy.TrendPullback,TradeSide.Long,null,null,null,done,done.ExpiresAt);
        Assert.Equal("Expired",result.Status);Assert.Contains("TerminalPatternRearmWindowReached",result.Reasons);
        Assert.DoesNotContain("SetupTimedOut",result.Reasons);
    }
}
