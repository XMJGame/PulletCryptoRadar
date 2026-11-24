using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Host;

public static class QuantReplayCommand
{
    public static async Task<int> RunAsync(string directory)
    {
        var folder=Path.Combine(directory,"Research");var results=new List<QuantReplayResult>();var missing=new List<string>();
        var datasets=new List<QuantDatasetEvidence>();
        foreach(var window in QuantDatasetCommand.Windows)foreach(var symbol in QuantDatasetCommand.Symbols)
        {
            var path=Path.Combine(folder,$"quant-data-{symbol}-{window.Month:yyyy-MM}.json");
            if(!File.Exists(path)){missing.Add(Path.GetFileName(path));continue;}
            var bytes=await File.ReadAllBytesAsync(path);
            var dataset=JsonSerializer.Deserialize<QuantResearchDataset>(bytes,HistoricalResearchCommand.Json)!;
            if(dataset.Symbol!=symbol||dataset.Start!=window.Month||dataset.End!=window.Month.AddMonths(1)||dataset.Partition!=window.Partition)
                throw new InvalidDataException("Dataset identity or predeclared partition mismatch.");
            datasets.Add(new(symbol,dataset.Partition,dataset.Start,dataset.End,Convert.ToHexString(SHA256.HashData(bytes)),
                dataset.Candles.Count,dataset.OpenInterest.Count,dataset.Funding.Count,dataset.Proofs.Count,dataset.Missing));
            if(dataset.Missing.Count>0)missing.Add($"{symbol}-{window.Month:yyyy-MM}: {dataset.Missing.Count} archive gaps");
            foreach(var regime in new[]{MarketState.Mixed,MarketState.Bull,MarketState.Bear})
            foreach(var cost in new[]{"Baseline","DoubleCosts"})
            {
                var result=new QuantReplayEngine().Run(dataset,regime,cost);results.Add(result);
                Console.WriteLine($"{symbol} {window.Partition} {regime} {cost}: confirmed={result.Confirmations}, trades={result.Trades.Count}, cash={result.FinalCash:F4}");
            }
        }
        var limitations=new List<string>
        {
            "尚非完整生产等价回测：历史全市场广度、盘口与合约过滤器没有还原，市场状态为明确假设情境",
            "开发区间固定2025-09、时间留出区间固定2026-01，样本固定MYX/TRB；本轮不根据留出结果调参",
            "只能检验所选存续样本与情境，不能证明全部合约、不同市场或未来的盈利能力",
            "每个情境独立1000U，现金含未平仓已实现部分；需同时查看窗口末持仓，不把现金变化当完整收益"
        };
        if(missing.Count>0)limitations.Add("尚未取得数据集："+string.Join(", ",missing));
        var report=new QuantReplayReport(QuantReplayEngine.Version,DateTimeOffset.UtcNow,results,limitations,datasets);
        var file=Path.Combine(folder,"quant-replay-report.json");
        await File.WriteAllTextAsync(file+".tmp",JsonSerializer.Serialize(report,HistoricalResearchCommand.Json));File.Move(file+".tmp",file,true);
        var text=new StringBuilder("# 普通量化内核历史情境回放\n\n");
        text.AppendLine($"生成 {report.At:O}\n\n生产 EntryEngine / SizingEngine / PaperPortfolioEngine 复用；历史行情真实，盘口与市场状态显式假设。\n");
        text.AppendLine("|合约|区间|假设市场|成本|确认形态|交易|窗口末未平|最终现金U|收盘权益最大回撤%|OI缺项决策|\n|---|---|---|---|---:|---:|---:|---:|---:|---:|");
        foreach(var result in results)text.AppendLine(FormattableString.Invariant($"|{result.Symbol}|{result.Partition}|{result.RegimeScenario}|{result.CostScenario}|{result.Confirmations}|{result.Trades.Count}|{result.Trades.Count(row=>row.ClosedAt is null)}|{result.FinalCash:F4}|{result.MaximumEquityDrawdownPercent:F4}|{result.OiMissingBars} / {result.DecisionBars}|"));
        text.AppendLine("\n## 限制\n");foreach(var limit in limitations.Concat(results.FirstOrDefault()?.Limitations??[]).Distinct())text.AppendLine("- "+limit);
        foreach(var result in results)
        {
            text.AppendLine($"\n## {result.Symbol} {result.Partition} {result.RegimeScenario} {result.CostScenario}\n");
            foreach(var reason in result.Reasons.OrderByDescending(row=>row.Value))text.AppendLine($"- {reason.Key}: {reason.Value}");
        }
        await File.WriteAllTextAsync(Path.Combine(folder,"quant-replay-report.md"),text.ToString(),new UTF8Encoding(false));
        Console.WriteLine(file);return missing.Count==0?0:2;
    }
}
