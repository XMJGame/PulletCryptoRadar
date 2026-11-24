using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Features;
using PulletCryptoRadar.Infrastructure;

namespace PulletCryptoRadar.Host;

public static class HistoricalResearchCommand
{
    public static readonly HistoricalSample[] Samples=
    [
        new("MYXUSDT","UserCaseDiscovery",At(2025,8),At(2025,10),"用户指定历史案例；用于探索，不能作为留出验证"),
        new("TRBUSDT","UserCaseDiscovery",At(2023,11),At(2024,1),"用户指定历史案例；固定跨月区间，不据最高价选入场"),
        new("WIFUSDT","Comparison",At(2025,8),At(2025,10),"同期高波动存续合约对照；不是预先认定的失败案例"),
        new("ARBUSDT","Comparison",At(2025,8),At(2025,10),"同期合约对照；不按结果选信号"),
        new("DOGEUSDT","Comparison",At(2025,8),At(2025,10),"同期流动性较高对照；不是低市值同类匹配"),
        new("LABUSDT","IdentityAndCoverageProbe",At(2026,9),At(2026,10),"用户指定简称；仅核对精确合约归档，尚未独立核实链上项目身份"),
        new("BTWUSDT","IdentityAndCoverageProbe",At(2026,9),At(2026,10),"用户指定简称；仅核对精确合约归档，尚未独立核实链上项目身份")
    ];
    private static DateTimeOffset At(int year,int month)=>new(year,month,1,0,0,0,TimeSpan.Zero);
    public static readonly JsonSerializerOptions Json=new(){WriteIndented=true,Converters={new JsonStringEnumConverter()}};
    public static async Task<int> RunAsync(string directory)
    {
        var database=new FoundationStore(directory);database.Initialize(DateTimeOffset.UtcNow);
        var target=Path.Combine(directory,"Research");Directory.CreateDirectory(target);
        using var http=new HttpClient(HttpTransport.Create(true)){Timeout=TimeSpan.FromSeconds(20)};
        var archive=new BinanceHistoricalArchive(http,database,TimeProvider.System,Path.Combine(target,"Archives"));
        var results=new List<HistoricalSampleResult>();
        foreach(var sample in Samples)
        {
            var candles=new List<HourCandle>();var missing=new List<string>();
            for(var month=At(sample.StudyStart.Year,sample.StudyStart.Month).AddMonths(-1);month<=sample.StudyEnd;month=month.AddMonths(1))
            {
                try
                {
                    var read=await archive.CandlesAsync(sample.Symbol,month,default);candles.AddRange(read.Candles);
                    Console.WriteLine($"{sample.Symbol} {month:yyyy-MM}: {read.Candles.Count} verified H1 bars");
                }
                catch(Exception error) when(error is HttpRequestException or InvalidDataException or InvalidOperationException or TaskCanceledException)
                {
                    missing.Add($"{month:yyyy-MM}: {error.GetType().Name}: {error.Message}");
                    Console.WriteLine($"{sample.Symbol} {month:yyyy-MM}: unavailable ({error.GetType().Name})");
                }
                // Two requests per new archive, <= 26/minute even when responses are immediate.
                await Task.Delay(TimeSpan.FromSeconds(4.7));
            }
            results.Add(new HistoricalResearchEngine().Evaluate(sample,candles,missing));
        }
        var report=new HistoricalResearchReport(HistoricalResearchEngine.Version,DateTimeOffset.UtcNow,results,
            ["探索性价格/成交量基线，不是正式妖币模型、普通量化策略回测或可执行收益",
             "历史持币、历史市值、解锁和关联主体资料未建立；今天的快照不会回填过去",
             "OI 原生 REST 仅有近期窗口；官方每日 metrics 归档可另行获取，本轮未把未下载资料当已覆盖",
             "没有独立留出验证；存续对照无法消除幸存者偏差；LAB/BTW 项目身份未核实前不输出潜力结论"]);
        var path=Path.Combine(target,"historical-report.json");
        await File.WriteAllTextAsync(path+".tmp",JsonSerializer.Serialize(report,Json));File.Move(path+".tmp",path,true);
        await File.WriteAllTextAsync(Path.Combine(target,"historical-report.md"),Markdown(report),new UTF8Encoding(false));
        Console.WriteLine($"Report: {path}; samples={results.Count}; signals={results.Sum(row=>row.Triggers.Count)}");
        return results.All(row=>row.Status=="PriceHistoryReady")?0:2;
    }
    public static string Markdown(HistoricalResearchReport report)
    {
        var text=new StringBuilder($"# 历史价格/成交量基线研究\n\n生成：{report.GeneratedAt:O}\n\n");
        text.AppendLine("规则预先固定：闭合价突破过去20小时最高价、量比≥1.5、24h涨幅0～15%、主动买入占比≥0.52；仅使用当时闭合K线，同币至少相隔7天记录。参考价为信号闭合价，不是可成交买入价。\n");
        text.AppendLine("|合约|样本角色|状态|H1条数|缺失小时间隔|信号数|完整7天窗口|完整30天窗口|\n|---|---|---|---:|---:|---:|---:|---:|");
        foreach(var row in report.Samples)text.AppendLine($"|{row.Sample.Symbol}|{row.Sample.Role}|{row.Status}|{row.Bars}|{row.MissingHourlyIntervals}|{row.Triggers.Count}|{row.Triggers.Count(t=>t.Outcomes.Any(o=>o.Days==7&&o.Complete))}|{row.Triggers.Count(t=>t.Outcomes.Any(o=>o.Days==30&&o.Complete))}|");
        foreach(var row in report.Samples)
        {
            text.AppendLine($"\n## {row.Sample.Symbol}\n\n{row.Sample.SelectionReason}\n\n研究区间 UTC：{row.Sample.StudyStart:yyyy-MM-dd}～{row.Sample.StudyEnd:yyyy-MM-dd}（不含结束）。\n");
            foreach(var missing in row.MissingArchives)text.AppendLine("- 缺项："+missing);
            text.AppendLine("\n|信号 UTC|窗口|完整|闭合收益%|最大上涨%|最低价相对参考%|收盘路径最大回撤%|\n|---|---:|---|---:|---:|---:|---:|");
            string N(decimal? value)=>value?.ToString("F4",System.Globalization.CultureInfo.InvariantCulture)??"缺失";
            foreach(var signal in row.Triggers)foreach(var outcome in signal.Outcomes)
                text.AppendLine($"|{signal.At:yyyy-MM-dd HH:mm}|{outcome.Days}d|{outcome.Complete}|{N(outcome.CloseReturnPercent)}|{N(outcome.MaximumRisePercent)}|{N(outcome.MaximumFallPercent)}|{N(outcome.MaximumDrawdownPercent)}|");
        }
        text.AppendLine("\n## 不能得出的结论\n");
        foreach(var limitation in report.Limitations.Concat(report.Samples.FirstOrDefault()?.Limitations??[]).Distinct())text.AppendLine("- "+limitation);
        text.AppendLine("\n来源：https://github.com/binance/binance-public-data 。ZIP 与官方 CHECKSUM 校验，URL、SHA256、下载时间保存在各归档旁 JSON；不改生产账户。\n");
        return text.ToString();
    }
}
