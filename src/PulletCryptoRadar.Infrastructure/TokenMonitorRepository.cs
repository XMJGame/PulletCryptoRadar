using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Infrastructure;
public sealed class TokenMonitorRepository:ITokenMonitorRepository
{
    private readonly object gate=new();private readonly string root;private readonly IReadOnlyList<TokenMonitorTarget> targets;
    public TokenMonitorRepository(IOptions<StorageOptions> options):this(options.Value.Directory,Path.Combine(AppContext.BaseDirectory,"Research","TokenMonitors.json")){}
    public TokenMonitorRepository(string directory,string manifest)
    {
        root=Path.GetFullPath(Path.Combine(directory,"TokenMonitoring"));
        targets=File.Exists(manifest)?JsonSerializer.Deserialize<TokenMonitorTarget[]>(File.ReadAllText(manifest),new JsonSerializerOptions{PropertyNameCaseInsensitive=true})??[]:[];
        if(targets.Count>3||targets.Select(t=>t.Id).Distinct().Count()!=targets.Count||targets.Any(t=>!System.Text.RegularExpressions.Regex.IsMatch(t.Id,@"\A[a-z0-9-]{1,60}\z")
            ||t.Platform!="binance-smart-chain"||!ChainIdentity.Address("bsc",t.ContractAddress)))throw new InvalidDataException("持续研究清单身份或数量无效");
    }
    public IReadOnlyList<TokenMonitorTarget> Targets()=>targets;
    private string Folder(string id)=>targets.Any(t=>t.Id==id)?Path.Combine(root,id):throw new ArgumentException("研究目标不存在");
    private static T? Read<T>(string path)=>File.Exists(path)?JsonSerializer.Deserialize<T>(File.ReadAllText(path)):default;
    private static void Write<T>(string path,T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);var temp=path+".tmp";
        using(var stream=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None))
        {var bytes=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value));stream.Write(bytes);stream.Flush(true);}
        File.Move(temp,path,true);
    }
    public TokenMonitorState? State(string id){lock(gate)return Read<TokenMonitorState>(Path.Combine(Folder(id),"state.json"));}
    public IReadOnlyList<TokenMonitorSnapshot> History(string id,int limit)
    {
        lock(gate){var folder=Path.Combine(Folder(id),"snapshots");return Directory.Exists(folder)?new DirectoryInfo(folder).EnumerateFiles("*.json")
            .OrderByDescending(f=>f.Name,StringComparer.Ordinal).Take(Math.Clamp(limit,1,400)).Select(f=>Read<TokenMonitorSnapshot>(f.FullName)??throw new InvalidDataException("持续研究快照为空")).ToArray():[];}
    }
    public void Save(TokenMonitorState state,TokenMonitorSnapshot? snapshot=null)
    {
        lock(gate)
        {
            var folder=Folder(state.TargetId);
            if(snapshot is not null)
            {
                if(snapshot.TargetId!=state.TargetId||state.Latest?.Id!=snapshot.Id)throw new InvalidDataException("持续研究提交身份不符");
                var path=Path.Combine(folder,"snapshots",snapshot.SourceTime.UtcTicks.ToString("D19")+"-"+snapshot.Id.ToString("N")+".json");
                if(File.Exists(path))throw new InvalidOperationException("研究快照不得覆盖");Write(path,snapshot);
            }
            Write(Path.Combine(folder,"state.json"),state);
        }
    }
    private IReadOnlyList<MonitorBudgetEntry> Entries()=>Read<MonitorBudgetEntry[]>(Path.Combine(root,"rpc-budget.json"))??[];
    public MonitorBudgetView Budget(DateTimeOffset now)
    {lock(gate){var entries=Entries();return new(entries.Where(e=>e.At>now.AddHours(-1)).Sum(e=>e.Requests),entries.Where(e=>e.At>now.AddDays(-1)).Sum(e=>e.Requests));}}
    public bool Reserve(DateTimeOffset now,int requests)
    {
        if(requests is <1 or >64)throw new ArgumentOutOfRangeException(nameof(requests));
        lock(gate){var b=Budget(now);if(b.ReservedHour+requests>b.HourLimit||b.ReservedDay+requests>b.DayLimit)return false;
            Write(Path.Combine(root,"rpc-budget.json"),Entries().Where(e=>e.At>now.AddDays(-1)).Append(new(now,requests)).ToArray());return true;}
    }
}
