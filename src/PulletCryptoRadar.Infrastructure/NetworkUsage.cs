using System.Net;
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
    public sealed record Ledger(string Day, string Month, long DayBytes, long MonthBytes, long Requests, long Blocked);
    public NetworkUsage(IOptions<NetworkOptions> settings, IOptions<StorageOptions> storage, TimeProvider clock)
    {
        options = settings.Value; time = clock;
        path = Path.Combine(storage.Value.Directory, "network-usage.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        state = File.Exists(path) ? JsonSerializer.Deserialize<Ledger>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Invalid network ledger") : new("", "", 0, 0, 0, 0);
        if (state.DayBytes < 0 || state.MonthBytes < 0 || state.Requests < 0) throw new InvalidDataException("Invalid network ledger counters");
    }
    private void Roll()
    {
        var now = time.GetUtcNow().ToOffset(TimeSpan.FromHours(8));
        var day = now.ToString("yyyy-MM-dd"); var month = now.ToString("yyyy-MM");
        state = state with { Day = day, Month = month, DayBytes = state.Day == day ? state.DayBytes : 0,
            MonthBytes = state.Month == month ? state.MonthBytes : 0 };
    }
    private void Save()
    {
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(state));
        File.Move(path + ".tmp", path, true);
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
