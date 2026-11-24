using System.Text.Json;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;

namespace PulletCryptoRadar.Host;

public static class QuantDatasetCommand
{
    // Declared before outcomes are evaluated; no adaptive selection based on the results.
    public static readonly string[] Symbols=["MYXUSDT","TRBUSDT"];
    public static readonly (DateTimeOffset Month,string Partition)[] Windows=
        [(new(2025,9,1,0,0,0,TimeSpan.Zero),"Development"),(new(2026,1,1,0,0,0,TimeSpan.Zero),"ChronologicalHoldout")];
    public static Task<int> RunAsync(string directory)=>CollectAsync(directory,Symbols,Windows);
    public static Task<int> RunExpansionAsync(string directory,bool holdout)
    {
        QuantExpansionCommand.FreezePlan(directory);
        return CollectAsync(directory,QuantExpansionCommand.Symbols,
            [(holdout?QuantExpansionCommand.Holdout:QuantExpansionCommand.Development,holdout?"FreshHoldout":"Development")]);
    }
    private static async Task<int> CollectAsync(string directory,IReadOnlyList<string> symbols,
        IReadOnlyList<(DateTimeOffset Month,string Partition)> windows)
    {
        var store=new FoundationStore(directory);store.Initialize(DateTimeOffset.UtcNow);
        var research=Path.Combine(directory,"Research");Directory.CreateDirectory(research);
        using var http=new HttpClient(HttpTransport.Create(true)){Timeout=TimeSpan.FromSeconds(20)};
        var archive=new BinanceHistoricalArchive(http,store,TimeProvider.System,Path.Combine(research,"Archives"));var failures=0;
        foreach(var window in windows)foreach(var symbol in symbols)
        {
            var candles=new List<HourCandle>();var marks=new List<HourCandle>();var oi=new List<OpenInterestPoint>();
            var funding=new List<ArchivedFunding>();var proofs=new List<ArchiveProof>();var missing=new List<string>();
            async Task Capture(string name,Func<Task> action)
            {
                try{await action();}
                catch(Exception error) when(error is HttpRequestException or TaskCanceledException or InvalidDataException or InvalidOperationException or FormatException or OverflowException or JsonException)
                {missing.Add(name+": "+error.GetType().Name+": "+error.Message);Console.WriteLine($"{symbol} {name}: unavailable ({error.GetType().Name})");}
            }
            for(var month=window.Month.AddMonths(-1);month<=window.Month;month=month.AddMonths(1))
            {
                await Capture($"klines {month:yyyy-MM}",async()=>
                {
                    var result=await archive.CandlesAsync(symbol,month,default);candles.AddRange(result.Candles);
                    proofs.Add(new(symbol,"klines",result.Month,result.Url,result.Sha256,result.Bytes,result.DownloadedAt));
                });
                await Capture($"markPrice {month:yyyy-MM}",async()=>
                {
                    var result=await archive.DerivativeAsync(symbol,"markPriceKlines",month,default);
                    marks.AddRange(BinanceHistoricalArchive.ParseMark(symbol,month,result.Csv,result.Proof.DownloadedAt));proofs.Add(result.Proof);
                });
                await Capture($"fundingRate {month:yyyy-MM}",async()=>
                {
                    var result=await archive.DerivativeAsync(symbol,"fundingRate",month,default);
                    funding.AddRange(BinanceHistoricalArchive.ParseFunding(month,result.Csv,result.Proof.DownloadedAt));proofs.Add(result.Proof);
                });
            }
            // Include the previous day for initial 1h/4h/24h OI comparisons.
            for(var day=window.Month.AddDays(-1);day<window.Month.AddMonths(1);day=day.AddDays(1))
            {
                await Capture($"metrics {day:yyyy-MM-dd}",async()=>
                {
                    var result=await archive.DerivativeAsync(symbol,"metrics",day,default);
                    oi.AddRange(BinanceHistoricalArchive.ParseMetrics(symbol,day,result.Csv,result.Proof.DownloadedAt));proofs.Add(result.Proof);
                    Console.WriteLine($"{symbol} {window.Partition} {day:yyyy-MM-dd}: OI archived");
                });
            }
            var dataset=new QuantResearchDataset(symbol,window.Month,window.Month.AddMonths(1),window.Partition,candles,marks,oi,funding,proofs,missing);
            var file=Path.Combine(research,$"quant-data-{symbol}-{window.Month:yyyy-MM}.json");
            await File.WriteAllTextAsync(file+".tmp",JsonSerializer.Serialize(dataset,HistoricalResearchCommand.Json));File.Move(file+".tmp",file,true);
            Console.WriteLine($"Dataset {symbol} {window.Month:yyyy-MM}: H1={candles.Count} OI={oi.Count} funding={funding.Count} missing={missing.Count}");
            if(missing.Count>0)failures++;
        }
        return failures==0?0:2;
    }
}
