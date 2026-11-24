using PulletCryptoRadar.Core;
using PulletCryptoRadar.Features;
using PulletCryptoRadar.Infrastructure;

namespace PulletCryptoRadar.Tests;

public sealed class RadarAuditTests
{
    private static readonly DateTimeOffset Now = new(2026,11,3,14,0,0,TimeSpan.Zero);
    private const string Address = "0x1111111111111111111111111111111111111111";
    private static AssetIdentity Asset(string symbol = "TEST") => new(Guid.NewGuid(),symbol,symbol,symbol+"USDT",null,null,[],Now);
    private static FeatureSnapshot Snapshot(AssetIdentity asset)
    {
        var values = new[] { (Metric.MarketCap,10_000_000m),(Metric.DexLiquidity,1_000_000m),(Metric.Top10,40m),
            (Metric.TotalClusterPercent,20m),(Metric.Return24hClosed,5m),(Metric.VolumeRatioH1,1.3m),
            (Metric.TakerBuyShareH1,.54m),(Metric.OiQuantityChange1h,2m),(Metric.OiQuantityChange4h,4m) };
        return new("test",asset.AssetId,Now,MarketState.Mixed,values.Select(pair =>
        {
            var provider = pair.Item1 switch { Metric.MarketCap=>"CoinGecko",Metric.DexLiquidity=>"DEXScreener",
                Metric.Top10 or Metric.TotalClusterPercent=>"InsightX",_=>"Binance" };
            var chain = pair.Item1 is Metric.DexLiquidity or Metric.Top10 or Metric.TotalClusterPercent;
            return new ResolvedMetric(pair.Item1,ResolutionStatus.SingleSource,pair.Item2,provider,true,
                [new(asset.AssetId,pair.Item1,pair.Item2,provider,"test",Now,Now,ObservationStatus.Valid,TimeQuality.ProviderTime,
                    chain ? ChainId.Bsc : null,chain ? Address : null)]);
        }).ToArray());
    }
    [Fact]
    public void FullContractScanDoesNotBecomeHundredsOfResearchCandidates()
    {
        var assets = Enumerable.Range(0,519).Select(index=>Asset("T"+index)).ToArray();
        var universe = assets.Select(asset=>new MarketInstrument(asset.BinanceSymbol,asset.Symbol,Now))
            .Concat([new("BTCUSDT","BTC",Now),new("ETHUSDT","ETH",Now)]).ToArray();
        var board = new RadarBoardBuilder().Build(assets,[],universe,Now);
        Assert.Equal(519,board.Coverage.ScannedContracts);
        Assert.Equal(519,board.Coverage.UnmappedIdentities);
        Assert.Equal(0,board.Coverage.DataQualified);
        Assert.Empty(board.Research); Assert.Empty(board.Activity); Assert.Empty(board.DataChecks);
    }
    [Fact]
    public void StaleActivityAndInactiveContractsNeverAppearAsCurrentCandidates()
    {
        var asset = Asset() with { Chains=[new(ChainId.Bsc,Address,18)],CoinGeckoId="test" };
        var universe = new[] { new MarketInstrument(asset.BinanceSymbol,asset.Symbol,Now) };
        var board = new RadarBoardBuilder().Build([asset],[Snapshot(asset)],universe,Now.AddMinutes(11));
        Assert.Empty(board.Research); Assert.Empty(board.Activity);
        Assert.Equal(1,board.Coverage.ActivityStale);
        Assert.Equal("StaleOrInvalid",Assert.Single(board.DataChecks).Metrics.Single(item=>item.Metric=="OiQuantityChange1h").Status);
        Assert.Empty(new RadarBoardBuilder().Build([asset],[Snapshot(asset)],[],Now).Research);
    }
    [Fact]
    public void ProjectReviewRequiresTheExactChainAndContract()
    {
        var asset = Asset("AIO") with { Chains=[new(ChainId.Bsc,"0x81a7da4074b8e0ed51bea40f9dcbdf4d9d4832b4",18)],CoinGeckoId="olaxbt" };
        var universe = new[] { new MarketInstrument(asset.BinanceSymbol,asset.Symbol,Now) };
        var builder = new RadarBoardBuilder();
        var reviewed = Assert.Single(builder.Build([asset],[Snapshot(asset)],universe,Now).EarlyWatch);
        Assert.NotNull(reviewed.LongHoldReview);
        var sameTicker = asset with { Chains=[new(ChainId.Bsc,Address,18)] };
        var unreviewed = Assert.Single(builder.Build([sameTicker],[Snapshot(sameTicker)],universe,Now).EarlyWatch);
        Assert.Null(unreviewed.LongHoldReview);
    }
    [Fact]
    public void EarlyWatchSurfacesVerifiedSmallCapBeforeActivityOrHolderData()
    {
        var asset = Asset() with { Chains=[new(ChainId.Bsc,Address,18)],CoinGeckoId="test" };
        var universe = new[] { new MarketInstrument(asset.BinanceSymbol,asset.Symbol,Now) };
        var snapshot = Snapshot(asset);
        snapshot = snapshot with { Metrics=snapshot.Metrics.Where(item=>item.Metric==Metric.MarketCap).ToArray() };
        var builder = new RadarBoardBuilder();
        var board = builder.Build([asset],[snapshot],universe,Now);
        Assert.Equal(asset.AssetId,Assert.Single(board.EarlyWatch).AssetId);
        Assert.Empty(board.Research);
        Assert.Empty(board.Activity);
        Assert.Empty(builder.Build([asset],[snapshot],universe,Now.AddHours(9)).EarlyWatch);
    }
    [Fact]
    public void RealProviderAndExactContractAreRequiredForResearchQualification()
    {
        var asset = Asset() with { Chains=[new(ChainId.Bsc,Address,18)],CoinGeckoId="test" };
        var snapshot = Snapshot(asset);
        Assert.True(new RadarEngine().Calculate(asset,snapshot,Now).LowCapResearchQualified);
        var wrongProvider = snapshot with { Metrics=snapshot.Metrics.Select(item=>item.Metric==Metric.MarketCap ?
            item with {Provider="Binance",Evidence=item.Evidence.Select(source=>source with {Provider="Binance"}).ToArray()} : item).ToArray() };
        Assert.False(new RadarEngine().Calculate(asset,wrongProvider,Now).LowCapResearchQualified);
        var wrongContract = snapshot with { Metrics=snapshot.Metrics.Select(item=>item.Metric==Metric.Top10 ?
            item with { Evidence=item.Evidence.Select(source=>source with {Contract="0x2222222222222222222222222222222222222222"}).ToArray()} : item).ToArray() };
        Assert.False(new RadarEngine().Calculate(asset,wrongContract,Now).LowCapResearchQualified);
    }
    [Fact]
    public void KnownIdentityAndUnverifiedTimestampAreDifferentFromMissingIdentity()
    {
        var asset = Asset() with { Chains=[new(ChainId.Bsc,Address,18)],CoinGeckoId="test" };
        var snapshot = Snapshot(asset);
        snapshot = snapshot with { Metrics=snapshot.Metrics.Select(item=>item.Metric==Metric.DexLiquidity ?
            item with {Status=ResolutionStatus.UnverifiedTime,UsableForScoring=false} : item).ToArray() };
        var row = new RadarBoardBuilder().Row(asset,snapshot,Now);
        Assert.False(row.DataQualified);
        Assert.Equal("UnverifiedTime",row.Metrics.Single(item=>item.Metric=="DexLiquidity").Status);
        Assert.Equal("IdentityMissing",new RadarBoardBuilder().Row(Asset(),null,Now).Metrics.Single(item=>item.Metric=="MarketCap").Status);
        var sol = asset with { Chains=[new(ChainId.Base,Address,18)] };
        Assert.Equal("CollectorUnsupportedChain",new RadarBoardBuilder().Row(sol,null,Now).Metrics.Single(item=>item.Metric=="Top10").Status);
    }
    [Fact]
    public void DistributionZeroPlaceholderIsMissingAndPositiveCoverageKeepsExactChain()
    {
        var asset = Asset() with { Chains=[new(ChainId.Bsc,Address,18)] };
        var zero = SupplementaryProvider.Parse(asset,"Distribution","""{"gini":0,"hhi":0,"nakamoto":0,"top_10_holder_concentration":0}""",Now,Now,Guid.NewGuid());
        Assert.All(zero.Observations,item=>{Assert.Null(item.Value);Assert.Equal(ObservationStatus.Missing,item.Status);});
        var positive = SupplementaryProvider.Parse(asset,"Distribution","""{"gini":0.8,"hhi":0.2,"nakamoto":3,"top_10_holder_concentration":65}""",Now,Now,Guid.NewGuid());
        Assert.All(positive.Observations,item=>{Assert.Equal(ChainId.Bsc,item.Chain);Assert.Equal(Address,item.Contract);Assert.Null(item.SourceTime);});
    }
    [Fact]
    public void ClusterTotalAndRepeatedWalletsCannotDoubleCountSupply()
    {
        var asset = Asset() with { Chains=[new(ChainId.Bsc,Address,18)] };
        var body = $$"""{"total_cluster_pct":30,"clusters":[{"pct":20,"tags":[],"cluster_addresses":[{"address":"{{Address}}","percentage":20}]}]}""";
        Assert.Throws<InvalidDataException>(()=>SupplementaryProvider.Parse(asset,"Clusters",body,Now,Now,Guid.NewGuid()));
        var duplicate = $$"""{"total_cluster_pct":40,"clusters":[{"pct":20,"tags":[],"cluster_addresses":[{"address":"{{Address}}","percentage":20}]},{"pct":20,"tags":[],"cluster_addresses":[{"address":"{{Address}}","percentage":20}]}]}""";
        Assert.Throws<InvalidDataException>(()=>SupplementaryProvider.Parse(asset,"Clusters",duplicate,Now,Now,Guid.NewGuid()));
    }
}
