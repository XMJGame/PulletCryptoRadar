using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Trading;

namespace PulletCryptoRadar.Host;

public static class QuantExpansionCommand
{
    public const string PlanFileName="quant-expansion-plan-1.1.json";
    public static readonly string[] Symbols=["MYXUSDT","TRBUSDT","WIFUSDT","DOGEUSDT","BTCUSDT","ETHUSDT"];
    public static readonly DateTimeOffset Development=new(2025,9,1,0,0,0,TimeSpan.Zero);
    public static readonly DateTimeOffset Holdout=new(2026,3,1,0,0,0,TimeSpan.Zero);
    public static void FreezePlan(string directory)
    {
        var folder=Path.Combine(directory,"Research");Directory.CreateDirectory(folder);
        var plan=JsonSerializer.Serialize(new {Version="SixAssetExpansion-1.1",Symbols,Development,Holdout,
            Baseline=EntryThresholds.Production,EntryAlgorithm=EntryEngine.Version,ReplayAlgorithm=QuantReplayEngine.Version,
            DevelopmentProfiles=QuantSensitivityCommand.Profiles,HoldoutProfiles=new[]{EntryThresholds.Production},
            Regimes=new[]{"Mixed"},Costs=new[]{"Baseline","DoubleCosts"}},HistoricalResearchCommand.Json);
        var path=Path.Combine(folder,PlanFileName);
        if(File.Exists(path))
        {
            if(File.ReadAllText(path)!=plan)throw new InvalidDataException("Frozen expansion plan changed; use a new explicit study version.");
        }
        else
        {
            using var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read);
            var bytes=Encoding.UTF8.GetBytes(plan);stream.Write(bytes);stream.Flush(true);
        }
    }
    public static async Task<int> RunAsync(string directory,bool holdout)
    {
        FreezePlan(directory);
        var folder=Path.Combine(directory,"Research");var month=holdout?Holdout:Development;
        var partition=holdout?"FreshHoldout":"Development";
        IReadOnlyList<EntryThresholds> profiles=holdout?[EntryThresholds.Production]:QuantSensitivityCommand.Profiles;
        var rows=new List<QuantSensitivityRow>();var datasets=new List<QuantDatasetEvidence>();var missing=new List<string>();
        foreach(var symbol in Symbols)
        {
            var path=Path.Combine(folder,$"quant-data-{symbol}-{month:yyyy-MM}.json");
            if(!File.Exists(path)){missing.Add(Path.GetFileName(path));continue;}
            var bytes=await File.ReadAllBytesAsync(path);
            var data=JsonSerializer.Deserialize<QuantResearchDataset>(bytes,HistoricalResearchCommand.Json)
                ??throw new InvalidDataException("Dataset is null.");
            if(data.Symbol!=symbol||data.Start!=month||data.End!=month.AddMonths(1)||data.Partition!=partition)
                throw new InvalidDataException("Expansion dataset differs from frozen plan.");
            datasets.Add(new(symbol,partition,data.Start,data.End,Convert.ToHexString(SHA256.HashData(bytes)),
                data.Candles.Count,data.OpenInterest.Count,data.Funding.Count,data.Proofs.Count,data.Missing));
            if(data.Missing.Count>0)missing.Add(symbol+": "+string.Join("; ",data.Missing));
            foreach(var profile in profiles)foreach(var cost in new[]{"Baseline","DoubleCosts"})
            {
                var result=new QuantReplayEngine().Run(data,MarketState.Mixed,cost,profile);rows.Add(new(profile,result));
                Console.WriteLine($"{symbol} {partition} {profile.Version} {cost}: confirmed={result.Confirmations}, trades={result.Trades.Count}, cash={result.FinalCash:F4}");
            }
        }
        var limits=new List<string>
        {
            "六币固定样本与月份见 quant-expansion-plan-1.1.json；修正回放目标价换算，未调整生产准入；MYX/TRB开发结果已查看；不存在自动选参或部署",
            holdout?"2026-03仅验证预先固定的生产基线；查看后不能再为后续选参提供独立留出证明":"2025-09扩展开发区间；七组单因素邻域不做收益排名选参",
            "每币、每参数及成本情境各自独立1000U；不是六仓组合，重复机会不能相加为独立交易",
            "市场状态固定Mixed假设；历史全市场广度、盘口和合约规则未还原；不等价生产回测",
            "OHLC内采用保守路径，资金费用小时开盘标记价参考；收盘回撤不涵盖盘中强平；继承内核回放限制",
            "最终现金不含未平仓浮盈亏；不同成本下风险预算可导致不同数量，不能从现金差异推断成本有利",
            "所选存续样本存在选择偏差；交易不足时不报告稳定胜率、收益能力或买卖推荐"
        };
        limits.AddRange(missing.Select(item=>"缺失："+item));
        var report=new QuantSensitivityReport(holdout?"SixAssetHoldout-1.0":"SixAssetDevelopment-1.0",DateTimeOffset.UtcNow,datasets,rows,limits,QuantReplayEngine.Version);
        var stem=holdout?"quant-expanded-holdout":"quant-expanded-development";
        var file=Path.Combine(folder,stem+".json");
        await File.WriteAllTextAsync(file+".tmp",JsonSerializer.Serialize(report,HistoricalResearchCommand.Json));File.Move(file+".tmp",file,true);
        var md=new StringBuilder(holdout?"# 六币新留出基线检验\n\n":"# 六币开发区间检验\n\n");
        md.AppendLine($"生成 {report.At:O}\n\n回放算法：{report.ReplayAlgorithm}\n");foreach(var limit in limits)md.AppendLine("- "+limit);
        md.AppendLine("\n|合约|参数|成本|确认|交易|未平|现金U|收盘回撤%|OI缺项|K线缺项|\n|---|---|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach(var row in rows)
        {
            var r=row.Result;md.AppendLine(FormattableString.Invariant($"|{r.Symbol}|{row.Thresholds.Version}|{r.CostScenario}|{r.Confirmations}|{r.Trades.Count}|{r.Trades.Count(t=>t.ClosedAt is null)}|{r.FinalCash:F4}|{r.MaximumEquityDrawdownPercent:F4}|{r.OiMissingBars}|{r.CandleMissingBars}|"));
        }
        foreach(var row in rows.Where(row=>row.Thresholds==EntryThresholds.Production&&row.Result.CostScenario=="Baseline"))
        {
            var r=row.Result;md.AppendLine($"\n## {r.Symbol} 基线原因（有顺序的评估计数，可重叠）\n");
            foreach(var reason in r.Reasons.OrderByDescending(pair=>pair.Value))md.AppendLine($"- {reason.Key}: {reason.Value}");
            md.AppendLine("\n### 按策略与方向的入场评估\n");
            foreach(var group in (r.EntryDiagnostics??[]).GroupBy(row=>(row.Strategy,row.Side)))
            {
                md.AppendLine($"\n{group.Key.Strategy} / {group.Key.Side}\n");
                foreach(var reason in group.OrderByDescending(row=>row.Count))md.AppendLine($"- {reason.Reason}: {reason.Count}");
            }
            foreach(var trade in r.Trades)md.AppendLine(FormattableString.Invariant($"- {trade.Strategy} {trade.Side} {trade.OpenedAt:O} → {trade.ClosedAt:O}: 净额 {trade.NetPnl:F4} U / 费用 {trade.Fees:F4} / 资金费 {trade.Funding:F4} / {trade.Status}"));
        }
        await File.WriteAllTextAsync(Path.Combine(folder,stem+".md"),md.ToString(),new UTF8Encoding(false));
        Console.WriteLine(file);return missing.Count==0?0:2;
    }
}
