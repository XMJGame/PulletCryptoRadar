using System.Net;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Infrastructure;

// Counts this application's HTTP response payload before gzip decoding; not VPN billing/TLS overhead.
public sealed class NetworkUsage : INetworkTraffic
{
    private readonly object gate = new();
    private readonly NetworkOptions options;
    private readonly TimeProvider time;
    private readonly string path;
    private Ledger state;
    public sealed record Ledger(string Day, string Month, long DayBytes, long MonthBytes, long Requests, long Blocked)
    {
        public long ReservedDayBytes {get;init;}
        public long ReservedMonthBytes {get;init;}
        public string? RecoveryNote {get;init;}
    }
    public NetworkUsage(IOptions<NetworkOptions> settings, IOptions<StorageOptions> storage, TimeProvider clock)
    {
        options = settings.Value; time = clock;
        path = Path.Combine(storage.Value.Directory, "network-usage.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        path=Path.GetFullPath(path);
        if(TryRead(path,out var primary)){state=primary!;return;}
        var damaged=File.Exists(path);if(damaged)Quarantine(path);
        if(TryRead(path+".bak",out var previous))
        {
            state=previous!;Roll();
            // A lost final commit has unknown traffic. Keep the service readable, but do not
            // silently grant a fresh budget: reserve a whole day until operator review/day roll.
            state=state with{DayBytes=checked(state.DayBytes+options.DailyBudgetBytes),MonthBytes=checked(state.MonthBytes+options.DailyBudgetBytes),
                ReservedDayBytes=checked(state.ReservedDayBytes+options.DailyBudgetBytes),ReservedMonthBytes=checked(state.ReservedMonthBytes+options.DailyBudgetBytes),
                RecoveryNote="流量计数主文件缺失或损坏，已保留原件并恢复上次完整副本；当日另预留一日预算，外部采集暂停至额度可用或人工核对。预留不是实测流量。"};
            Save();return;
        }
        var damagedBackup=File.Exists(path+".bak");if(damagedBackup)Quarantine(path+".bak");
        state=new("","",0,0,0,0);
        if(damaged||damagedBackup)
        {
            Roll();state=state with{DayBytes=options.DailyBudgetBytes,MonthBytes=options.MonthlyBudgetBytes,ReservedDayBytes=options.DailyBudgetBytes,ReservedMonthBytes=options.MonthlyBudgetBytes,
                RecoveryNote="流量计数及可用副本均无法核对，损坏原件已隔离；暂按日/月预算满额预留，外部采集暂停。需以可信备份核对后恢复，不将未知用量清零。"};Save();
        }
    }
    private bool TryRead(string file,out Ledger? value)
    {
        value=null;if(!File.Exists(file))return false;
        try
        {
            using var stream=File.OpenRead(file);if(stream.Length>16384)throw new InvalidDataException("Network ledger exceeds bound");
            var read=JsonSerializer.Deserialize<Ledger>(stream)??throw new InvalidDataException("Empty network ledger");
            var empty=read.Day==""&&read.Month==""&&read.DayBytes==0&&read.MonthBytes==0;
            if(!empty&&(!DateOnly.TryParseExact(read.Day,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var day)||day.ToString("yyyy-MM",CultureInfo.InvariantCulture)!=read.Month||day>DateOnly.FromDateTime(time.GetUtcNow().ToOffset(TimeSpan.FromHours(8)).Date)))throw new InvalidDataException("Invalid network ledger date");
            if(read.DayBytes<0||read.MonthBytes<read.DayBytes||read.Requests<0||read.Blocked<0||read.ReservedDayBytes<0||read.ReservedMonthBytes<0||read.ReservedDayBytes>read.DayBytes||read.ReservedMonthBytes>read.MonthBytes)throw new InvalidDataException("Invalid network ledger counters");
            value=read;return true;
        }
        catch(Exception ex)when(ex is JsonException or InvalidDataException){return false;}
    }
    private void Quarantine(string file)=>File.Move(file,path+".corrupt-"+time.GetUtcNow().ToString("yyyyMMdd-HHmmss",CultureInfo.InvariantCulture)+"-"+Guid.NewGuid().ToString("N")+".json");
    private void Roll()
    {
        var now = time.GetUtcNow().ToOffset(TimeSpan.FromHours(8));
        var day = now.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture); var month = now.ToString("yyyy-MM",CultureInfo.InvariantCulture);
        state = state with { Day = day, Month = month, DayBytes = state.Day == day ? state.DayBytes : 0,
            MonthBytes = state.Month == month ? state.MonthBytes : 0,ReservedDayBytes=state.Day==day?state.ReservedDayBytes:0,ReservedMonthBytes=state.Month==month?state.ReservedMonthBytes:0 };
    }
    private void Save()
    {
        var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){JsonSerializer.Serialize(file,state);file.Flush(true);}
            if(File.Exists(path))File.Replace(temporary,path,path+".bak",true);else File.Move(temporary,path);
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    public void BeforeRequest()
    {
        lock (gate)
        {
            Roll();
            if (state.DayBytes >= options.DailyBudgetBytes || state.MonthBytes >= options.MonthlyBudgetBytes)
            {
                state = state with { Blocked = state.Blocked + 1 }; Save();
                throw new NetworkBudgetExceededException("本程序联网预算已用完；新请求暂停，已有数据保持原时间，不能当作实时信号。");
            }
            state = state with { Requests = state.Requests + 1 }; Save();
        }
    }
    public void Record(long bytes)
    {
        if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        lock (gate)
        {
            Roll(); state = state with { DayBytes = checked(state.DayBytes + bytes), MonthBytes = checked(state.MonthBytes + bytes) }; Save();
            if (state.DayBytes >= options.DailyBudgetBytes || state.MonthBytes >= options.MonthlyBudgetBytes)
                throw new NetworkBudgetExceededException("联网负载用量已达上限，中断当前响应并暂停后续请求。");
        }
    }
    public object Snapshot()
    {
        lock (gate)
        {
            Roll();
            return new { Settings = options, Usage = state, Paused = state.DayBytes >= options.DailyBudgetBytes || state.MonthBytes >= options.MonthlyBudgetBytes,
                Recovery=state.RecoveryNote is null?null:new{Note=state.RecoveryNote,state.ReservedDayBytes,state.ReservedMonthBytes,RecordedDayBytes=state.DayBytes-state.ReservedDayBytes,RecordedMonthBytes=state.MonthBytes-state.ReservedMonthBytes},
                TimeZone = "Asia/Shanghai", Scope = "HTTP body before gzip decoding and WS application payload; excludes TLS/TCP, VPN, other apps and offline archive scripts. In-flight chunks can exceed the threshold.",
                WebSocketsEnabled = !options.EconomyMode, Warning = "UseSystemProxy=false不能绕过VPN的TUN/系统路由；请在VPN客户端配置分流。省流模式关闭WS，K线/OI重点覆盖20个标的，缺失/失鲜不算通过。" };
        }
    }
}

public sealed class MeteredNetworkHandler(NetworkUsage usage) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        usage.BeforeRequest();
        if (request.Content is not null) usage.Record((await request.Content.ReadAsByteArrayAsync(ct)).LongLength);
        var response = await base.SendAsync(request, ct);
        try
        {
            // Bound each response; charge successful chunks even when the request is cancelled or invalid.
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            using var body = new MemoryStream(); var buffer = new byte[16384]; int read;
            while ((read = await source.ReadAsync(buffer, ct)) != 0)
            {
                usage.Record(read); body.Write(buffer, 0, read);
                if (body.Length > 8_000_000) throw new InvalidDataException("Provider response exceeds 8 MB safety bound");
            }
            var headers = response.Content.Headers.ToArray();
            var encoding=response.Content.Headers.ContentEncoding.ToArray();
            var bytes=body.ToArray();
            if(encoding.Length>1)throw new InvalidDataException("Unexpected stacked content encodings");
            if(encoding.Length==1)
            {
                if(!encoding[0].Equals("gzip",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Unexpected content encoding");
                using var packed=new MemoryStream(bytes);using var gzip=new System.IO.Compression.GZipStream(packed,System.IO.Compression.CompressionMode.Decompress);
                using var decoded=new MemoryStream();
                while((read=await gzip.ReadAsync(buffer,ct))!=0)
                {
                    decoded.Write(buffer,0,read);
                    if(decoded.Length>32_000_000)throw new InvalidDataException("Decoded response exceeds safety bound");
                }
                bytes=decoded.ToArray();
            }
            response.Content.Dispose();response.Content = new ByteArrayContent(bytes);
            foreach (var header in headers.Where(h=>!h.Key.Equals("Content-Encoding",StringComparison.OrdinalIgnoreCase)&&!h.Key.Equals("Content-Length",StringComparison.OrdinalIgnoreCase)))
                response.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            return response;
        }
        catch { response.Dispose(); throw; }
    }
}
