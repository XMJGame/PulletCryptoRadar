using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Trading;

namespace PulletCryptoRadar.Host;

public sealed record ConfirmationStageRow(EntryConfirmationMode Mode,QuantReplayResult Result);
public sealed record ConfirmationStageReport(string Version,DateTimeOffset At,string Definition,string ReplayAlgorithm,
    IReadOnlyList<QuantDatasetEvidence> Datasets,IReadOnlyList<ConfirmationStageRow> Results,IReadOnlyList<string> Limitations,PaperRuntimePolicy MarginPolicy);

public static class ConfirmationStageCommand
{
    public const string Version="ConfirmationStageStudy-1.1";
    public const string Definition="启动仍使用生产参与度门槛；候选确认仅验证后续闭合K线的价格形态，继续要求新鲜有效特征、H1/H4趋势、结构止损和超时；不修改成本、仓位或生产规则";
    public static async Task<int> RunAsync(string directory)
    {
        var folder=Path.Combine(directory,"Research");Directory.CreateDirectory(folder);
        var marginPolicy=new PaperRuntimePolicy();
        var evidence=new List<QuantDatasetEvidence>();var datasets=new List<QuantResearchDataset>();var missing=new List<string>();
        foreach(var window in new[]{(QuantExpansionCommand.Development,"Development","ViewedDevelopment"),
                    (QuantExpansionCommand.Holdout,"FreshHoldout","ViewedTimeOutside")})
        foreach(var symbol in QuantExpansionCommand.Symbols)
        {
            var path=Path.Combine(folder,$"quant-data-{symbol}-{window.Item1:yyyy-MM}.json");
            if(!File.Exists(path)){missing.Add(Path.GetFileName(path));continue;}
            var bytes=await File.ReadAllBytesAsync(path);
            var data=JsonSerializer.Deserialize<QuantResearchDataset>(bytes,HistoricalResearchCommand.Json)
                ??throw new InvalidDataException("Dataset missing content.");
            if(data.Symbol!=symbol||data.Start!=window.Item1||data.End!=window.Item1.AddMonths(1)||data.Partition!=window.Item2)
                throw new InvalidDataException("Confirmation study input identity/window mismatch.");
            evidence.Add(new(symbol,window.Item3,data.Start,data.End,Convert.ToHexString(SHA256.HashData(bytes)),
                data.Candles.Count,data.OpenInterest.Count,data.Funding.Count,data.Proofs.Count,data.Missing));
            datasets.Add(data with {Partition=window.Item3});
            if(data.Missing.Count>0)missing.Add(symbol+" "+window.Item1.ToString("yyyy-MM")+": archive gaps");
        }
        // Freeze the exact already-viewed inputs and single hypothesis before this study's outcomes.
        // No downloader and no production account/store are instantiated by this command.
        var plan=JsonSerializer.Serialize(new {Version,Definition,EntryAlgorithm=EntryEngine.Version,
            ReplayAlgorithm=QuantReplayEngine.Version,Thresholds=EntryThresholds.Production,
            MarginPolicy=marginPolicy,Admission="ProductionPaperStopBuffer",Modes=Enum.GetValues<EntryConfirmationMode>(),
            Costs=new[]{"Baseline","DoubleCosts"},Regime="Mixed",Datasets=evidence},HistoricalResearchCommand.Json);
        var planFile=Path.Combine(folder,"confirmation-stage-plan-1.1.json");
        if(File.Exists(planFile))
        {
            if(await File.ReadAllTextAsync(planFile)!=plan)throw new InvalidDataException("Frozen confirmation plan/input changed; declare a new study revision.");
        }
        else
        {
            await using var file=new FileStream(planFile,FileMode.CreateNew,FileAccess.Write,FileShare.Read);
            await file.WriteAsync(Encoding.UTF8.GetBytes(plan));await file.FlushAsync();
        }
        var rows=new List<ConfirmationStageRow>();
        foreach(var data in datasets)foreach(var mode in Enum.GetValues<EntryConfirmationMode>())foreach(var cost in new[]{"Baseline","DoubleCosts"})
        {
            var result=new QuantReplayEngine().Run(data,MarketState.Mixed,cost,confirmationMode:mode,marginPolicy:marginPolicy);rows.Add(new(mode,result));
            Console.WriteLine($"{data.Symbol} {data.Partition} {mode} {cost}: confirmed={result.Confirmations}, trades={result.Trades.Count}, cash={result.FinalCash:F4}");
        }
        var limits=new List<string>
        {
            "全部区间基线结果已查看；本轮为离线机制诊断，候选不能借这些数据证明独立泛化",
            "只有一个预先说明的阶段候选，不搜索最佳门槛，不自动修改或部署生产规则",
            "候选确认不再要求再次放量/OI增长/同向主动成交，但数据有效与新鲜、趋势、价格确认、原结构止损和超时仍须满足",
            "阶段改变会改变较早形态占用及后续机会；候选与基线交易未必是严格包含关系",
            "市场状态固定Mixed假设，历史盘口/过滤器未还原；单资产独立1000U，不是六仓组合或盘中强平还原",
            "两种模式均应用当前生产模拟的止损清算缓冲准入（维护假设5%、额外缓冲2%）；不是交易所档位或盘中安全退出还原",
            "小时内退出按保守OHLC路径，资金费标记价为小时开盘参考；MFE/MAE仅模拟观察点，继承内核全部限制",
            "现金不含窗口末浮盈亏；同一机会跨成本或模式重复，不可相加为独立样本",
            "当前网络采集保持停止；本命令只读本地数据并写研究报告，不下载、不触及生产账户"
        };
        limits.AddRange(missing.Select(item=>"缺失："+item));
        var report=new ConfirmationStageReport(Version,DateTimeOffset.UtcNow,Definition,QuantReplayEngine.Version,evidence,rows,limits,marginPolicy);
        var json=Path.Combine(folder,"confirmation-stage-report.json");
        await File.WriteAllTextAsync(json+".tmp",JsonSerializer.Serialize(report,HistoricalResearchCommand.Json));File.Move(json+".tmp",json,true);
        var md=new StringBuilder("# 已查看区间的确认阶段离线研究\n\n");
        md.AppendLine($"生成 {report.At:O}\n\n{Definition}\n\n回放算法 {report.ReplayAlgorithm}\n");
        foreach(var limit in limits)md.AppendLine("- "+limit);
        md.AppendLine("\n|币|已查看区间|模式|成本|确认|交易|未平|现金U|收盘回撤%|\n|---|---|---|---|---:|---:|---:|---:|---:|");
        foreach(var row in rows){var r=row.Result;md.AppendLine(FormattableString.Invariant($"|{r.Symbol}|{r.Partition}|{row.Mode}|{r.CostScenario}|{r.Confirmations}|{r.Trades.Count}|{r.Trades.Count(t=>t.ClosedAt is null)}|{r.FinalCash:F4}|{r.MaximumEquityDrawdownPercent:F4}|"));}
        foreach(var row in rows.Where(row=>row.Result.CostScenario=="Baseline"))
        {
            var r=row.Result;md.AppendLine($"\n## {r.Symbol} {r.Partition} {row.Mode}\n");
            foreach(var reason in r.Reasons.OrderByDescending(pair=>pair.Value))md.AppendLine($"- {reason.Key}: {reason.Value}");
            foreach(var trade in r.Trades)md.AppendLine(FormattableString.Invariant($"- {trade.Strategy} {trade.Side} {trade.OpenedAt:O} → {trade.ClosedAt:O}: {trade.NetPnl:F4} U, fees {trade.Fees:F4}, funding {trade.Funding:F4}, {trade.Status}"));
        }
        await File.WriteAllTextAsync(Path.Combine(folder,"confirmation-stage-report.md"),md.ToString(),new UTF8Encoding(false));
        Console.WriteLine(json);return missing.Count==0?0:2;
    }
}
