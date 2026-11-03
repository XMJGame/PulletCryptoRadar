using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using PulletCryptoRadar.Infrastructure;
using PulletCryptoRadar.Core;

var json=new JsonSerializerOptions{WriteIndented=true};
try
{
    if(args is ["soak-start",var server,var output])
    {
        server=Path.GetFullPath(server).TrimEnd(Path.DirectorySeparatorChar);output=Path.GetFullPath(output);
        if(Directory.Exists(output)||File.Exists(output)||output.StartsWith(server+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("观察输出须为服务器目录外的新目录");
        var snapshot=await Capture(server);Directory.CreateDirectory(output);
        foreach(var (name,value) in snapshot)File.WriteAllText(Path.Combine(output,name+".json"),value.GetRawText());
        var pid=snapshot["health"].GetProperty("processId").GetInt32();var baseline=SimulationObservation.Start(server,DateTimeOffset.UtcNow,pid,
            ["2026-11-01测试及隔离维护进程在.NET10.0.14 JSON编码路径出现异常，根因未确认；24小时观察不自动关闭该问题"]);
        using(var file=new FileStream(Path.Combine(output,"baseline.json"),FileMode.CreateNew))JsonSerializer.Serialize(file,baseline,json);
        Console.WriteLine(JsonSerializer.Serialize(new{baseline.Version,baseline.StartedAt,baseline.DueAt,baseline.ProcessId,baseline.SessionId,baseline.Revision,SavedRows=baseline.Rows.Count,Files=baseline.Binaries.Count},json));return 0;
    }
    if(args is ["soak-check",var observation])
    {
        var baseline=JsonSerializer.Deserialize<SimulationObservationBaseline>(File.ReadAllText(Path.Combine(observation,"baseline.json")))??throw new InvalidDataException("缺观察起点");
        var snapshot=await Capture(baseline.ServerDirectory);var healthy=snapshot["health"].TryGetProperty("processId",out var pid)&&pid.GetInt32()==baseline.ProcessId;
        var reliability=JsonSerializer.Deserialize<RuntimeReliability>(snapshot["reliability"],new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var report=SimulationObservation.Check(baseline,DateTimeOffset.UtcNow,healthy,reliability?.SupervisorFresh==true);
        var check=Path.Combine(observation,"checks",DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-ffff"));Directory.CreateDirectory(check);
        foreach(var (name,value) in snapshot)File.WriteAllText(Path.Combine(check,name+".json"),value.GetRawText());
        var text=JsonSerializer.Serialize(report,json);File.WriteAllText(Path.Combine(check,"report.json"),text);File.WriteAllText(Path.Combine(observation,"latest.json"),text);Console.WriteLine(text);return report.Status=="NeedsInvestigation"?2:0;
    }
    object result=args switch
    {
        ["backup",var source,var destination]=>SimulationBackup.Create(source,destination),
        ["verify-backup",var backup]=>SimulationBackup.Verify(backup),
        ["restore-test",var backup,var destination]=>SimulationBackup.RestoreTest(backup,destination),
        _=>throw new ArgumentException("Commands: backup <server> <new-directory> | verify-backup <backup> | restore-test <backup> <new-directory> | soak-start <server> <new-directory> | soak-check <observation>")
    };
    Console.WriteLine(JsonSerializer.Serialize(result,json));return 0;
}
catch(Exception ex){Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");return 1;}

static async Task<Dictionary<string,JsonElement>> Capture(string server)
{
    using var client=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){BaseAddress=new Uri("http://localhost:5218"),Timeout=TimeSpan.FromSeconds(30)};
    var result=new Dictionary<string,JsonElement>();
    foreach(var (name,url) in new[]{("health","/health"),("reliability","/api/runtime/reliability"),("execution","/api/quant/execution"),("daily","/api/quant/daily"),("costs","/api/execution/status"),("providers","/api/providers/health"),("network","/api/network"),("paper","/api/paper")})
    {
        try{using var response=await client.GetAsync(url);response.EnsureSuccessStatusCode();using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync());result[name]=doc.RootElement.Clone();}
        catch(Exception ex)when(ex is HttpRequestException or TaskCanceledException or JsonException)
        {if(name=="health")throw;result[name]=JsonSerializer.SerializeToElement(new{error=ex.GetType().Name,meaning="本次本地读取失败，保持未知，不补成有效数据"});}
        if(name=="health")
        {
            var expected="PulletCryptoRadar-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(server).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant())))[..20];
            if(!result[name].TryGetProperty("instanceId",out var instance)||instance.GetString()!=expected)throw new InvalidDataException("本机5218不是所选服务器实例");
        }
    }
    return result;
}
