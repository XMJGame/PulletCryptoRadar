using System.Text.Json;
using Microsoft.Data.Sqlite;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Features;
using PulletCryptoRadar.Infrastructure;
using PulletCryptoRadar.DataFoundation;

namespace PulletCryptoRadar.Tests;

public sealed class OpenInterestHistoryTests
{
    private static readonly Guid Asset = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026,11,3,8,0,0,TimeSpan.Zero);
    private static readonly AssetIdentity Identity = new(Asset,"BTC","Bitcoin","BTCUSDT",null,null,[],Now);
    private static OpenInterestPoint[] Points() => Enumerable.Range(0,289).Select(i =>
        new OpenInterestPoint(Asset,Now.AddMinutes((i-288)*5),Now,100+i,1000+i*20)).ToArray();
    private static Observation MetricValue(IReadOnlyList<OpenInterestPoint> points, Metric metric) =>
        Assert.Single(new OpenInterestFeatureEngine().Calculate(Asset,points,Now),item => item.Metric == metric);

    [Fact]
    public void ComputesIndependentQuantityAndNotionalPercentChanges()
    {
        var values = new OpenInterestFeatureEngine().Calculate(Asset,Points(),Now);
        decimal? Value(Metric metric) => Assert.Single(values,item => item.Metric == metric).Value;
        Assert.Equal((388m/376-1)*100,Value(Metric.OiQuantityChange1h));
        Assert.Equal((388m/340-1)*100,Value(Metric.OiQuantityChange4h));
        Assert.Equal(288m,Value(Metric.OiQuantityChange24h));
        Assert.Equal(576m,Value(Metric.OiNotionalChange24h));
        Assert.All(values,item => Assert.Equal(Now,item.SourceTime));
    }

    [Fact]
    public void PriceOnlyIncreaseDoesNotInventQuantityGrowthAndDeclinesRemainSigned()
    {
        var points = Points().Select(point => point with { Quantity = 100 }).ToArray();
        Assert.Equal(0m,MetricValue(points,Metric.OiQuantityChange24h).Value);
        Assert.Equal(576m,MetricValue(points,Metric.OiNotionalChange24h).Value);
        points[^1] = points[^1] with { Quantity = 90 };
        var negative = MetricValue(points,Metric.OiQuantityChange1h);
        Assert.Equal(-10m,negative.Value);
        var resolved = new CanonicalResolver().Resolve(Asset,negative.Metric,[negative],Now,TimeSpan.FromMinutes(10),"Binance");
        Assert.True(resolved.UsableForScoring);
        Assert.Throws<ArgumentException>(() => new CanonicalResolver().Resolve(Asset,negative.Metric,[negative],Now,
            TimeSpan.FromMinutes(10),"Other"));
    }

    [Fact]
    public void GapsInvalidateOnlyAffectedWindowsAndAreNeverFilled()
    {
        var points = Points().Where((_,i) => i != 2).ToArray();
        Assert.NotNull(MetricValue(points,Metric.OiQuantityChange1h).Value);
        Assert.NotNull(MetricValue(points,Metric.OiQuantityChange4h).Value);
        Assert.Null(MetricValue(points,Metric.OiQuantityChange24h).Value);
        points = Points().Where((_,i) => i != 280).ToArray();
        Assert.All(new OpenInterestFeatureEngine().Calculate(Asset,points,Now),item => Assert.Null(item.Value));
    }

    [Fact]
    public void ZeroBaselineShortHistoryAndStaleTailYieldMissingValues()
    {
        var points = Points();
        points[0] = points[0] with { Quantity = 0 };
        Assert.Null(MetricValue(points,Metric.OiQuantityChange24h).Value);
        Assert.NotNull(MetricValue(points,Metric.OiNotionalChange24h).Value);
        Assert.Null(MetricValue(points.TakeLast(12).ToArray(),Metric.OiQuantityChange1h).Value);
        Assert.All(new OpenInterestFeatureEngine().Calculate(Asset,points,Now.AddMinutes(11)),item => Assert.Null(item.Value));
        Assert.All(new OpenInterestFeatureEngine().Calculate(Asset,[],Now),item => Assert.Equal(ObservationStatus.Missing,item.Status));
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("futureReceipt")]
    [InlineData("futureSource")]
    [InlineData("negative")]
    [InlineData("unordered")]
    public void RejectsNonCausalOrInvalidFeatureEvidence(string condition)
    {
        var points = Points();
        points[^1] = condition switch
        {
            "foreign" => points[^1] with { AssetId = Guid.NewGuid() },
            "futureReceipt" => points[^1] with { ReceivedTime = Now.AddSeconds(1) },
            "futureSource" => points[^1] with { SourceTime = Now.AddMinutes(5) },
            "negative" => points[^1] with { NotionalUsdt = -1 },
            _ => points[^2]
        };
        Assert.Throws<ArgumentException>(() => new OpenInterestFeatureEngine().Calculate(Asset,points,Now));
    }

    [Fact]
    public void ParserKeepsRealGapsButRejectsForeignDuplicateFutureAndNegativeRows()
    {
        object Row(DateTimeOffset at,string symbol = "BTCUSDT",string quantity = "100") =>
            new { symbol, sumOpenInterest = quantity, sumOpenInterestValue = "1000", timestamp = at.ToUnixTimeMilliseconds() };
        var parsed = BinanceMarketProvider.ParseOpenInterestHistory(Identity,
            JsonSerializer.Serialize(new[] { Row(Now.AddMinutes(-10)),Row(Now) }),Now);
        Assert.Equal(2,parsed.Count);
        foreach (var rows in new[] { new[] { Row(Now,"ETHUSDT") },new[] { Row(Now),Row(Now) },
            new[] { Row(Now.AddMinutes(5)) },new[] { Row(Now,quantity:"-1") } })
            Assert.Throws<InvalidDataException>(() => BinanceMarketProvider.ParseOpenInterestHistory(Identity,JsonSerializer.Serialize(rows),Now));
    }

    [Fact]
    public void RawSeriesSurvivesStorageAndReplaysExactlyAtOriginalReceiptTime()
    {
        var folder = Path.Combine(Path.GetTempPath(),"v2-oi-replay-"+Guid.NewGuid());
        try
        {
            var store = new FoundationStore(folder);
            store.Initialize(Now);
            store.Register(Identity);
            var values = new OpenInterestFeatureEngine().Calculate(Asset,Points(),Now);
            var read = new ProviderRead(new(Guid.NewGuid(),"Binance","BTCUSDT","/futures/data/openInterestHist",Now,
                200,CollectionFailure.None,"[]"),values,OpenInterestHistory:Points());
            var metrics = values.Select(item => new CanonicalResolver().Resolve(Asset,item.Metric,values,Now,
                TimeSpan.FromMinutes(10),"Binance")).ToArray();
            store.Save(new FeatureSnapshotBuilder().Build(Asset,Now,metrics),[read]);
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
                DataSource = Path.Combine(folder,"PulletCryptoRadar.db"), Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Payload FROM V2RawEvidence";
            var restored = JsonSerializer.Deserialize<ProviderRead>((string)command.ExecuteScalar()!)!;
            var replayed = new OpenInterestFeatureEngine().Calculate(Asset,restored.OpenInterestHistory!,Now);
            Assert.Equal(JsonSerializer.Serialize(values),JsonSerializer.Serialize(replayed));
            Assert.Throws<ArgumentException>(() => new OpenInterestFeatureEngine().Calculate(Asset,
                restored.OpenInterestHistory!,Now.AddMinutes(-1)));
        }
        finally
        {
            foreach (var file in Directory.GetFiles(folder)) File.Delete(file);
            Directory.Delete(folder);
        }
    }
}
