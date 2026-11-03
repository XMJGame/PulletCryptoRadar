using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;
namespace PulletCryptoRadar.IntegrationTests;

public sealed class HourlyWarmupTests
{
    private static readonly DateTimeOffset End=new(2026,11,1,10,0,0,TimeSpan.FromHours(8));
    private static Candle[] Bars()=>Enumerable.Range(0,800).Select(i=>new Candle("TESTUSDT",60,End.AddHours(i-800),100,101,99,100,1000,End.AddHours(i-799).AddMilliseconds(-1),End.AddHours(i-799))).ToArray();
    [Fact]public void RejectedReceptionInsideAFullBaselineRequestsEnoughHistoryInsteadOfDeclaringItComplete()
    {
        var rows=Bars();var index=rows.Length-66;rows[index]=rows[index] with{ReceivedTime=rows[index].SourceTime.AddMilliseconds(-15)};
        var before=QuantRules.HashValue(rows);var plan=HourlyWarmupEngine.Plan("TESTUSDT",rows,End,End.AddMinutes(1));
        Assert.True(plan.Required);Assert.Equal(1,plan.MissingHours);Assert.Equal(1,plan.RejectedRows);Assert.Equal(67,plan.FetchLimit);
        Assert.NotNull(QuantSignalEngine.Frame("TESTUSDT",rows,End.AddMinutes(1),new()).Issue);Assert.Equal(before,QuantRules.HashValue(rows));
    }
    [Fact]public void CompleteBaselineRequiresNoDownloadAndNewClosedHourNeedsOnlyTwoRows()
    {
        var rows=Bars();Assert.False(HourlyWarmupEngine.Plan("TESTUSDT",rows,End,End).Required);
        var plan=HourlyWarmupEngine.Plan("TESTUSDT",rows,End.AddHours(1),End.AddHours(1));Assert.True(plan.Required);Assert.Equal(2,plan.FetchLimit);
        Assert.Equal(800,HourlyWarmupEngine.Plan("TESTUSDT",rows.Where((_,i)=>i!=500).ToArray(),End,End).FetchLimit);
    }
    [Fact]public void WrongIdentityPeriodCloseTimeFutureReceptionAndConflictingValuesCannotRepairTheBaseline()
    {
        var good=Bars()[^1];var invalid=new[]{good with{Symbol="OTHER"},good with{IntervalMinutes=5},good with{OpenTime=good.OpenTime.AddTicks(1)},good with{SourceTime=good.EndTime},good with{ReceivedTime=End.AddSeconds(1)},good with{SourceTime=good.EndTime.AddSeconds(-3)},good with{ReceivedTime=good.SourceTime.AddMilliseconds(-1)}};
        Assert.Empty(HourlyWarmupEngine.Accept("TESTUSDT",invalid,End,End));
        Assert.Empty(HourlyWarmupEngine.Accept("TESTUSDT",[good,good with{Close=100.5m}],End,End));
        Assert.Single(HourlyWarmupEngine.Accept("TESTUSDT",[good,good],End,End));
    }
    [Fact]public void RejectedOldRowsOutsideTheRequiredWindowDoNotCauseRepeatedLargeDownloads()
    {
        var rows=Bars();rows[0]=rows[0] with{ReceivedTime=rows[0].SourceTime.AddMilliseconds(-1)};
        var plan=HourlyWarmupEngine.Plan("TESTUSDT",rows,End,End);Assert.False(plan.Required);Assert.Equal(0,plan.FetchLimit);
        Assert.Equal(800,HourlyWarmupEngine.Plan("TESTUSDT",[],End,End).FetchLimit);
    }
    [Fact]public async Task FreshSourceBackfillRepairsTheCacheWithoutChangingFrozenInputOrItsReceptionTimes()
    {
        var root=Path.Combine(Path.GetTempPath(),"Pullet-Hourly-Repair-"+Guid.NewGuid().ToString("N"));
        try
        {
            var db=new SqliteDatabase(Options.Create(new StorageOptions{Directory=root}));db.Initialize();var repository=new AssetRepository(db);
            var original=Bars();var index=original.Length-66;var damaged=original.ToArray();damaged[index]=damaged[index] with{ReceivedTime=damaged[index].SourceTime.AddMilliseconds(-15)};
            var shadow=new QuantShadowRepository(Options.Create(new StorageOptions{Directory=root}));var rules=QuantRules.Hash(new());var input=new QuantInputBundle("TESTUSDT",End,damaged,[]);var digest=shadow.SaveInput(rules,input);
            db.Save(damaged.Select(c=>(1,c,(decimal?)null,(decimal?)null)).ToArray(),[]);
            var saved=await repository.ReadHourlyHistoryAsync(1,800,default);Assert.True(HourlyWarmupEngine.Plan("TESTUSDT",saved,End,End).Required);
            var replacement=original[index] with{ReceivedTime=End.AddMinutes(1)};db.Save([(1,replacement,null,null)],[]);
            saved=await repository.ReadHourlyHistoryAsync(1,800,default);Assert.False(HourlyWarmupEngine.Plan("TESTUSDT",saved,End,End.AddMinutes(2)).Required);
            Assert.Null(QuantSignalEngine.Frame("TESTUSDT",saved,End.AddMinutes(2),new()).Issue);
            Assert.Equal(digest,QuantRules.HashValue(shadow.ReadInput(rules,digest)!));Assert.Equal(damaged[index].ReceivedTime,shadow.ReadInput(rules,digest)!.Candles[index].ReceivedTime);
            Assert.NotNull(QuantSignalEngine.Frame("TESTUSDT",shadow.ReadInput(rules,digest)!.Candles,End.AddMinutes(2),new()).Issue);
        }
        finally
        {
            SqliteConnection.ClearAllPools();var full=Path.GetFullPath(root);if(!full.StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException();if(Directory.Exists(full))Directory.Delete(full,true);
        }
    }
}
