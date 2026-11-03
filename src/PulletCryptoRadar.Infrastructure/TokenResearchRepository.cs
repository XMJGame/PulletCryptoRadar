using System.Text.Json;
using Microsoft.Extensions.Options;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Infrastructure;
public sealed class TokenResearchRepository:ITokenResearchRepository
{
    private readonly IReadOnlyList<TokenResearchClaim> claims;
    public TokenResearchRepository():this(Path.Combine(AppContext.BaseDirectory,"Research","TokenEvidence.json")){}
    public TokenResearchRepository(string path)
    {
        claims=File.Exists(path)?JsonSerializer.Deserialize<TokenResearchClaim[]>(File.ReadAllText(path),new JsonSerializerOptions{PropertyNameCaseInsensitive=true})
            ??throw new InvalidDataException("项目研究证据文件为空"):[];
    }
    public IReadOnlyList<TokenResearchClaim> Claims()=>claims;
}
public sealed class TokenFollowupRepository(IOptions<StorageOptions> options):ITokenFollowupRepository
{
    private readonly object gate=new();
    private string Root=>Path.GetFullPath(Path.Combine(options.Value.Directory,"TokenFollowups"));
    public TokenFollowup? Get(Guid id)
    {
        lock(gate){var path=Path.Combine(Root,id.ToString("N")+".json");return File.Exists(path)?JsonSerializer.Deserialize<TokenFollowup>(File.ReadAllText(path)):null;}
    }
    public void Save(TokenFollowup value)
    {
        lock(gate){Directory.CreateDirectory(Root);var path=Path.Combine(Root,value.ReportId.ToString("N")+".json");File.WriteAllText(path+".tmp",JsonSerializer.Serialize(value));File.Move(path+".tmp",path,true);}
    }
}
