using System.Text.Json;
using Microsoft.Data.Sqlite;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;

namespace PulletCryptoRadar.Tests;

public sealed class RequestPriorityTests : IDisposable
{
    private static readonly DateTimeOffset Now=new(2026,11,4,12,0,0,TimeSpan.Zero);
    private readonly string folder=Path.Combine(Path.GetTempPath(),"radar-priority-"+Guid.NewGuid());
    private readonly FoundationStore store;
    public RequestPriorityTests(){store=new(folder);store.Initialize(Now);}
    [Fact]
    public async Task ConcurrentBackgroundCannotConsumeCoreAndCriticalMinuteReservations()
    {
        var decisions=await Task.WhenAll(Enumerable.Range(0,25).Select(_=>Task.Run(()=>
            new FoundationStore(folder).TryReserve(Guid.NewGuid(),"Binance",Now,100,RequestPriority.Background))));
        Assert.Equal(18,decisions.Count(item=>item.Allowed));
        Assert.All(decisions.Where(item=>!item.Allowed),item=>Assert.Equal("ReservedCriticalMinuteCapacity",item.Reason));
        Assert.Equal(0,store.CollectionCapacity(Now,RequestPriority.Background));
        Assert.Equal(6,store.CollectionCapacity(Now,RequestPriority.Core));
        for(var index=0;index<6;index++)Assert.True(store.TryReserve(Guid.NewGuid(),"Binance",Now,100,RequestPriority.Core).Allowed);
        Assert.False(store.TryReserve(Guid.NewGuid(),"Binance",Now,100,RequestPriority.Core).Allowed);
        for(var index=0;index<6;index++)Assert.True(store.TryReserve(Guid.NewGuid(),"Binance",Now,100,RequestPriority.Critical).Allowed);
        var rejected=store.TryReserve(Guid.NewGuid(),"Binance",Now,100,RequestPriority.Critical);
        Assert.Equal("ProviderMinuteLimit",rejected.Reason);Assert.True(rejected.RetryAt>Now);
        Assert.True(store.TryReserve(Guid.NewGuid(),"Binance",Now.AddSeconds(61),100,RequestPriority.Background).Allowed);
    }
    [Fact]
    public void TimeoutCooldownReasonAndRetrySurviveRestartAndSuccessfulRecoveryClearsCurrentFailure()
    {
        var id=Guid.NewGuid();Assert.True(store.Reserve(id,"Binance",Now,100));
        store.CompleteWithReason(id,0,Now.AddMinutes(1),"CooldownAfterTimeout");
        var reopened=new FoundationStore(folder);
        var decision=reopened.TryReserve(Guid.NewGuid(),"Binance",Now.AddSeconds(1),100,RequestPriority.Critical);
        Assert.False(decision.Allowed);Assert.Equal("CooldownAfterTimeout",decision.Reason);Assert.Equal(Now.AddMinutes(1),decision.RetryAt);
        Assert.Equal(0,reopened.CollectionCapacity(Now.AddSeconds(1),RequestPriority.Critical));
        var failed=new RawEvidence(Guid.NewGuid(),"Binance","TESTUSDT","/test",Now.AddSeconds(1),null,CollectionFailure.Quota,null,
            FailureDetail:decision.Reason,RetryAt:decision.RetryAt,Priority:RequestPriority.Critical);
        reopened.SaveResearchProbe(new(failed,[]));
        Assert.Equal("CooldownAfterTimeout",Assert.Single(reopened.SourceHealth(Now.AddSeconds(2))).FailureDetail);
        var later=Now.AddSeconds(61);Assert.True(reopened.TryReserve(Guid.NewGuid(),"Binance",later,100,RequestPriority.Critical).Allowed);
        reopened.SaveResearchProbe(new(new(Guid.NewGuid(),"Binance","TESTUSDT","/test",later,200,CollectionFailure.None,"{}"),[]));
        var health=Assert.Single(reopened.SourceHealth(later));
        Assert.Equal(CollectionFailure.None,health.Failure);Assert.Null(health.FailureDetail);Assert.Null(health.RetryAt);
        using var report=JsonDocument.Parse(JsonSerializer.Serialize(reopened.CollectionDiagnostics(later)));
        Assert.Equal(1,report.RootElement.GetProperty("denials24h")[0].GetProperty("count").GetInt32());
    }
    private void Usage(long bytes,DateTimeOffset at)
    {
        using var connection=new SqliteConnection(new SqliteConnectionStringBuilder {DataSource=Path.Combine(folder,"PulletCryptoRadar.db"),Pooling=false}.ToString());
        connection.Open();using var command=connection.CreateCommand();
        command.CommandText="INSERT INTO V2Request VALUES($id,'Binance',$at,$bytes,1)";
        command.Parameters.AddWithValue("$id",Guid.NewGuid().ToString());command.Parameters.AddWithValue("$at",at.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$bytes",bytes);command.ExecuteNonQuery();
    }
    [Theory]
    [InlineData(700000000,RequestPriority.Background,"ReservedCriticalDailyCapacity")]
    [InlineData(850000000,RequestPriority.Core,"ReservedCriticalDailyCapacity")]
    [InlineData(900000000,RequestPriority.Critical,"DailyPayloadBudget")]
    public void PayloadReservationsIncludeHigherPriorityDailyHeadroom(long usage,RequestPriority priority,string expected)
    {
        Usage(usage,Now.AddHours(-1));
        var decision=store.TryReserve(Guid.NewGuid(),"Binance",Now,1,priority);
        Assert.Equal(expected,decision.Reason);Assert.Equal(Now.UtcDateTime.Date.AddDays(1),decision.RetryAt!.Value.UtcDateTime);
        Assert.Equal(0,store.CollectionCapacity(Now,priority));
        if(priority!=RequestPriority.Critical)Assert.True(store.TryReserve(Guid.NewGuid(),"Binance",Now,1,priority+1).Allowed);
    }
    [Theory]
    [InlineData(24000000000,RequestPriority.Background,"ReservedCriticalMonthlyCapacity")]
    [InlineData(27000000000,RequestPriority.Core,"ReservedCriticalMonthlyCapacity")]
    [InlineData(30000000000,RequestPriority.Critical,"MonthlyPayloadBudget")]
    public void MonthlyReserveAndHardLimitAreDistinct(long usage,RequestPriority priority,string expected)
    {
        Usage(usage,Now.AddDays(-1));
        var decision=store.TryReserve(Guid.NewGuid(),"Binance",Now,1,priority);
        Assert.Equal(expected,decision.Reason);Assert.Equal(12,decision.RetryAt!.Value.Month);
    }
    [Fact]
    public void ProviderCallLimitIsNotReportedAsPayloadBudget()
    {
        for(var index=0;index<4;index++)Assert.True(store.TryReserve(Guid.NewGuid(),"InsightX",Now,1,RequestPriority.Background).Allowed);
        Assert.Equal("ProviderMinuteLimit",store.TryReserve(Guid.NewGuid(),"InsightX",Now,1,RequestPriority.Background).Reason);
    }
    [Fact]
    public void FailedJobRetriesEarlierWithoutDelayingAnExistingEarlierRetry()
    {
        Assert.True(store.ReserveJob("Metadata:test",Now,TimeSpan.FromHours(6)));
        store.RetryJobEarlier("Metadata:test",Now.AddMinutes(5));
        store.RetryJobEarlier("Metadata:test",Now.AddHours(1));
        Assert.False(store.ReserveJob("Metadata:test",Now.AddMinutes(4),TimeSpan.FromHours(6)));
        Assert.True(store.ReserveJob("Metadata:test",Now.AddMinutes(5),TimeSpan.FromHours(6)));
    }
    public void Dispose(){foreach(var file in Directory.GetFiles(folder))File.Delete(file);Directory.Delete(folder);}
}
