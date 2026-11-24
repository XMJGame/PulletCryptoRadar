using PulletCryptoRadar.Core;
using PulletCryptoRadar.Features;
using PulletCryptoRadar.Infrastructure;

namespace PulletCryptoRadar.Tests;

public sealed class RadarTests
{
    private static readonly DateTimeOffset Now = new(2026,11,3,8,0,0,TimeSpan.Zero);
    private static readonly AssetIdentity Asset = new(Guid.NewGuid(),"TEST","Test","TESTUSDT",null,null,
        [new(ChainId.Bsc,"0x1111111111111111111111111111111111111111",18)],Now);
    private static FeatureSnapshot Snapshot(decimal change = 5, decimal volume = 1.3m, decimal taker = .54m,
        decimal oi1 = 2, decimal oi4 = 4)
    {
        var values = new[] { (Metric.Return24hClosed,change),(Metric.VolumeRatioH1,volume),(Metric.TakerBuyShareH1,taker),
            (Metric.OiQuantityChange1h,oi1),(Metric.OiQuantityChange4h,oi4),(Metric.MarketCap,100_000_000m),
            (Metric.DexLiquidity,1_000_000m),(Metric.Top10,50m),(Metric.TotalClusterPercent,25m) };
        return new("test",Asset.AssetId,Now,MarketState.Mixed,values.Select(pair =>
        {
            var provider = pair.Item1 switch { Metric.MarketCap => "CoinGecko",Metric.DexLiquidity => "DEXScreener",
                Metric.Top10 or Metric.TotalClusterPercent => "InsightX",_ => "Binance" };
            var chainMetric = pair.Item1 is Metric.DexLiquidity or Metric.Top10 or Metric.TotalClusterPercent;
            return new ResolvedMetric(pair.Item1,ResolutionStatus.SingleSource,pair.Item2,provider,true,
                [new(Asset.AssetId,pair.Item1,pair.Item2,provider,"test",Now,Now,ObservationStatus.Valid,TimeQuality.ProviderTime,
                    chainMetric ? ChainId.Bsc : null,chainMetric ? Asset.Chains[0].Address : null)]);
        }).ToArray());
    }
    private static RadarAssessment Assess(FeatureSnapshot? snapshot,DateTimeOffset? now = null) =>
        new RadarEngine().Calculate(Asset,snapshot,now ?? Now);

    [Theory]
    [InlineData(5,1.3,.54,2,4,RadarStage.Participation)]
    [InlineData(12,2,.6,2,4,RadarStage.Expansion)]
    [InlineData(60,4,.6,2,4,RadarStage.Overheated)]
    [InlineData(5,1,.54,2,4,RadarStage.Observing)]
    [InlineData(-12,2,.3,2,4,RadarStage.Observing)]
    [InlineData(12,2,.6,-2,4,RadarStage.Observing)]
    public void StagesRequireCombinedEvidence(double change,double volume,double taker,double oi1,double oi4,RadarStage stage)
    {
        var result = Assess(Snapshot((decimal)change,(decimal)volume,(decimal)taker,(decimal)oi1,(decimal)oi4));
        Assert.Equal(stage,result.Stage);
        Assert.Contains("ResearchOnlyNoTradeInstruction",result.Reasons);
    }

    [Theory]
    [InlineData(ResolutionStatus.Conflict)]
    [InlineData(ResolutionStatus.UnverifiedTime)]
    [InlineData(ResolutionStatus.Stale)]
    public void MissingOwnershipOrMarketCapNeverQualifiesLowCapResearch(ResolutionStatus status)
    {
        var snapshot = Snapshot();
        snapshot = snapshot with { Metrics = snapshot.Metrics.Select(item => item.Metric == Metric.MarketCap
            ? item with { Status = status, UsableForScoring = false } : item).ToArray() };
        var result = Assess(snapshot);
        Assert.False(result.LowCapResearchQualified);
        Assert.Contains("MarketCap",result.MissingEvidence);
        Assert.Equal(RadarStage.Participation,result.Stage);
    }

    [Fact]
    public void ExpiredOrForeignOrFutureEvidenceCannotCreateActiveStage()
    {
        Assert.Equal(RadarStage.InsufficientData,Assess(Snapshot(),Now.AddMinutes(11)).Stage);
        Assert.Equal(RadarStage.InsufficientData,Assess(Snapshot() with { AssetId = Guid.NewGuid() }).Stage);
        Assert.Equal(RadarStage.InsufficientData,Assess(Snapshot() with { AsOf = Now.AddSeconds(1) }).Stage);
        Assert.Equal(RadarStage.InsufficientData,Assess(null).Stage);
        var snapshot = Snapshot();
        snapshot = snapshot with { Metrics = snapshot.Metrics.Select(item => item.Metric == Metric.OiQuantityChange1h
            ? item with { Evidence = item.Evidence.Select(source => source with { SourceTime = Now.AddSeconds(1) }).ToArray() } : item).ToArray() };
        Assert.Equal(RadarStage.InsufficientData,Assess(snapshot).Stage);
    }

    [Fact]
    public void BenchmarksAreExcludedAndLargeCapOrUnknownChainDoesNotQualify()
    {
        Assert.Equal(RadarStage.Excluded,new RadarEngine().Calculate(Asset with { BinanceSymbol = "BTCUSDT" },Snapshot(),Now).Stage);
        Assert.False(new RadarEngine().Calculate(Asset with { Chains = [] },Snapshot(),Now).LowCapResearchQualified);
        var snapshot = Snapshot();
        snapshot = snapshot with { Metrics = snapshot.Metrics.Select(item => item.Metric == Metric.MarketCap
            ? item with { Value = 500_000_000, Evidence = item.Evidence.Select(source => source with { Value = 500_000_000 }).ToArray() }
            : item).ToArray() };
        Assert.False(Assess(snapshot).LowCapResearchQualified);
        Assert.Contains("AboveLowCapResearchLimit",Assess(snapshot).Reasons);
    }

    [Fact]
    public void NonBinanceTradingEvidenceCannotDriveRadarStage()
    {
        var snapshot = Snapshot();
        snapshot = snapshot with { Metrics = snapshot.Metrics.Select(item => item.Metric == Metric.VolumeRatioH1
            ? item with { Provider = "OtherExchange", Evidence = item.Evidence.Select(source => source with { Provider = "OtherExchange" }).ToArray() }
            : item).ToArray() };
        Assert.Equal(RadarStage.InsufficientData,Assess(snapshot).Stage);
        Assert.Contains("VolumeRatioH1",Assess(snapshot).MissingEvidence);
    }

    [Fact]
    public void EpisodesPersistAndOnlyStageOrQualificationChangesCreateEvents()
    {
        var folder = Path.Combine(Path.GetTempPath(),"v2-radar-"+Guid.NewGuid());
        try
        {
            var store = new FoundationStore(folder);
            store.Initialize(Now);
            store.Register(Asset);
            var initial = Assess(Snapshot());
            var episode = store.SaveRadar(initial);
            store = new FoundationStore(folder);
            Assert.Equal(episode.EpisodeId,store.SaveRadar(initial with { AsOf = Now.AddMinutes(1) }).EpisodeId);
            Assert.Single(store.RadarEvents(Asset.AssetId));
            var expansion = initial with { AsOf = Now.AddMinutes(2), Stage = RadarStage.Expansion };
            Assert.Equal(episode.EpisodeId,store.SaveRadar(expansion).EpisodeId);
            Assert.Equal(2,store.RadarEvents(Asset.AssetId).Count);
            store.SaveRadar(initial with { AsOf = Now.AddSeconds(1) });
            Assert.Equal(RadarStage.Expansion,Assert.Single(store.Radar()).Assessment.Stage);
            var restarted = store.SaveRadar(initial with { AsOf = Now.AddHours(7) });
            Assert.NotEqual(episode.EpisodeId,restarted.EpisodeId);
            Assert.Equal(3,store.RadarEvents(Asset.AssetId).Count);
            Assert.Equal(restarted.EpisodeId,Assert.Single(store.Radar()).EpisodeId);
        }
        finally
        {
            foreach (var file in Directory.GetFiles(folder)) File.Delete(file);
            Directory.Delete(folder);
        }
    }
}
