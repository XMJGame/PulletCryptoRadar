using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;
namespace PulletCryptoRadar.Infrastructure;
public sealed class QuantStudyRepository(IOptions<StorageOptions> options):IQuantStudyRepository
{
    private readonly object gate=new();private string Root=>Path.GetFullPath(Path.Combine(options.Value.Directory,"QuantValidation"));
    private string FileAt(string name)=>Path.Combine(Root,name);
    private T? Read<T>(string name,bool zipped=false)
    {
        var path=FileAt(name);if(!File.Exists(path))return default;using var f=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        if(f.Length>64*1024*1024)throw new InvalidDataException("Study file size exceeded");using var gz=zipped?new GZipStream(f,CompressionMode.Decompress,true):null;
        using var buffer=new MemoryStream();var input=(Stream?)gz??f;var block=new byte[16384];int count;
        while((count=input.Read(block))>0){buffer.Write(block,0,count);if(buffer.Length>128*1024*1024)throw new InvalidDataException("Expanded study size exceeded");}
        return JsonSerializer.Deserialize<T>(buffer.ToArray())??throw new InvalidDataException("Study file is null");
    }
    private bool Write<T>(string name,T value,bool zipped=false,bool immutable=false)
    {
        var path=FileAt(name);if(immutable&&File.Exists(path))return false;Directory.CreateDirectory(Path.GetDirectoryName(path)!);var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var f=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {if(zipped){using(var gz=new GZipStream(f,CompressionLevel.Optimal,true))JsonSerializer.Serialize(gz,value);}else JsonSerializer.Serialize(f,value);f.Flush(true);}
            File.Move(temp,path,!immutable);return true;
        }
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
    public QuantStudyProgress? LatestProgress(){lock(gate)return Read<QuantStudyProgress>("latest-progress.json");}
    public void SaveProgress(QuantStudyProgress progress)
    {
        lock(gate)
        {
            var name=Path.Combine("Runs",progress.Request.Id.ToString("N"),"request.json");
            if(!Write(name,progress.Request,immutable:true)&&QuantRules.HashValue(Read<QuantStudyRequest>(name))!=QuantRules.HashValue(progress.Request))throw new InvalidDataException("Frozen study request cannot change");Write("latest-progress.json",progress);
        }
    }
    public QuantStudyReport? LatestReport(){lock(gate)return Read<QuantStudyReport>("latest-report.json.gz",true);}
    public QuantStudyReport? Report(Guid id){lock(gate)return Read<QuantStudyReport>(Path.Combine("Runs",id.ToString("N"),"report.json.gz"),true);}
    public IReadOnlyList<QuantStudyTrade> ReadTrades(Guid id){lock(gate)return Read<QuantStudyTrade[]>(Path.Combine("Runs",id.ToString("N"),"trades.json.gz"),true)??[];}
    public void SaveReport(QuantStudyReport report,IReadOnlyList<QuantStudyTrade> trades)
    {
        lock(gate)
        {
            var dir=Path.Combine("Runs",report.Request.Id.ToString("N"));
            var tradeName=Path.Combine(dir,"trades.json.gz");var reportName=Path.Combine(dir,"report.json.gz");
            if(!Write(tradeName,trades,true,true)&&QuantRules.HashValue(Read<QuantStudyTrade[]>(tradeName,true))!=QuantRules.HashValue(trades))throw new InvalidDataException("Study run cannot overwrite immutable trades");
            if(!Write(reportName,report,true,true))
            {
                var old=Read<QuantStudyReport>(reportName,true)!;
                if(QuantRules.HashValue(old)!=QuantRules.HashValue(report with{CompletedAt=old.CompletedAt}))throw new InvalidDataException("Frozen study report conflict");report=old;
            }
            Write("latest-report.json.gz",report,true);
        }
    }
    public QuantStudySeries? Series(string hash)
    {
        if(hash.Length!=64||!hash.All(Uri.IsHexDigit))throw new InvalidDataException("Invalid source hash");lock(gate)
        {var result=Read<QuantStudySeries>(Path.Combine("Inputs",hash+".json.gz"),true);if(result is not null&&QuantRules.HashValue(result)!=hash)throw new InvalidDataException("Study source hash mismatch");return result;}
    }
    public string SaveSeries(QuantStudySeries series)
    {lock(gate){var hash=QuantRules.HashValue(series);if(!Write(Path.Combine("Inputs",hash+".json.gz"),series,true,true))Series(hash);return hash;}}
    public IReadOnlyList<QuantStudyTrade> Trades(Guid id,string? window,string? strategy,string? policy,int cost,int limit)
    {
        lock(gate)return (Read<QuantStudyTrade[]>(Path.Combine("Runs",id.ToString("N"),"trades.json.gz"),true)??[])
            .Where(t=>(window is null||t.Window==window)&&(strategy is null||t.Strategy==strategy)&&(policy is null||t.Policy==policy)&&t.CostMultiplier==cost)
            .OrderByDescending(t=>t.SignalHour).ThenBy(t=>t.Symbol,StringComparer.Ordinal).Take(Math.Clamp(limit,1,100)).ToArray();
    }
}
