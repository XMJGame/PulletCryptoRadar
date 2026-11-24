using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Features;
using PulletCryptoRadar.Infrastructure;

namespace PulletCryptoRadar.Tests;

public sealed class HistoricalResearchTests:IDisposable
{
    private static readonly DateTimeOffset Month=new(2025,9,1,0,0,0,TimeSpan.Zero);
    private static readonly DateTimeOffset Now=new(2026,11,4,8,0,0,TimeSpan.Zero);
    private readonly string folder=Path.Combine(Path.GetTempPath(),"history-study-"+Guid.NewGuid());
    private static byte[] Zip(string name,string csv)
    {
        using var memory=new MemoryStream();
        using(var zip=new ZipArchive(memory,ZipArchiveMode.Create,true))
        {using var writer=new StreamWriter(zip.CreateEntry(name).Open());writer.Write(csv);}
        return memory.ToArray();
    }
    private static string Csv(DateTimeOffset? open=null)
    {
        var at=open??Month;
        return $"open_time,open,high,low,close,volume,close_time,quote_volume,count,taker_base,taker_quote,ignore\n{at.ToUnixTimeMilliseconds()},100,102,99,101,1,{at.AddHours(1).AddMilliseconds(-1).ToUnixTimeMilliseconds()},1000,5,1,600,0\n";
    }
    [Fact]
    public void ArchiveParserKeepsIdentityAndClosedTimeAndRejectsWrongFileOrMonth()
    {
        var bytes=Zip("TESTUSDT-1h-2025-09.csv",Csv());
        var row=Assert.Single(BinanceHistoricalArchive.Parse("TESTUSDT",Month,bytes,Now));
        Assert.Equal(BinanceIdentity.AssetId("TESTUSDT"),row.AssetId);Assert.Equal(Month.AddHours(1),row.ClosedAt);
        Assert.Equal(600,row.TakerBuyQuoteVolume);
        Assert.Throws<InvalidDataException>(()=>BinanceHistoricalArchive.Parse("OTHERUSDT",Month,bytes,Now));
        Assert.Throws<InvalidDataException>(()=>BinanceHistoricalArchive.Parse("TESTUSDT",Month.AddMonths(-1),bytes,Now));
        Assert.Throws<InvalidDataException>(()=>BinanceHistoricalArchive.Parse("TESTUSDT",Month,Zip("TESTUSDT-1h-2025-09.csv",Csv(Month.AddMonths(-1))),Now));
    }
    [Fact]
    public void ParserRejectsFutureBarsAndBadVolumeAndDuplicateBars()
    {
        Assert.Throws<InvalidDataException>(()=>BinanceHistoricalArchive.Parse("TESTUSDT",Month,Zip("TESTUSDT-1h-2025-09.csv",Csv()),Month));
        Assert.Throws<InvalidDataException>(()=>BinanceHistoricalArchive.Parse("TESTUSDT",Month,Zip("TESTUSDT-1h-2025-09.csv",Csv().Replace(",600,0",",1600,0")),Now));
        var csv=Csv()+Csv().Split('\n')[1];
        Assert.Throws<InvalidDataException>(()=>BinanceHistoricalArchive.Parse("TESTUSDT",Month,Zip("TESTUSDT-1h-2025-09.csv",csv),Now));
    }
    private sealed class Clock:TimeProvider {public override DateTimeOffset GetUtcNow()=>Now;}
    private sealed class Handler:HttpMessageHandler
    {
        public int Calls;public bool BadHash;
        public readonly byte[] Bytes=Zip("TESTUSDT-1h-2025-09.csv",Csv());
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            Calls++;var checksum=request.RequestUri!.AbsolutePath.EndsWith(".CHECKSUM",StringComparison.Ordinal);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=checksum?
                new StringContent((BadHash?new string('0',64):Convert.ToHexString(SHA256.HashData(Bytes)))+"  TESTUSDT-1h-2025-09.zip\n"):
                new ByteArrayContent(Bytes)});
        }
    }
    [Fact]
    public async Task OfficialChecksumCacheWorksOfflineAndTamperingIsRejected()
    {
        var database=new FoundationStore(folder);database.Initialize(Now);var handler=new Handler();using var http=new HttpClient(handler);
        var archive=new BinanceHistoricalArchive(http,database,new Clock(),Path.Combine(folder,"archive"));
        var first=await archive.CandlesAsync("TESTUSDT",Month,default);var second=await archive.CandlesAsync("TESTUSDT",Month,default);
        Assert.Equal(2,handler.Calls);Assert.Equal(first.DownloadedAt,second.DownloadedAt);Assert.Equal(first.Sha256,second.Sha256);
        File.WriteAllBytes(Path.Combine(folder,"archive","TESTUSDT-1h-2025-09.zip"),[1,2,3]);
        await Assert.ThrowsAsync<InvalidDataException>(()=>archive.CandlesAsync("TESTUSDT",Month,default));
        Assert.Equal(2,handler.Calls);
    }
    [Fact]
    public async Task WrongOfficialChecksumDoesNotWriteAcceptedArchive()
    {
        var database=new FoundationStore(folder);database.Initialize(Now);var handler=new Handler{BadHash=true};using var http=new HttpClient(handler);
        var archive=new BinanceHistoricalArchive(http,database,new Clock(),Path.Combine(folder,"archive"));
        await Assert.ThrowsAsync<InvalidDataException>(()=>archive.CandlesAsync("TESTUSDT",Month,default));
        Assert.Empty(Directory.GetFiles(Path.Combine(folder,"archive")));
    }
    private static HourCandle[] Bars(int count=1000)
    {
        return Enumerable.Range(0,count).Select(i=>new HourCandle(BinanceIdentity.AssetId("TESTUSDT"),Month.AddHours(i),Month.AddHours(i+1),
            Now,100,101,99,100,1000,600)).ToArray();
    }
    private static HistoricalSample Sample()=>new("TESTUSDT","Test",Month,Month.AddDays(10),"test");
    [Fact]
    public void MissingFutureWindowsStayMissingRatherThanUsingAvailablePartialResult()
    {
        var result=HistoricalResearchEngine.Outcome(Bars(100),60,7);
        Assert.False(result.Complete);Assert.Null(result.CloseReturnPercent);Assert.Null(result.MaximumDrawdownPercent);
        var gap=Bars();gap[100]=gap[100] with {OpenAt=gap[100].OpenAt.AddHours(1),ClosedAt=gap[100].ClosedAt.AddHours(1)};
        Assert.False(HistoricalResearchEngine.Outcome(gap,60,7).Complete);
    }
    [Fact]
    public void FuturePricesCannotChangeSignalSelectionAndIntrabarOrderIsNotInvented()
    {
        var bars=Bars();bars[61]=bars[61] with {Open=100,High=103,Low=99,Close=102,QuoteVolume=2000,TakerBuyQuoteVolume=1200};
        var engine=new HistoricalResearchEngine();var first=engine.Evaluate(Sample(),bars,[]);
        Assert.NotEmpty(first.Triggers);var changed=bars.Select((row,i)=>i>61?row with {Open=10,High=500,Low=1,Close=10}:row).ToArray();
        var second=engine.Evaluate(Sample(),changed,[]);
        Assert.Equal(first.Triggers[0].At,second.Triggers[0].At);Assert.Equal(102,first.Triggers[0].ReferenceClose);
        Assert.Equal(0,HistoricalResearchEngine.Outcome(Bars(),61,7).MaximumDrawdownPercent);
        Assert.Equal(-1,HistoricalResearchEngine.Outcome(Bars(),61,7).MaximumFallPercent);
    }
    [Fact]
    public void GapsAndMissingArchiveAreExplicitAndWrongIdentityRejected()
    {
        var engine=new HistoricalResearchEngine();var bars=Bars();
        Assert.Equal("PartialCoverage",engine.Evaluate(Sample(),bars,["missing month"]).Status);
        var removed=bars.Where((_,i)=>i!=70).ToArray();Assert.Equal(1,engine.Evaluate(Sample(),removed,[]).MissingHourlyIntervals);
        Assert.Throws<InvalidDataException>(()=>engine.Evaluate(Sample() with {Symbol="OTHERUSDT"},bars,[]));
    }
    public void Dispose(){if(Directory.Exists(folder))Directory.Delete(folder,true);}
}
