using System.Text.Json;
using Microsoft.Extensions.Options;
using PulletCryptoRadar.Core;
namespace PulletCryptoRadar.Infrastructure;

public sealed class HourlyWarmupRepository(IOptions<StorageOptions> options):IHourlyWarmupRepository
{
    private readonly object gate=new();
    private string Root=>Path.GetFullPath(Path.Combine(options.Value.Directory,"HourlyWarmup"));
    public IReadOnlyList<HourlyWarmupRecord> Read()
    {
        lock(gate)
        {
            if(!Directory.Exists(Root))return [];
            return Directory.EnumerateFiles(Root,"*.json").Select(path=>
            {using var stream=File.OpenRead(path);if(stream.Length>64*1024)throw new InvalidDataException("补数记录过大");return JsonSerializer.Deserialize<HourlyWarmupRecord>(stream)??throw new InvalidDataException("补数记录为空");}).ToArray();
        }
    }
    public void Save(HourlyWarmupRecord record)
    {
        if(string.IsNullOrWhiteSpace(record.Symbol)||record.Symbol.Length>80||record.UpdatedAt<record.StartedAt||record.Hour>record.UpdatedAt)throw new InvalidDataException("补数记录身份或时间无效");
        lock(gate)
        {
            Directory.CreateDirectory(Root);var path=Path.Combine(Root,PulletCryptoRadar.Application.QuantRules.HashValue(record.Symbol)+".json");var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try{using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){JsonSerializer.Serialize(stream,record);stream.Flush(true);}File.Move(temp,path,true);}
            finally{if(File.Exists(temp))File.Delete(temp);}
        }
    }
}
