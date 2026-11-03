namespace PulletCryptoRadar.Core;

public sealed record RuntimeEvent(DateTimeOffset At,int ProcessId,string Phase,string? SessionId=null,string? ErrorType=null);
public sealed record RuntimeGap(DateTimeOffset LastEvidenceAt,DateTimeOffset ResumedAt,int PreviousProcessId,bool CleanExit,string Meaning);
public sealed record SupervisorState(DateTimeOffset At,int ProcessId,int? ServerProcessId,string Phase,int UnexpectedExits10Minutes,
    int? LastExitCode,DateTimeOffset? LastExitAt,DateTimeOffset? RetryAt,string Message);
public sealed record RuntimeReliability(DateTimeOffset AsOf,DateTimeOffset StartedAt,DateTimeOffset? LastHeartbeatAt,
    SupervisorState? Supervisor,bool SupervisorFresh,IReadOnlyList<RuntimeGap> Gaps,IReadOnlyList<string> Notices);
public sealed record RecoveryDecision(bool Restart,TimeSpan Delay,string Reason);
public static class RuntimeRecoveryPolicy
{
    public static RecoveryDecision Decide(bool requestedStop,bool completedStop,int unexpectedExits10Minutes)=>requestedStop||completedStop?
        new(false,TimeSpan.Zero,"正常停止/已请求停止，不自动拉起"):unexpectedExits10Minutes>3?
        new(false,TimeSpan.Zero,"10分钟内异常退出超过3次，停止重启，需人工检查"):
        new(true,TimeSpan.FromSeconds(15*Math.Pow(2,Math.Clamp(unexpectedExits10Minutes-1,0,2))),"退出原因未确认，等待后受控恢复；不删除数据或重置账户");
    public static IReadOnlyList<RuntimeGap> Gaps(IReadOnlyList<RuntimeEvent> entries)
    {
        var gaps=new List<RuntimeGap>();RuntimeEvent? started=null;RuntimeEvent? last=null;var clean=false;
        foreach(var row in entries.OrderBy(e=>e.At))
        {
            if(row.Phase=="Starting")
            {
                if(started is not null&&last is not null&&row.At>=last.At)gaps.Add(new(last.At,row.At,started.ProcessId,clean,
                    clean?"上次正常停止后的停机窗口，不补造记录":"上次缺少Stopped：最后生命迹象至本次启动为可能断档窗口，具体退出时刻/原因未知"));
                started=row;last=row;clean=false;
            }
            else if(started is not null&&row.ProcessId==started.ProcessId&&(row.SessionId is null||started.SessionId is null||row.SessionId==started.SessionId))
            {if(!clean)last=row;if(row.Phase=="Stopped")clean=true;}
        }
        return gaps.TakeLast(20).Reverse().ToArray();
    }
}
