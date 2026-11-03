using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;
namespace PulletCryptoRadar.Infrastructure;

public sealed class QuantPortfolioValidationRepository(IOptions<StorageOptions> options):IQuantPortfolioValidationRepository
{
    private readonly object gate=new();private string Root=>Path.GetFullPath(Path.Combine(options.Value.Directory,"QuantPortfolioValidation"));
    private string SourcePath(string hash)
    {if(hash.Length!=64||!hash.All(Uri.IsHexDigit))throw new ArgumentException("原始组合资料摘要无效");return Path.Combine(Root,"Sources",hash+".json.gz");}
    public string SaveSource(QuantPortfolioSource source)
    {
        var hash=QuantRules.HashValue(source);lock(gate)
        {
            var path=SourcePath(hash);if(File.Exists(path)){ReadSource(hash);return hash;}
            Write(path,source,false);return hash;
        }
    }
    public QuantPortfolioSource ReadSource(string hash)
    {
        lock(gate){var result=ReadFile<QuantPortfolioSource>(SourcePath(hash),16*1024*1024);if(QuantRules.HashValue(result)!=hash)throw new InvalidDataException("原始组合资料摘要不符");return result;}
    }
    public QuantPortfolioValidationReport? Latest()
    {
        lock(gate)
        {var path=Path.Combine(Root,"latest.json.gz");if(!File.Exists(path))return null;var report=ReadFile<QuantPortfolioValidationReport>(path,8*1024*1024);
         if(report.Version!=QuantPortfolioValidationEngine.Version||Read(report.Id) is not {} run||QuantRules.HashValue(run.Report)!=QuantRules.HashValue(report))throw new InvalidDataException("组合报告版本或冻结内容不符");return report;}
    }
    public QuantPortfolioValidationRun? Read(Guid id)
    {
        lock(gate)
        {
            var path=Path.Combine(Root,"Runs",id.ToString("N")+".json.gz");if(!File.Exists(path))return null;
            var run=ReadFile<QuantPortfolioValidationRun>(path,64*1024*1024);Validate(run);return run;
        }
    }
    private void Validate(QuantPortfolioValidationRun run)
    {
        if(run.Input.Id!=run.Report.Id||run.Report.InputHash!=QuantRules.HashValue(run.Input)||QuantRules.HashValue(run)!=QuantRules.HashValue(QuantPortfolioValidationEngine.Run(run.Input,ReadSource)))
            throw new InvalidDataException("组合档案无法由冻结原始资料重放");
    }
    public void Save(QuantPortfolioValidationRun run)
    {
        lock(gate)
        {
            Validate(run);var path=Path.Combine(Root,"Runs",run.Input.Id.ToString("N")+".json.gz");
            if(File.Exists(path)){if(QuantRules.HashValue(ReadFile<QuantPortfolioValidationRun>(path,64*1024*1024))!=QuantRules.HashValue(run))throw new InvalidDataException("不能覆盖既有组合报告");}
            else Write(path,run,false);
            Write(Path.Combine(Root,"latest.json.gz"),run.Report,true);
        }
    }
    private static T ReadFile<T>(string path,int expanded)
    {using var file=File.OpenRead(path);if(file.Length>64*1024*1024)throw new InvalidDataException("组合压缩档案过大");using var bytes=new MemoryStream();file.CopyTo(bytes);return StorageRecordCodec.Decode<T>(bytes.ToArray(),expanded);}
    private static void Write<T>(string path,T value,bool overwrite)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){using(var zip=new GZipStream(stream,CompressionLevel.Optimal,true))JsonSerializer.Serialize(zip,value);stream.Flush(true);}File.Move(temporary,path,overwrite);}
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
}
