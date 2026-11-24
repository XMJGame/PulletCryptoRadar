using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Host;

public sealed record QuantSensitivityRow(EntryThresholds Thresholds,QuantReplayResult Result);
public sealed record QuantSensitivityReport(string Version,DateTimeOffset At,IReadOnlyList<QuantDatasetEvidence> Datasets,
    IReadOnlyList<QuantSensitivityRow> Results,IReadOnlyList<string> Limitations,string? ReplayAlgorithm=null);

public static class QuantSensitivityCommand
{
    // Predeclared single-factor neighbours. No joint grid search and no winning profile is selected.
    public static readonly EntryThresholds[] Profiles=
    [EntryThresholds.Production,
        new("Oi-0.5",OiGrowthPercent:.5m),new("Oi-1.5",OiGrowthPercent:1.5m),
        new("Volume-Lower",PullbackVolume:1.1m,BreakoutVolume:1.3m),
        new("Volume-Higher",PullbackVolume:1.3m,BreakoutVolume:1.7m),
        new("Taker-0.51",DirectionalTaker:.51m),new("Taker-0.53",DirectionalTaker:.53m)];
    public static async Task<int> RunAsync(string directory)
    {
        var folder=Path.Combine(directory,"Research");Directory.CreateDirectory(folder);
        var rows=new List<QuantSensitivityRow>();var evidence=new List<QuantDatasetEvidence>();var missing=new List<string>();
        foreach(var window in QuantDatasetCommand.Windows)foreach(var symbol in QuantDatasetCommand.Symbols)
        {
            var path=Path.Combine(folder,$"quant-data-{symbol}-{window.Month:yyyy-MM}.json");
            if(!File.Exists(path)){missing.Add(Path.GetFileName(path));continue;}
            var bytes=await File.ReadAllBytesAsync(path);
            var data=JsonSerializer.Deserialize<QuantResearchDataset>(bytes,HistoricalResearchCommand.Json)
                ??throw new InvalidDataException("Missing dataset content.");
            if(data.Symbol!=symbol||data.Start!=window.Month||data.End!=window.Month.AddMonths(1)||data.Partition!=window.Partition)
                throw new InvalidDataException("Predeclared dataset identity mismatch.");
            evidence.Add(new(symbol,data.Partition,data.Start,data.End,Convert.ToHexString(SHA256.HashData(bytes)),
                data.Candles.Count,data.OpenInterest.Count,data.Funding.Count,data.Proofs.Count,data.Missing));
            if(data.Missing.Count>0)missing.Add(Path.GetFileName(path)+": archive gaps");
            foreach(var profile in Profiles)foreach(var cost in new[]{"Baseline","DoubleCosts"})
            {
                var result=new QuantReplayEngine().Run(data,MarketState.Mixed,cost,profile);rows.Add(new(profile,result));
                Console.WriteLine($"{symbol} {window.Partition} {profile.Version} {cost}: {result.Confirmations} confirmed, {result.Trades.Count} trades, {result.FinalCash:F4} cash");
            }
        }
        var limits=new List<string>
        {
            "用于解释当前门槛的局部敏感性，不自动选择或部署参数；生产规则不变",
            "2026-01区间结果已被查看，本轮将其作为已查看的时间外诊断；不能再称调参后独立留出验证",
            "每次只改变一个条件家族；不检验多条件相互作用，不按本表最大收益选择参数",
            "仅Mixed假设市场；历史盘口、市场广度、六仓组合与盘中强平未还原，继承情境回放全部限制",
            "趋势与参与度原因按有顺序的策略/方向评估计数，子项重叠，不能相加成信号数",
            "最终现金受固定风险预算导致的仓位变化影响，双倍费用结果不保证低于基线；不能解释为高成本有利",
            "少量交易、同一机会重复及未平仓不足以支持胜率或盈利稳定性判断；尚需更广样本和全新留出区间"
        };
        limits.AddRange(missing.Select(item=>"缺失："+item));
        var report=new QuantSensitivityReport("EntrySensitivity-1.0",DateTimeOffset.UtcNow,evidence,rows,limits,QuantReplayEngine.Version);
        var file=Path.Combine(folder,"quant-sensitivity-report.json");
        await File.WriteAllTextAsync(file+".tmp",JsonSerializer.Serialize(report,HistoricalResearchCommand.Json));File.Move(file+".tmp",file,true);
        var md=new StringBuilder("# 入场条件局部敏感性\n\n");
        md.AppendLine($"生成 {report.At:O}\n\n回放算法：{report.ReplayAlgorithm}\n");foreach(var limit in limits)md.AppendLine("- "+limit);
        md.AppendLine("\n|合约|区间|参数|成本|确认|交易|未平|现金U|收盘权益回撤%|\n|---|---|---|---|---:|---:|---:|---:|---:|");
        foreach(var row in rows){var r=row.Result;md.AppendLine(FormattableString.Invariant($"|{r.Symbol}|{r.Partition}|{row.Thresholds.Version}|{r.CostScenario}|{r.Confirmations}|{r.Trades.Count}|{r.Trades.Count(t=>t.ClosedAt is null)}|{r.FinalCash:F4}|{r.MaximumEquityDrawdownPercent:F4}|"));}
        await File.WriteAllTextAsync(Path.Combine(folder,"quant-sensitivity-report.md"),md.ToString(),new UTF8Encoding(false));
        Console.WriteLine(file);return missing.Count==0?0:2;
    }
}
