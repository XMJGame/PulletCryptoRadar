using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;

namespace PulletCryptoRadar.IntegrationTests;

public sealed class NetworkUsageTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"Pullet-Network-"+Guid.NewGuid());
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026,10,29,10,0,0,TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow()=>Now;
    }
    private NetworkUsage Meter(Clock clock,long day=10,long month=30)=>new(Options.Create(new NetworkOptions{DailyBudgetBytes=day,MonthlyBudgetBytes=month}),Options.Create(new StorageOptions{Directory=root}),clock);
    private static JsonElement State(NetworkUsage meter)=>JsonSerializer.SerializeToElement(meter.Snapshot());
    [Fact] public void BudgetIsDurableAndBlocksWithoutResettingOnRestart()
    {
        var clock=new Clock();var meter=Meter(clock);meter.BeforeRequest();meter.Record(8);
        Assert.Throws<NetworkBudgetExceededException>(()=>meter.Record(2));
        var restored=Meter(clock);Assert.Throws<NetworkBudgetExceededException>(restored.BeforeRequest);
        Assert.Equal(10,State(restored).GetProperty("Usage").GetProperty("DayBytes").GetInt64());
    }
    [Fact] public void ShanghaiDayRolloverDoesNotClearMonthBudget()
    {
        var clock=new Clock{Now=new(2026,10,29,15,59,0,TimeSpan.Zero)};var meter=Meter(clock,15,20);meter.Record(12);
        clock.Now=clock.Now.AddMinutes(2);meter.BeforeRequest();
        Assert.Equal(0,State(meter).GetProperty("Usage").GetProperty("DayBytes").GetInt64());
        Assert.Throws<NetworkBudgetExceededException>(()=>meter.Record(8));
        clock.Now=new(2026,10,31,16,0,0,TimeSpan.Zero);meter.BeforeRequest();
        Assert.Equal(0,State(meter).GetProperty("Usage").GetProperty("MonthBytes").GetInt64());
    }
    [Fact] public void InvalidLedgerFailsClosed()
    {
        Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,"network-usage.json"),"bad json");
        Assert.Throws<JsonException>(()=>Meter(new Clock()));
    }
    private sealed class Source : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {Calls++;return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest){Content=new StringContent("1234567890")});}
    }
    [Fact] public async Task CountsErrorResponseAndRequestBodyWithoutChangingContent()
    {
        var meter=Meter(new Clock(),100,1000);var source=new Source();
        using var client=new HttpClient(new MeteredNetworkHandler(meter){InnerHandler=source});
        using var response=await client.PostAsync("https://example.invalid/test",new StringContent("abc"));
        Assert.Equal("1234567890",await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest,response.StatusCode);
        Assert.Equal(13,State(meter).GetProperty("Usage").GetProperty("DayBytes").GetInt64());
    }
    [Fact] public async Task ExhaustedBudgetNeverReachesTransport()
    {
        var meter=Meter(new Clock());Assert.Throws<NetworkBudgetExceededException>(()=>meter.Record(10));
        var source=new Source();using var client=new HttpClient(new MeteredNetworkHandler(meter){InnerHandler=source});
        await Assert.ThrowsAsync<NetworkBudgetExceededException>(()=>client.GetAsync("https://example.invalid/test"));Assert.Equal(0,source.Calls);
    }
    private sealed class PackedSource(byte[] bytes):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            var body=new ByteArrayContent(bytes);body.Headers.ContentEncoding.Add("gzip");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=body});
        }
    }
    [Fact] public async Task GzipIsChargedBeforeDecodingAndContentIsRestored()
    {
        var text=new string('x',10000);using var packed=new MemoryStream();
        using(var gzip=new System.IO.Compression.GZipStream(packed,System.IO.Compression.CompressionMode.Compress,true))
        {var bytes=System.Text.Encoding.UTF8.GetBytes(text);gzip.Write(bytes);}
        var payload=packed.ToArray();var meter=Meter(new Clock(),100000,1000000);
        using var client=new HttpClient(new MeteredNetworkHandler(meter){InnerHandler=new PackedSource(payload)});
        using var response=await client.GetAsync("https://example.invalid/test");
        Assert.Equal(text,await response.Content.ReadAsStringAsync());
        Assert.Empty(response.Content.Headers.ContentEncoding);
        Assert.Equal(payload.Length,State(meter).GetProperty("Usage").GetProperty("DayBytes").GetInt64());
    }
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
}
