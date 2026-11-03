using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Application;

public static class QuantDiagnosticEngine
{
    public const string Version="Quant-Diagnostics-1";
    private sealed record Row(QuantStudyTrade Trade,bool Valid,decimal RawR,decimal SlipR,decimal FeeR,decimal FundingR,decimal NetR,decimal Hours);
    public static QuantDiagnosticReport Build(QuantStudyReport report,IReadOnlyList<QuantStudyTrade> trades,
        Func<string,QuantStudySeries?> readSeries,string window,string strategy,string policy,int cost)
    {
        if(!report.Request.Windows.Any(w=>w.Name==window)||strategy is not ("TrendPullback" or "Breakout20")||policy is not ("ScaleTail" or "FullAtFirst")||cost is <1 or >3)
            throw new ArgumentException("无效的诊断筛选条件");
        if(report.Request.StudyHash!=QuantStudyEngine.Hash(report.Request.Strategy,report.Request.Parameters)||report.Request.RulesHash!=QuantRules.Hash(report.Request.Strategy))
            throw new InvalidDataException("研究版本摘要不匹配");
        // Count reconciliation prevents a truncated detail listing from silently becoming the diagnostic sample.
        if(trades.Count!=report.Summaries.Sum(s=>s.Trades)||trades.Select(t=>t.Id).Distinct().Count()!=trades.Count)
            throw new InvalidDataException("研究逐笔总数或唯一性不匹配");
        foreach(var s in report.Summaries)
            if(trades.Count(t=>t.Window==s.Window&&t.Strategy==s.Strategy&&t.Policy==s.Policy&&t.CostMultiplier==s.CostMultiplier)!=s.Trades)
                throw new InvalidDataException("研究分组与存档报告不匹配");
        var sources=report.Sources.ToDictionary(s=>s.Symbol,StringComparer.Ordinal);
        if(trades.Any(t=>!sources.TryGetValue(t.Symbol,out var s)||s.Hash!=t.SourceHash||t.Symbol!=t.Seed.Symbol||t.Direction!=t.Seed.Direction||t.SignalHour!=t.Seed.EntryAt))
            throw new InvalidDataException("逐笔研究来源或种子身份不匹配");
        var filtered=trades.Where(t=>t.Window==window&&t.Strategy==strategy&&t.CostMultiplier==cost).ToArray();
        var rows=filtered.Where(t=>t.Policy==policy).Select(t=>Measure(t,report.Request.Parameters)).ToArray();
        var context=new Dictionary<string,QuantStudySeries?>();
        foreach(var symbol in new[]{"BTCUSDT","ETHUSDT"})
        {
            var series=sources.TryGetValue(symbol,out var source)?readSeries(source.Hash):null;
            if(series is not null&&(series.Instrument.Symbol!=symbol||QuantRules.HashValue(series)!=source!.Hash))throw new InvalidDataException("环境原始来源摘要不匹配");
            context[symbol]=series;
        }
        var environments=new Dictionary<DateTimeOffset,string>();
        string EnvironmentAt(DateTimeOffset hour)
        {
            if(environments.TryGetValue(hour,out var known))return known;
            var directions=new List<int>();
            foreach(var symbol in new[]{"BTCUSDT","ETHUSDT"})
            {
                if(context[symbol] is not {} series){directions.Add(0);continue;}
                var baseline=series.Candles.Where(c=>c.EndTime<=hour&&c.EndTime>hour.AddHours(-report.Request.Strategy.MinimumHours)).ToArray();
                var frame=QuantSignalEngine.HistoricalFrame(symbol,baseline,hour,series.CollectedAt,report.Request.Strategy);
                if(frame.Issue is not null||frame.Facts is not {} f){directions.Add(0);continue;}
                directions.Add(f.FourHourEma20>f.FourHourEma60&&f.FourHourClose>=f.FourHourEma20?1:
                    f.FourHourEma20<f.FourHourEma60&&f.FourHourClose<=f.FourHourEma20?-1:2);
            }
            return environments[hour]=directions.Contains(0)?"Unknown":directions.All(d=>d==1)?"BothUp":directions.All(d=>d==-1)?"BothDown":"Mixed";
        }
        var groups=new List<QuantDiagnosticGroup>();
        foreach(var direction in new[]{"Long","Short"})groups.Add(new("Direction",direction,Metrics(rows.Where(r=>r.Trade.Direction==direction).ToArray())));
        var env=rows.GroupBy(r=>EnvironmentAt(r.Trade.SignalHour)).ToDictionary(g=>g.Key,g=>g.ToArray());
        foreach(var name in new[]{"BothUp","BothDown","Mixed","Unknown"})groups.Add(new("Environment",name,Metrics(env.GetValueOrDefault(name)??[])));
        void Group(string dimension,Func<Row,string> key)
        {foreach(var g in rows.GroupBy(key).OrderBy(g=>g.Key,StringComparer.Ordinal))groups.Add(new(dimension,g.Key,Metrics(g.ToArray())));}
        Group("Symbol",r=>r.Trade.Symbol);Group("UtcDate",r=>r.Trade.SignalHour.UtcDateTime.ToString("yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture));
        Group("ExitPath",r=>ExitPath(r.Trade.Conservative));
        var concentration=new[]{Concentration("Symbol",rows,r=>r.Trade.Symbol),Concentration("UtcDate",rows,r=>r.Trade.SignalHour.UtcDateTime.ToString("yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture))};
        return new(Version,report.Request.Id,report.Request.StudyHash,QuantRules.HashValue(trades),window,strategy,policy,cost,Metrics(rows),groups,concentration,Pair(filtered,report.Request.Parameters),
            ["本页是看过历史结果后的探索性诊断；分组好坏不能作为新增准入条件或新的样本外盈利证明。",
            "环境只取信号时已闭合的BTC/ETH 4h均线与收盘，缺失或冲突为Unknown；不重建当时广度、OI、盘口或完整市场状态。",
            "成本桥接：同一路径还原滑点前的入场/退出价，毛R－滑点R－手续费R－资金费压力R＝净R。它不是重新运行零成本策略；路径和数量保持原样。",
            "成本吞掉毛利指还原滑点后的毛额为正、模型净额不为正。资金费为原研究的不利小时假设，不能解释为实际支付。",
            "去掉前三个正贡献币/UTC日期只是敏感性检查；正贡献占比的分母是正贡献分组合计，不是总净收益，不据此挑币或挑日期。",
            "退出配对只比较同策略、方向、币种、信号时刻、成本且种子完全一致的完整路径；两种退出的重叠占用可能造成不配对，不能只比较汇总均值推断退出更优。",
            "所有R均为独立机会初始止损风险单位；分组净R之和不是账户收益、权益曲线或回撤。未完成及记账不一致样本单列。"]);
    }
    private static bool Near(decimal a,decimal b)=>Math.Abs(a-b)<=.0000000001m*Math.Max(1,Math.Max(Math.Abs(a),Math.Abs(b)));
    private static Row Measure(QuantStudyTrade t,QuantStudyParameters p)
    {
        var x=t.Conservative;var seed=t.Seed;
        Row Invalid()=>new(t,false,0,0,0,0,0,0);
        if(x.Status!="Closed"||x.Net is null||x.ClosedAt is null||x.InitialRisk<=0||seed.Quantity<=0||seed.Entry<=0||seed.Direction is not ("Long" or "Short")||t.CostMultiplier is <1 or >3||x.Fees<0||x.Funding<0||x.RemainingQuantity!=0||x.ClosedAt<seed.EntryAt||x.Legs.Count==0)return Invalid();
        var side=seed.Direction=="Long"?1m:-1m;var slip=p.SlippagePerSide*t.CostMultiplier;
        if(slip<0||slip>=1||p.FeePerSide<0||x.Legs.Any(l=>l.Quantity<=0||l.Price<=0||l.Fee<0||l.At<seed.EntryAt||l.At>x.ClosedAt))return Invalid();
        var gross=x.Legs.Sum(l=>l.Gross);var fee=seed.Entry*seed.Quantity*p.FeePerSide*t.CostMultiplier+x.Legs.Sum(l=>l.Fee);
        if(!Near(x.Legs.Sum(l=>l.Quantity),seed.Quantity)||!Near(x.InitialRisk,side*(seed.Entry-seed.Stop)*seed.Quantity)||!Near(gross-x.Fees-x.Funding,x.Net.Value)||!Near(fee,x.Fees)||
            x.Legs.Any(l=>!Near(l.Gross,side*(l.Price-seed.Entry)*l.Quantity)||!Near(l.Fee,l.Price*l.Quantity*p.FeePerSide*t.CostMultiplier)))return Invalid();
        var rawEntry=seed.Entry/(1+side*slip);var raw=x.Legs.Sum(l=>side*(l.Price/(1-side*slip)-rawEntry)*l.Quantity);
        return new(t,true,raw/x.InitialRisk,(raw-gross)/x.InitialRisk,x.Fees/x.InitialRisk,x.Funding/x.InitialRisk,x.Net.Value/x.InitialRisk,(decimal)(x.ClosedAt.Value-seed.EntryAt).TotalHours);
    }
    private static QuantDiagnosticMetrics Metrics(IReadOnlyList<Row> rows)
    {
        var valid=rows.Where(r=>r.Valid).ToArray();decimal? Mean(Func<Row,decimal> value)=>valid.Length>0?valid.Average(value):null;
        return new(rows.Count,valid.Length,rows.Count(r=>r.Trade.Conservative.Status!="Closed"),rows.Count(r=>r.Trade.Conservative.Status=="Closed"&&!r.Valid),
            Mean(r=>r.NetR),Mean(r=>r.RawR),Mean(r=>r.SlipR),Mean(r=>r.FeeR),Mean(r=>r.FundingR),Mean(r=>r.Hours),valid.Sum(r=>r.NetR),valid.Count(r=>r.RawR>0&&r.NetR<=0));
    }
    private static string ExitPath(QuantExitResult x)
    {
        if(x.Status!="Closed")return x.Status;
        var final=x.Legs.LastOrDefault()?.Reason??"Unknown";
        var targets=x.Legs.Any(l=>l.Reason=="Target2")?"AfterT2":x.Legs.Any(l=>l.Reason=="Target1")?"AfterT1":"BeforeT1";
        return final is "Stop" or "GapStop" or "TimeLimit"?final+targets:final;
    }
    private static QuantDiagnosticConcentration Concentration(string dimension,IReadOnlyList<Row> rows,Func<Row,string> key)
    {
        var valid=rows.Where(r=>r.Valid).ToArray();var positive=valid.GroupBy(key).Select(g=>(Key:g.Key,Net:g.Sum(r=>r.NetR))).Where(g=>g.Net>0).OrderByDescending(g=>g.Net).ThenBy(g=>g.Key,StringComparer.Ordinal).ToArray();
        var removed=positive.Take(3).Select(g=>g.Key).ToArray();var remaining=valid.Where(r=>!removed.Contains(key(r),StringComparer.Ordinal)).ToArray();
        return new(dimension,removed,remaining.Length,remaining.Length>0?remaining.Average(r=>r.NetR):null,positive.Length>0?positive.Take(3).Sum(g=>g.Net)/positive.Sum(g=>g.Net)*100:null);
    }
    private static QuantDiagnosticPairing Pair(IReadOnlyList<QuantStudyTrade> rows,QuantStudyParameters p)
    {
        var matched=0;var tailOnly=0;var fullOnly=0;var unfinished=0;var invalid=0;var tailBetter=0;var fullBetter=0;var equal=0;decimal sum=0;
        foreach(var g in rows.GroupBy(t=>(t.Symbol,t.Direction,t.SignalHour)))
        {
            var tails=g.Where(t=>t.Policy=="ScaleTail").ToArray();var fulls=g.Where(t=>t.Policy=="FullAtFirst").ToArray();
            if(tails.Length>1||fulls.Length>1){invalid++;continue;}
            if(tails.Length==0){fullOnly++;continue;}if(fulls.Length==0){tailOnly++;continue;}
            if(tails[0].Seed!=fulls[0].Seed){invalid++;continue;}
            if(tails[0].Conservative.Status!="Closed"||fulls[0].Conservative.Status!="Closed"){unfinished++;continue;}
            var tail=Measure(tails[0],p);var full=Measure(fulls[0],p);if(!tail.Valid||!full.Valid){invalid++;continue;}
            var difference=tail.NetR-full.NetR;matched++;sum+=difference;if(difference>0)tailBetter++;else if(difference<0)fullBetter++;else equal++;
        }
        return new(matched,tailOnly,fullOnly,unfinished,invalid,matched>0?sum/matched:null,tailBetter,fullBetter,equal);
    }
}
