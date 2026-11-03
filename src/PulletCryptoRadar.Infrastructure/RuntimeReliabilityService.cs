using System.Text.Json;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Infrastructure;
public sealed class RuntimeReliabilityService(TimeProvider time)
{
    private readonly DateTimeOffset started=System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();
    public RuntimeReliability Status()=>Read(Path.Combine(AppContext.BaseDirectory,"Logs"),time.GetUtcNow(),started,Environment.ProcessId);
    public static RuntimeReliability Read(string directory,DateTimeOffset now,DateTimeOffset startedAt,int processId)
    {
        var notices=new List<string>();var events=new List<RuntimeEvent>();SupervisorState? supervisor=null;var skipped=0;
        try
        {
            var path=Path.Combine(directory,"runtime-sessions.jsonl");
            if(File.Exists(path))foreach(var line in File.ReadLines(path).TakeLast(10000))
                try{var e=JsonSerializer.Deserialize<RuntimeEvent>(line);if(e is not null&&e.At<=now)events.Add(e);}catch(JsonException){skipped++;}
        }
        catch(IOException){notices.Add("生命周期日志暂不可读，不能判定完整运行时段");}
        try
        {
            var path=Path.Combine(directory,"supervisor-state.json");
            if(File.Exists(path))
            {
                // The independent supervisor replaces this file atomically. Permit rename/delete
                // while reading so UI polling cannot prevent its heartbeat update on Windows.
                using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
                supervisor=JsonSerializer.Deserialize<SupervisorState>(file);
            }
        }
        catch(Exception ex)when(ex is IOException or JsonException){notices.Add("守护状态不可读，不能视作恢复已启用");}
        var current=events.Where(e=>e.ProcessId==processId&&e.At>=startedAt.AddMinutes(-1)).ToArray();
        var heartbeat=current.Where(e=>e.Phase=="Heartbeat").LastOrDefault()?.At;
        var actualStart=current.FirstOrDefault(e=>e.Phase=="Starting")?.At??startedAt;
        var fresh=supervisor is {Phase:"Running" or "WarmingUp"}&&supervisor.ServerProcessId==processId&&supervisor.At<=now&&now-supervisor.At<=TimeSpan.FromSeconds(40);
        if(!fresh)notices.Add("守护未确认活跃；本次HTTP响应只证明服务器此刻可访问");
        var gaps=RuntimeRecoveryPolicy.Gaps(events);
        if(gaps.Any(g=>!g.CleanExit))notices.Add("存在缺少正常退出标记的历史会话；断档开始仅能由最后生命迹象估计，未证明具体退出原因");
        if(skipped>0)notices.Add($"生命周期日志有{skipped}条未完整解析，不将其当作正常记录");
        notices.Add("心跳只证明进程活动，不证明行情、持仓观察或链上采集完整；停机不补造快照");
        return new(now,actualStart,heartbeat,supervisor,fresh,gaps,notices);
    }
}
