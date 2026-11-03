using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;
namespace PulletCryptoRadar.Infrastructure;
public sealed class QuantRehearsalRepository(IOptions<StorageOptions> options):IQuantRehearsalRepository
{
    private readonly object gate=new();private string Root=>Path.GetFullPath(Path.Combine(options.Value.Directory,"QuantMigration"));
    public QuantRehearsalRun? Latest()
    {
        lock(gate)
        {
            var path=Path.Combine(Root,"latest.json.gz");if(!File.Exists(path))return null;var run=Read(path);Validate(run);return run;
        }
    }
    private static QuantRehearsalRun Read(string path)
    {
        using var file=File.OpenRead(path);if(file.Length>32*1024*1024)throw new InvalidDataException("副本压缩档案过大");using var zip=new GZipStream(file,CompressionMode.Decompress);using var buffer=new MemoryStream();var block=new byte[16384];int count;
        while((count=zip.Read(block))>0){buffer.Write(block,0,count);if(buffer.Length>128*1024*1024)throw new InvalidDataException("副本展开过大");}
        return JsonSerializer.Deserialize<QuantRehearsalRun>(buffer.ToArray())??throw new InvalidDataException("副本档案为空");
    }
    private static void Validate(QuantRehearsalRun run)
    {
        QuantRehearsalLedgerEngine.Validate(run.Ledger);
        if(run.Report.Id!=run.Input.Id||run.Report.InputHash!=QuantRules.HashValue(run.Input)||run.Ledger.BaselineHash!=QuantRules.HashValue(run.Input.Paper.State)||QuantRules.HashValue(QuantRehearsalEngine.Run(run.Input))!=QuantRules.HashValue(run))throw new InvalidDataException("副本档案无法重放核对");
    }
    public void Save(QuantRehearsalRun run)
    {
        lock(gate)
        {
            Validate(run);Directory.CreateDirectory(Root);var archive=Path.Combine(Root,run.Report.Id.ToString("N")+".json.gz");
            if(File.Exists(archive)){if(QuantRules.HashValue(Read(archive))!=QuantRules.HashValue(run))throw new InvalidDataException("不能覆盖既有副本报告");}
            else Write(archive,run,false);
            Write(Path.Combine(Root,"latest.json.gz"),run,true);
        }
    }
    private static void Write(string path,QuantRehearsalRun value,bool overwrite)
    {
        var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{using(var file=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){using(var zip=new GZipStream(file,CompressionLevel.Optimal,true))JsonSerializer.Serialize(zip,value);file.Flush(true);}File.Move(temp,path,overwrite);}
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
}
