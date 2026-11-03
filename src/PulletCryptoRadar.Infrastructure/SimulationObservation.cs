using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Infrastructure;

public sealed record SimulationSavedRow(string Table,string Id,string Hash);
public sealed record SimulationObservationBaseline(string Version,DateTimeOffset StartedAt,DateTimeOffset DueAt,string ServerDirectory,int ProcessId,string SessionId,
    string SettingsHash,string? ControlHash,IReadOnlyDictionary<string,string> Binaries,IReadOnlyList<SimulationSavedRow> Rows,long Revision,int MalformedAtStart,long JournalBytes,IReadOnlyList<string> KnownIssues);
public sealed record SimulationObservationGate(string Name,string Status,string Detail);
public sealed record SimulationObservationReport(string Status,DateTimeOffset CheckedAt,DateTimeOffset From,DateTimeOffset Through,DateTimeOffset DueAt,
    decimal Hours,int Heartbeats,decimal LongestHeartbeatGapSeconds,int EquitySamples,int ValidEquitySamples,int EquityMinuteSlots,int ValidEquityMinuteSlots,
    decimal LongestEquityGapSeconds,int MissingMarks,int MissingFunding,int Unreconciled,int PreservedRows,int MissingOrChangedRows,IReadOnlyList<SimulationObservationGate> Gates)
{public int LedgerFailures {get;init;}public int ReconciliationUnknown {get;init;}}

public static class SimulationObservation
{
    public const string Version="Simulation-Observation-1";
    public static IReadOnlyDictionary<string,string> Binaries(string server)
    {
        var files=Directory.EnumerateFiles(server,"*",SearchOption.TopDirectoryOnly).Where(p=>new[]{".dll",".exe",".json"}.Contains(Path.GetExtension(p),StringComparer.OrdinalIgnoreCase)&&Path.GetFileName(p)!="appsettings.json")
            .Concat(Directory.Exists(Path.Combine(server,"wwwroot"))?Directory.EnumerateFiles(Path.Combine(server,"wwwroot"),"*",SearchOption.AllDirectories):[]);
        return files.Order(StringComparer.Ordinal).ToDictionary(p=>Path.GetRelativePath(server,p),SimulationBackup.HashFile,StringComparer.Ordinal);
    }
    public static IReadOnlyList<SimulationSavedRow> Rows(string database)
    {
        using var c=SimulationBackup.ReadOnly(database);using var tx=c.BeginTransaction();var result=new List<SimulationSavedRow>();
        foreach(var (table,key) in new[]{("PaperTrade","Id"),("PaperFundingEvent","EventKey"),("PaperEntryReview","Id"),("PaperEquitySample","Revision")})
        {
            using var command=c.CreateCommand();command.Transaction=tx;command.CommandText=$"SELECT {key},Payload FROM {table} ORDER BY {key}";using var reader=command.ExecuteReader();
            while(reader.Read())result.Add(new(table,reader.GetValue(0).ToString()!,Convert.ToHexString(SHA256.HashData((byte[])reader[1]))));
        }
        return result;
    }
    public static IReadOnlyList<RuntimeEvent> Journal(string path,out int broken)
    {
        var rows=new List<RuntimeEvent>();broken=0;
        using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);using var reader=new StreamReader(file);
        while(reader.ReadLine() is {} line)try{var row=JsonSerializer.Deserialize<RuntimeEvent>(line);if(row is null)broken++;else rows.Add(row);}catch(JsonException){broken++;}
        return rows;
    }
    public static SimulationObservationBaseline Start(string server,DateTimeOffset now,int pid,IReadOnlyList<string>? knownIssues=null)
    {
        server=Path.GetFullPath(server);var journalPath=Path.Combine(server,"Logs","runtime-sessions.jsonl");var journal=Journal(journalPath,out var malformed);
        var current=journal.LastOrDefault(r=>r.Phase=="Starting"&&r.ProcessId==pid&&r.At<=now)??throw new InvalidDataException("缺少当前进程的会话起点");
        if(string.IsNullOrWhiteSpace(current.SessionId)||!journal.Any(r=>r.ProcessId==pid&&r.SessionId==current.SessionId&&r.Phase=="Heartbeat"&&r.At<=now&&now-r.At<=TimeSpan.FromSeconds(90)))throw new InvalidDataException("当前会话没有新鲜心跳");
        var db=Path.Combine(server,"Data","PulletCryptoRadar.db");var ledger=SimulationBackup.Ledger(db);
        return new(Version,now,now.AddHours(24),server,pid,current.SessionId,SimulationBackup.HashFile(Path.Combine(server,"appsettings.json")),ledger.ControlHash,Binaries(server),Rows(db),ledger.Revision,malformed,new FileInfo(journalPath).Length,knownIssues??[]);
    }
    public static decimal Gap(IReadOnlyList<DateTimeOffset> times,DateTimeOffset from,DateTimeOffset through)
    {
        var previous=from;decimal longest=0;foreach(var time in times.Where(t=>t>=from&&t<=through).Order()){longest=Math.Max(longest,(decimal)(time-previous).TotalSeconds);previous=time;}
        return Math.Max(longest,(decimal)(through-previous).TotalSeconds);
    }
    public static SimulationObservationReport Evaluate(SimulationObservationBaseline baseline,DateTimeOffset now,IReadOnlyList<RuntimeEvent> journal,int malformed,
        IReadOnlyList<PaperEquitySample> samples,IReadOnlyList<SimulationSavedRow> saved,string settingsHash,string? controlHash,IReadOnlyDictionary<string,string> binaries,bool healthy,bool supervisorFresh)
    {
        if(baseline.Version!=Version||baseline.DueAt!=baseline.StartedAt.AddHours(24)||now<baseline.StartedAt)throw new InvalidDataException("观察版本或时间范围无效");
        var through=now<baseline.DueAt?now:baseline.DueAt;var complete=now>=baseline.DueAt;var events=journal.Where(r=>r.At>=baseline.StartedAt&&r.At<=through).ToArray();
        var heartbeats=events.Where(r=>r.ProcessId==baseline.ProcessId&&r.SessionId==baseline.SessionId&&r.Phase=="Heartbeat").Select(r=>r.At).ToArray();
        var unexpected=events.Count(r=>r.Phase is "Starting" or "Stopping" or "Stopped"||r.ErrorType is not null);
        var maxHeartbeat=Gap(heartbeats,baseline.StartedAt,through);var equity=samples.Where(s=>s.CapturedAt>=baseline.StartedAt&&s.CapturedAt<=through).ToArray();
        var maxEquity=Gap(equity.Select(s=>s.CapturedAt).ToArray(),baseline.StartedAt,through);
        var ledgerFailures=equity.Count(s=>!s.Reconciled&&s.Difference is not null);
        var reconciliationUnknown=equity.Count(s=>!s.Reconciled&&s.Difference is null);
        var lookup=saved.ToDictionary(r=>(r.Table,r.Id),r=>r.Hash);var missing=baseline.Rows.Count(r=>!lookup.TryGetValue((r.Table,r.Id),out var h)||h!=r.Hash);
        var gates=new List<SimulationObservationGate>{
            new("24小时窗口",complete?"Pass":"Collecting",$"观察起点{baseline.StartedAt:o}，到期{baseline.DueAt:o}；不提前宣称稳定24小时"),
            new("会话连续性",unexpected==0&&malformed==0&&maxHeartbeat<=90?"Pass":"Fail",$"窗口内启动/停止/错误事件{unexpected}；日志解析缺口{malformed}；最长心跳间隔{maxHeartbeat:F1}秒。停止或休眠需核查，不能补造"),
            new("当前服务与守护",healthy&&supervisorFresh?"Pass":"Unknown","只证明检查时刻健康；历史连续性由窗口日志另行核对"),
            new("冻结配置与规则",settingsHash==baseline.SettingsHash&&controlHash==baseline.ControlHash?"Pass":"Fail","正常账户涨跌允许，配置与冻结执行控制须保持"),
            new("发布文件",binaries.Count==baseline.Binaries.Count&&baseline.Binaries.All(p=>binaries.GetValueOrDefault(p.Key)==p.Value)?"Pass":"Fail","运行文件与网页资源按内容摘要核对，不只比较PID"),
            new("原账本记录",missing==0?"Pass":"Fail",$"观察开始前{baseline.Rows.Count}条成交/资金费/复核/净值记录，缺失或改写{missing}条；新增正常记录允许"),
            new("净值采样连续性",equity.Length>0&&maxEquity<=180?"Pass":"Unknown",$"样本{equity.Length}条，最长含边界采样间隔{maxEquity:F1}秒；180秒以上保持待核查"),
            new("估值数据覆盖",equity.Length>0&&equity.All(s=>s.Equity is not null&&s.MarksFresh&&s.FundingObserved&&s.Reconciled)?"Pass":"Unknown","逐条保留失鲜、资金费未知及不守恒；可信净值不等于所有候选OI/盘口完整"),
            new("账目守恒",ledgerFailures>0?"Fail":equity.Length>0&&reconciliationUnknown==0?"Pass":"Unknown",$"已知核对失败{ledgerFailures}条，因缺少数值无法核对{reconciliationUnknown}条；失鲜不冒充账目差错，已知差额失败仍须调查"),
            new("已知原生异常",baseline.KnownIssues.Count>0?"Unknown":"Pass",baseline.KnownIssues.Count>0?string.Join("；",baseline.KnownIssues):"开始时无登记的未解决问题；运行窗口通过也不保证不存在其他缺陷"),
            new("策略盈利","Unknown","运行与模拟工程观察，不是盈利评审；不自动转真实资金")};
        var state=gates.Any(g=>g.Status=="Fail")?"NeedsInvestigation":!complete?"Collecting":gates.Where(g=>g.Name!="策略盈利").Any(g=>g.Status=="Unknown")?"CompletedWithEvidenceGaps":"Observed24Hours";
        return new(state,now,baseline.StartedAt,through,baseline.DueAt,(decimal)(through-baseline.StartedAt).TotalHours,heartbeats.Length,maxHeartbeat,equity.Length,equity.Count(s=>s.Equity is not null),
            equity.Select(s=>s.CapturedAt.ToUnixTimeSeconds()/60).Distinct().Count(),equity.Where(s=>s.Equity is not null).Select(s=>s.CapturedAt.ToUnixTimeSeconds()/60).Distinct().Count(),maxEquity,
            equity.Count(s=>!s.MarksFresh),equity.Count(s=>!s.FundingObserved),equity.Count(s=>!s.Reconciled),baseline.Rows.Count-missing,missing,gates){LedgerFailures=ledgerFailures,ReconciliationUnknown=reconciliationUnknown};
    }
    public static SimulationObservationReport Check(SimulationObservationBaseline baseline,DateTimeOffset now,bool healthy,bool supervisorFresh)
    {
        var server=baseline.ServerDirectory;var journal=Journal(Path.Combine(server,"Logs","runtime-sessions.jsonl"),out var malformed);
        using var c=SimulationBackup.ReadOnly(Path.Combine(server,"Data","PulletCryptoRadar.db"));using var tx=c.BeginTransaction();
        var state=StorageRecordCodec.Decode<PaperState>(c.QuerySingle<byte[]>("SELECT Payload FROM PaperAccount WHERE Id=1",transaction:tx));
        var samples=c.Query<byte[]>("SELECT Payload FROM PaperEquitySample WHERE Time>=@from AND Time<=@to ORDER BY Time,Revision",new{from=baseline.StartedAt.ToUnixTimeMilliseconds(),to=Math.Min(now.ToUnixTimeMilliseconds(),baseline.DueAt.ToUnixTimeMilliseconds())},tx).Select(bytes=>StorageRecordCodec.Decode<PaperEquitySample>(bytes)).ToArray();
        var journalIssue=new FileInfo(Path.Combine(server,"Logs","runtime-sessions.jsonl")).Length<baseline.JournalBytes?1:Math.Max(0,malformed-baseline.MalformedAtStart);
        return Evaluate(baseline,now,journal,journalIssue,samples,Rows(Path.Combine(server,"Data","PulletCryptoRadar.db")),SimulationBackup.HashFile(Path.Combine(server,"appsettings.json")),state.QuantControl is null?null:QuantRules.HashValue(state.QuantControl),Binaries(server),healthy,supervisorFresh);
    }
}
