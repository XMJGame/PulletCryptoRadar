using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;

namespace PulletCryptoRadar.Tests;

public sealed class TrafficEfficiencyTests
{
    private sealed class Clock:TimeProvider
    {
        public DateTimeOffset At=new(2026,11,4,10,1,0,TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow()=>At;
    }
    private static ProviderRead Candles(AssetIdentity asset,DateTimeOffset boundary,DateTimeOffset received)=>
        new(new(Guid.NewGuid(),"Binance",asset.BinanceSymbol,"/fapi/v1/klines?symbol="+asset.BinanceSymbol+"&interval=1h&limit=300",
            received,200,CollectionFailure.None,"synthetic-candle-fixture"),[],Candles:Enumerable.Range(-300,300).Select(i=>
                new HourCandle(asset.AssetId,boundary.AddHours(i),boundary.AddHours(i+1),received,100,101,99,100,1000,600)).ToArray());
    [Fact]
    public async Task SameClosedHourSharesOneFetchAcrossCallersAndRestartWithoutRefreshingEvidenceTime()
    {
        var folder=Path.Combine(Path.GetTempPath(),"candle-cache-"+Guid.NewGuid());
        try
        {
            var clock=new Clock();var store=new FoundationStore(folder);store.Initialize(clock.At);
            var asset=new AssetIdentity(Guid.NewGuid(),"TEST","Test","TESTUSDT",null,null,[],clock.At);store.Register(asset);
            var cache=new ClosedHourCandleCache(store,clock);var calls=0;var boundary=clock.At.AddMinutes(-1);
            Task<ProviderRead> Fetch(){calls++;return Task.FromResult(Candles(asset,boundary,clock.At));}
            var reads=await Task.WhenAll(Enumerable.Range(0,8).Select(_=>cache.ReadAsync(asset,Fetch,default)));
            Assert.Equal(1,calls);Assert.Single(reads.Select(read=>read.Evidence.Id).Distinct());
            var original=reads[0];clock.At=clock.At.AddMinutes(30);
            var restarted=new ClosedHourCandleCache(new FoundationStore(folder),clock);
            var cached=await restarted.ReadAsync(asset,Fetch,default);
            Assert.Equal(1,calls);Assert.Equal(original.Evidence,cached.Evidence);
            Assert.Equal(original.Candles![^1].ReceivedAt,cached.Candles![^1].ReceivedAt);
            clock.At=boundary.AddHours(1);boundary=clock.At;
            var next=await restarted.ReadAsync(asset,Fetch,default);
            Assert.Equal(2,calls);Assert.NotEqual(original.Evidence.Id,next.Evidence.Id);
            Assert.Equal(clock.At,next.Candles![^1].ClosedAt);
            clock.At=clock.At.AddHours(-2);
            Assert.False(ClosedHourCandleCache.IsCurrent(next,asset,clock.At));
            Assert.False(ClosedHourCandleCache.IsCurrent(next with {Evidence=next.Evidence with {Failure=CollectionFailure.Transport}},asset,clock.At.AddHours(2)));
            var gap=next.Candles.ToArray();gap[5]=gap[5] with {OpenAt=gap[5].OpenAt.AddHours(1),ClosedAt=gap[5].ClosedAt.AddHours(1)};
            Assert.False(ClosedHourCandleCache.IsCurrent(next with {Candles=gap},asset,clock.At.AddHours(2)));
        }
        finally{if(Directory.Exists(folder))Directory.Delete(folder,true);}
    }

    [Theory]
    [InlineData("gzip")]
    [InlineData("deflate")]
    [InlineData("br")]
    public async Task TransportNegotiatesCompressionAndReturnsDecodedPayloadThroughRealHandler(string encoding)
    {
        var body="{\"fixture\":\""+new string('x',20000)+"\"}";
        using var packed=new MemoryStream();
        using(Stream encoder=encoding switch
        {
            "gzip"=>new GZipStream(packed,CompressionLevel.SmallestSize,true),
            "deflate"=>new ZLibStream(packed,CompressionLevel.SmallestSize,true),
            _=>new BrotliStream(packed,CompressionLevel.SmallestSize,true)
        }){encoder.Write(Encoding.UTF8.GetBytes(body));}
        var bytes=packed.ToArray();Assert.True(bytes.Length<Encoding.UTF8.GetByteCount(body));
        using var cancellation=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        var port=((IPEndPoint)listener.LocalEndpoint).Port;
        var server=Task.Run(async()=>
        {
            using var client=await listener.AcceptTcpClientAsync(cancellation.Token);
            await using var stream=client.GetStream();using var reader=new StreamReader(stream,Encoding.ASCII,false,1024,true);
            var request=new StringBuilder();string? line;
            while(!string.IsNullOrEmpty(line=await reader.ReadLineAsync(cancellation.Token)))request.AppendLine(line);
            var headers=Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {bytes.Length}\r\nContent-Encoding: {encoding}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(headers,cancellation.Token);await stream.WriteAsync(bytes,cancellation.Token);
            return request.ToString();
        },cancellation.Token);
        using var handler=HttpTransport.Create(false);using var http=new HttpClient(handler){Timeout=TimeSpan.FromSeconds(5)};
        using var response=await http.GetAsync($"http://127.0.0.1:{port}/fixture",HttpCompletionOption.ResponseHeadersRead,cancellation.Token);
        Assert.Equal(body,await response.Content.ReadAsStringAsync(cancellation.Token));
        var requestHeaders=await server;
        Assert.Contains("Accept-Encoding:",requestHeaders,StringComparison.OrdinalIgnoreCase);
        Assert.Contains(encoding,requestHeaders,StringComparison.OrdinalIgnoreCase);
        Assert.False(handler.AllowAutoRedirect);Assert.False(handler.UseProxy);
        using var proxied=HttpTransport.Create(true);Assert.True(proxied.UseProxy);
    }
}
