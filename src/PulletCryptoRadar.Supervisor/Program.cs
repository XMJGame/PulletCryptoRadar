using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using PulletCryptoRadar.Core;

var root=Path.GetFullPath(args.FirstOrDefault()??throw new ArgumentException("server directory required"));
var serverPath=Path.Combine(root,"PulletCryptoRadar.exe");if(!File.Exists(serverPath))throw new FileNotFoundException("Server executable missing");
var logs=Path.Combine(root,"Logs");Directory.CreateDirectory(logs);
using var lease=new FileStream(Path.Combine(logs,"supervisor-instance.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
var stopPath=Path.Combine(logs,"supervisor-stop.request");var statePath=Path.Combine(logs,"supervisor-state.json");
var eventsPath=Path.Combine(logs,"supervisor-events.jsonl");var journalPath=Path.Combine(logs,"runtime-sessions.jsonl");
var failures=new Queue<DateTimeOffset>();int? serverId=null;int? lastExit=null;DateTimeOffset? lastExitAt=null;DateTimeOffset? retry=null;
if(File.Exists(eventsPath))foreach(var line in File.ReadLines(eventsPath).TakeLast(2000))
{try{var old=JsonSerializer.Deserialize<SupervisorState>(line);if(old is {Phase:"UnexpectedExit"}&&old.At>DateTimeOffset.UtcNow.AddMinutes(-10))failures.Enqueue(old.At);}catch(JsonException){}}
using var http=new HttpClient{Timeout=TimeSpan.FromSeconds(2)};
void Write(string phase,string message,bool significant=false)
{
    while(failures.TryPeek(out var at)&&at<=DateTimeOffset.UtcNow.AddMinutes(-10))failures.Dequeue();
    var state=new SupervisorState(DateTimeOffset.UtcNow,Environment.ProcessId,serverId,phase,failures.Count,lastExit,lastExitAt,retry,message);
    var json=JsonSerializer.Serialize(state);var temp=statePath+".tmp";
    using(var file=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None)){var bytes=System.Text.Encoding.UTF8.GetBytes(json);file.Write(bytes);file.Flush(true);}
    File.Move(temp,statePath,true);
    if(significant){File.AppendAllText(eventsPath,json+Environment.NewLine);Console.WriteLine($"{state.At:O} {phase}: {message}");}
}
bool Clean(int id,DateTimeOffset start)
{
    if(!File.Exists(journalPath))return false;
    foreach(var line in File.ReadLines(journalPath).TakeLast(2000))try
    {var e=JsonSerializer.Deserialize<RuntimeEvent>(line);if(e?.ProcessId==id&&e.At>=start.AddSeconds(-1)&&e.Phase=="Stopped")return true;}catch(JsonException){}
    return false;
}
bool Same(Process p){try{return string.Equals(p.MainModule?.FileName,serverPath,StringComparison.OrdinalIgnoreCase);}catch(System.ComponentModel.Win32Exception){return false;}catch(InvalidOperationException){return false;}}
async Task<bool> WaitUntil(DateTimeOffset until,string phase,string message)
{
    while(DateTimeOffset.UtcNow<until)
    {if(File.Exists(stopPath)){Write("Stopped","已收到停止标记，不再拉起服务器",true);return false;}Write(phase,message);await Task.Delay(1000);}
    return true;
}
try
{
    Write("Starting","仅恢复此目录进程；正常停止不重启；10分钟最多3次自动恢复",true);
    while(true)
    {
        if(File.Exists(stopPath)){Write("Stopped","已收到停止标记，不再启动服务器",true);break;}
        while(failures.TryPeek(out var at)&&at<=DateTimeOffset.UtcNow.AddMinutes(-10))failures.Dequeue();
        if(failures.Count>3){Write("CircuitOpen","连续异常退出保护已触发，请检查日志后手工启动",true);break;}
        Process? server=null;
        try
        {
            using var deployment=new FileStream(Path.Combine(root,".deployment.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            if(File.Exists(stopPath))continue;
            var existing=Process.GetProcessesByName("PulletCryptoRadar");
            foreach(var p in existing){if(server is null&&Same(p))server=p;else p.Dispose();}
            if(server is null)
            {
                var info=new ProcessStartInfo(serverPath){WorkingDirectory=root,UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden};
                var dumpPath=Path.Combine(logs,"Dumps");Directory.CreateDirectory(dumpPath);
                info.Environment["DOTNET_DbgEnableMiniDump"]="1";info.Environment["DOTNET_DbgMiniDumpType"]="1";info.Environment["DOTNET_DbgMiniDumpName"]=Path.Combine(dumpPath,"PulletCryptoRadar-%p-%t.dmp");
                server=Process.Start(info)??throw new InvalidOperationException("Server did not start");serverId=server.Id;Write("ChildStarted","已启动本目录服务器，正在等待本地健康检查",true);
            }
            else{serverId=server.Id;Write("AdoptedExisting","接管本目录既有进程的退出观察，不重复启动",true);}
        }
        catch(IOException){server?.Dispose();if(!await WaitUntil(DateTimeOffset.UtcNow.AddSeconds(3),"PublicationWait","部署目录正被使用，等待，不启动第二实例"))break;continue;}
        catch(Exception ex)when(ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {Write("LaunchFailed","启动失败："+ex.GetType().Name,true);failures.Enqueue(DateTimeOffset.UtcNow);Write("UnexpectedExit","未能启动子进程",true);if(!await WaitUntil(DateTimeOffset.UtcNow.AddSeconds(15),"Backoff","等待启动重试"))break;continue;}
        using(server)
        {
            var started=server.StartTime.ToUniversalTime();var first=DateTimeOffset.UtcNow;var misses=0;
            while(!server.HasExited)
            {
                if(File.Exists(stopPath)){Write("StopRequested","已请求停止；等待服务器正常排空写入，不再自动恢复",true);return;}
                try
                {
                    var health=await http.GetFromJsonAsync<JsonElement>("http://localhost:5218/health");
                    var ok=health.GetProperty("application").GetString()=="PulletCryptoRadar"&&health.GetProperty("processId").GetInt32()==server.Id;
                    if(!ok)throw new InvalidDataException("Health PID mismatch");
                    misses=0;Write("Running","本地健康检查通过；行情与链上时效需另看采集状态");
                }
                catch(Exception ex)when(ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidDataException)
                {misses++;Write(DateTimeOffset.UtcNow-first<TimeSpan.FromSeconds(60)?"WarmingUp":"HealthDegraded",$"连续{misses}次健康检查未通过；进程仍存在，不强杀、不启动另一实例");}
                await Task.WhenAny(server.WaitForExitAsync(),Task.Delay(TimeSpan.FromSeconds(10)));
            }
            lastExit=server.ExitCode;lastExitAt=DateTimeOffset.UtcNow;var clean=Clean(server.Id,started);serverId=null;
            if(!clean&&!File.Exists(stopPath))failures.Enqueue(lastExitAt.Value);
            var decision=RuntimeRecoveryPolicy.Decide(File.Exists(stopPath),clean,failures.Count);
            if(!decision.Restart){Write(clean?"Stopped":"CircuitOpen",decision.Reason+"；退出码 "+lastExit,true);break;}
            Write("UnexpectedExit","缺少Stopped，退出码 "+lastExit+"；原因未确认，原始账户和断点保留",true);
            retry=DateTimeOffset.UtcNow+decision.Delay;Write("Backoff",decision.Reason,true);
            if(!await WaitUntil(retry.Value,"Backoff",decision.Reason))break;retry=null;
        }
    }
}
catch(Exception ex){Write("SupervisorFailed","守护自身异常："+ex.GetType().Name+"，不再启动子进程",true);Environment.ExitCode=1;}
