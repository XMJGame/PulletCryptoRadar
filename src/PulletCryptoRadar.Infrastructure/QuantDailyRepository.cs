using System.Text.Json;
using Microsoft.Extensions.Options;
using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;
namespace PulletCryptoRadar.Infrastructure;

public sealed class QuantDailyRepository(IOptions<StorageOptions> options):IQuantDailyRepository
{
    private readonly object gate=new();
    private string PathFor(QuantLiveControl control,DateOnly date)=>Path.Combine(options.Value.Directory,"QuantDaily",QuantRules.HashValue(control),date.ToString("yyyy-MM-dd")+".json");
    public void Save(QuantDailyReport report)
    {
        if(report.Version!=QuantDailyEngine.Version||report.Control.RulesHash!=QuantRules.Hash(report.Control.Parameters,report.Control.Costs))throw new InvalidDataException("每日复盘版本无效");
        var path=PathFor(report.Control,DateOnly.ParseExact(report.Date,"yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture));
        lock(gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try
            {
                using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){JsonSerializer.Serialize(stream,report);stream.Flush(true);}
                File.Move(temporary,path,true);
            }
            finally{if(File.Exists(temporary))File.Delete(temporary);}
        }
    }
    public QuantDailyReport? Read(QuantLiveControl control,DateOnly date)
    {
        lock(gate)
        {
            var path=PathFor(control,date);if(!File.Exists(path))return null;using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            if(stream.Length>8*1024*1024)throw new InvalidDataException("每日复盘文件过大");var report=JsonSerializer.Deserialize<QuantDailyReport>(stream)??throw new InvalidDataException("每日复盘为空");
            if(report.Version!=QuantDailyEngine.Version||report.Date!=date.ToString("yyyy-MM-dd")||QuantRules.HashValue(report.Control)!=QuantRules.HashValue(control))throw new InvalidDataException("每日复盘归档身份不符");return report;
        }
    }
}
