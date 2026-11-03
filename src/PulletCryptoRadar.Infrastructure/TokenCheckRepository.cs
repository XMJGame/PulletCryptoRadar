using System.Text.Json;
using Microsoft.Extensions.Options;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Infrastructure;
public sealed class TokenCheckRepository(IOptions<StorageOptions> settings):ITokenCheckRepository
{
    private readonly object gate=new();
    private string Root=>Path.GetFullPath(Path.Combine(settings.Value.Directory,"TokenChecks"));
    public void Save(TokenCheckReport report)
    {
        lock(gate)
        {
            Directory.CreateDirectory(Root);var path=Path.Combine(Root,report.Id.ToString("N")+".json");
            File.WriteAllText(path+".tmp",JsonSerializer.Serialize(report));File.Move(path+".tmp",path,false);
        }
    }
    public TokenCheckReport? Get(Guid id)
    {
        lock(gate)
        {
            var path=Path.Combine(Root,id.ToString("N")+".json");if(!File.Exists(path))return null;
            try{return JsonSerializer.Deserialize<TokenCheckReport>(File.ReadAllText(path));}
            catch(JsonException){return null;}
        }
    }
    public IReadOnlyList<TokenCheckReport> Recent(int limit)
    {
        lock(gate)return Directory.Exists(Root)?new DirectoryInfo(Root).EnumerateFiles("*.json").OrderByDescending(f=>f.LastWriteTimeUtc).Take(Math.Clamp(limit,1,100))
            .Select(f=>Guid.TryParseExact(Path.GetFileNameWithoutExtension(f.Name),"N",out var id)?Get(id):null).OfType<TokenCheckReport>().OrderByDescending(r=>r.AsOf).ToArray():[];
    }
    public IReadOnlyList<TokenCheckReport> ForFollowup(DateTimeOffset since)
    {
        lock(gate)return Directory.Exists(Root)?new DirectoryInfo(Root).EnumerateFiles("*.json").Where(f=>f.LastWriteTimeUtc>=since.UtcDateTime)
            .Select(f=>Guid.TryParseExact(Path.GetFileNameWithoutExtension(f.Name),"N",out var id)?Get(id):null)
            .OfType<TokenCheckReport>().Where(r=>r.AsOf>=since).ToArray():[];
    }
}
