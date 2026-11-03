using System.Text.Json;
using PulletCryptoRadar.Core;
namespace PulletCryptoRadar.Application;
public static class QuantStudyEngine
{
    public const string Definition="Historical finalized OHLC reconstruction, current-universe bias; 60d 30/15/15 time split,72h purged entries; next-hour-open assumption;100U independent unleveraged continuous quantity; price-only no OI/book/MMR; ScaleTail30/30/40 afterT2 close-bar3ATR versus FullAtFirst;72h; stop-first/target-first ordering; gaps unknown; fees/slip.0005 each side, adverse .0001 hourly;cost1/2/3; date-cluster bootstrap seed17; no optimization or account return.";
    public static string Hash(QuantParameters p,QuantStudyParameters s)=>QuantRules.HashValue(new{Definition,RulesHash=QuantRules.Hash(p),Settings=s});
    public static QuantStudyRequest Request(IReadOnlyList<Instrument> universe,QuantParameters p,DateTimeOffset now)
    {
        var settings=new QuantStudyParameters();var end=DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds()/3600*3600);var from=end.AddDays(-settings.Days);var middle=from.AddDays(settings.Days/2);var holdout=from.AddDays(settings.Days*3/4);
        QuantStudyWindow Window(string name,DateTimeOffset a,DateTimeOffset b)=>new(name,a,b,b.AddHours(-settings.HoldHours));
        return new(Guid.NewGuid(),now,from,end,QuantRules.Hash(p),Hash(p,settings),p,settings,universe,[Window("Development",from,middle),Window("Validation",middle,holdout),Window("Holdout",holdout,end)]);
    }
    public static (QuantStudyReport Report,IReadOnlyList<QuantStudyTrade> Trades) Run(QuantStudyRequest request,IReadOnlyList<(QuantStudySource Source,QuantStudySeries Series)> input,
        QuantForwardReproduction forward,DateTimeOffset now,Action<int>? progress,CancellationToken ct)
    {
        if(request.StudyHash!=Hash(request.Strategy,request.Parameters)||request.RulesHash!=QuantRules.Hash(request.Strategy)||request.Through>now||input.Count>67)throw new InvalidDataException("Study identity or time boundary invalid");
        var trades=new List<QuantStudyTrade>();var counts=new Dictionary<string,long>();void Count(string key,long n=1)=>counts[key]=counts.GetValueOrDefault(key)+n;
        var processed=0;
        foreach(var (source,series) in input)
        {
            ct.ThrowIfCancellationRequested();if(source.Symbol!=series.Instrument.Symbol||QuantRules.HashValue(series)!=source.Hash)throw new InvalidDataException("Study source hash/identity mismatch");
            var bars=series.Candles.Where(c=>c.Symbol==series.Instrument.Symbol&&c.IntervalMinutes==60&&c.IsValid&&c.EndTime<=request.Through&&c.ReceivedTime<=now).OrderBy(c=>c.OpenTime).ToArray();
            var byOpen=bars.GroupBy(b=>b.OpenTime).ToDictionary(g=>g.Key,g=>g.First());var busy=new Dictionary<string,DateTimeOffset>();
            foreach(var window in request.Windows)
            for(var hour=window.From;hour<window.LastEntryExclusive;hour=hour.AddHours(1))
            {
                ct.ThrowIfCancellationRequested();Count("HoursChecked");
                var baseline=bars.Where(b=>b.EndTime<=hour&&b.EndTime>hour.AddHours(-request.Strategy.MinimumHours)).ToArray();
                var frame=QuantSignalEngine.HistoricalFrame(series.Instrument.Symbol,baseline,hour,now,request.Strategy);
                var decisions=QuantSignalEngine.Reconstruct(series.Instrument,frame,now,request.RulesHash,source.Hash,request.Strategy);
                foreach(var d in decisions)
                {
                    Count(d.Status);if(d.Status!="PriceCandidate"||d.Facts is not {} facts||d.Stop is null)continue;
                    if(!byOpen.TryGetValue(hour,out var entryBar)||entryBar.EndTime>now){Count("MissingEntry");continue;}
                    if(Math.Abs(entryBar.Open-facts.Close)>.75m*facts.Atr){Count("EntryChaseRejected");continue;}
                    foreach(var multiplier in new[]{1,2,3})foreach(var policy in new[]{"ScaleTail","FullAtFirst"})
                    {
                        var key=$"{window.Name}:{d.Strategy}:{multiplier}:{policy}";if(busy.GetValueOrDefault(key)>hour){Count("OverlapSkipped");continue;}
                        var side=d.Direction=="Long"?1m:-1m;var entry=entryBar.Open*(1+side*request.Parameters.SlippagePerSide*multiplier);
                        if(side*(entry-d.Stop.Value)<=0){Count("InvalidEntryStructure");continue;}
                        var targets=QuantExitEngine.Targets(d,entry);if(targets.First<=0||targets.Second<=0){Count("InvalidTargets");continue;}
                        var seed=new QuantExitSeed(d.Symbol,d.Direction,hour,entry,request.Parameters.Notional/entry,d.Stop.Value,targets.First,targets.Second,facts.Atr);
                        var lower=QuantExitEngine.Replay(seed,bars,now,policy,multiplier,request.Parameters);var upper=QuantExitEngine.Replay(seed,bars,now,policy,multiplier,request.Parameters,true);
                        var id=QuantRules.HashValue(new{request.Id,d.Symbol,d.Strategy,d.Direction,Hour=hour,policy,multiplier});
                        trades.Add(new(id,window.Name,d.Symbol,d.Strategy,d.Direction,policy,multiplier,hour,now,source.Hash,seed,lower,upper));
                        if(trades.Count>30000)throw new InvalidDataException("Study case limit exceeded; no partial performance report is published");
                        busy[key]=lower.ClosedAt??hour.AddHours(request.Parameters.HoldHours);
                    }
                }
            }
            progress?.Invoke(++processed);
        }
        var summaries=new List<QuantStudySummary>();
        foreach(var window in request.Windows)foreach(var strategy in new[]{"TrendPullback","Breakout20"})foreach(var policy in new[]{"ScaleTail","FullAtFirst"})foreach(var multiplier in new[]{1,2,3})
        {
            var rows=trades.Where(t=>t.Window==window.Name&&t.Strategy==strategy&&t.Policy==policy&&t.CostMultiplier==multiplier).ToArray();summaries.Add(Summary(window.Name,strategy,policy,multiplier,rows,request.Parameters));
        }
        var gates=new List<QuantStudyGate>
        {
            new("ForwardReproduction",forward.Decisions>0&&forward.Mismatches==0&&forward.MissingInputs==0?"Pass":forward.Decisions==0?"Unknown":"Fail",$"校对{forward.Decisions}条；一致{forward.Matches}，不一致{forward.Mismatches}，缺输入{forward.MissingInputs}"),
            new("ImmutableInputs","Pass","原始接收时间、源摘要、研究版本与分段保留；不改原模拟盘"),
            new("HistoricalMarketContext","Unknown","历史全市场成分、OI、盘口、实际资金费/档位不完整；本报告仅价格情景研究"),
            new("ProspectiveProfitEvidence","Unknown","需之后的新数据、足够日历天与完整交易；历史留出不替代前瞻"),
        };
        foreach(var strategy in new[]{"TrendPullback","Breakout20"})
        {
            var hold=summaries.Single(s=>s.Window=="Holdout"&&s.Strategy==strategy&&s.Policy=="ScaleTail"&&s.CostMultiplier==1);
            var stress=summaries.Single(s=>s.Window=="Holdout"&&s.Strategy==strategy&&s.Policy=="ScaleTail"&&s.CostMultiplier==2);
            var state=hold.Completed<30||hold.SignalDays<10?"Unknown":hold.MeanLower95>0&&hold.ProfitFactor>=1.2m&&stress.MeanNetR>0?"Pass":"Fail";
            gates.Add(new(strategy+"HistoricalHoldout",state,$"留出完整样本{hold.Completed}笔/{hold.SignalDays}个信号日；2倍成本均值R={stress.MeanNetR?.ToString("G6")??"Unknown"}；仅该情景内检验"));
        }
        var report=new QuantStudyReport(request,now,input.Select(i=>i.Source).ToArray(),summaries,counts,forward,gates,
            ["当前名单回看有幸存者及选样偏差；不是历史全市场排行榜样本。", "本报告只重建价格条件，不假装具备当时OI、全市场广度、盘口、保证金档位及完整资金费。", "按下一小时开盘假设成交，未模拟线上120秒窗口、精确队列及延迟；不是线上Quant-Shadow-1的完整成交复现。", "100U无杠杆独立机会样本、连续数量；没有全账户五仓/相关性资金占用，不能相加成账户收益或每天1%达成率。", "净R包含假设手续费、滑点和不利资金费；不等于真实交易成本或实际可实现收益。", "同小时两种顺序分别展示；目标先后可能改变后续持仓路径。置信区间按信号UTC日期整组重采样，短窗口和相关日期仍限制结论。", "缺失路径/未完整观察不强制平仓；剔除未完成样本可能有偏差，须同时查看未完成数量。", "研究没有优化参数；历史开发/验证/留出仍需之后前瞻证据验证，不直接授予实盘或盈利验证状态。"]);
        return(report,trades);
    }
    public static QuantStudySummary Summary(string window,string strategy,string policy,int multiplier,IReadOnlyList<QuantStudyTrade> rows,QuantStudyParameters p)
    {
        var closed=rows.Where(r=>r.Conservative.Status=="Closed"&&r.Conservative.Net is not null&&r.Conservative.InitialRisk>0).ToArray();var values=closed.Select(t=>t.Conservative.Net!.Value/t.Conservative.InitialRisk).ToArray();
        var positives=values.Where(v=>v>0).Sum();var losses=-values.Where(v=>v<0).Sum();
        var high=rows.Where(r=>r.TargetFirst.Status=="Closed"&&r.TargetFirst.Net is not null&&r.TargetFirst.InitialRisk>0).Select(r=>r.TargetFirst.Net!.Value/r.TargetFirst.InitialRisk).ToArray();
        var days=closed.GroupBy(t=>t.SignalHour.UtcDateTime.Date).Select(g=>(Sum:g.Sum(t=>t.Conservative.Net!.Value/t.Conservative.InitialRisk),Count:g.Count())).ToArray();
        decimal? low=null,upper=null;
        if(days.Length>=10&&closed.Length>=30)
        {
            var random=new Random(p.BootstrapSeed);var draws=new decimal[p.BootstrapSamples];
            for(var i=0;i<draws.Length;i++){decimal sum=0;var count=0;for(var j=0;j<days.Length;j++){var d=days[random.Next(days.Length)];sum+=d.Sum;count+=d.Count;}draws[i]=sum/count;}
            Array.Sort(draws);low=draws[(int)(draws.Length*.025)];upper=draws[Math.Min(draws.Length-1,(int)(draws.Length*.975))];
        }
        return new(window,strategy,policy,multiplier,rows.Count,closed.Length,rows.Count-closed.Length,rows.Count(r=>r.Conservative.AmbiguousBars>0||r.TargetFirst.AmbiguousBars>0),values.Length>0?values.Average():null,
            high.Length>0?high.Average():null,losses>0?positives/losses:null,values.Length>0?100m*values.Count(v=>v>0)/values.Length:null,closed.Count(t=>t.Direction=="Long"),closed.Count(t=>t.Direction=="Short"),days.Length,low,upper);
    }
    public static QuantForwardReproduction Audit(IQuantShadowRepository repository,QuantCohort? cohort)
    {
        if(cohort is null)return new(0,0,0,0,0,["前瞻批次未就绪"]);var batches=0;var decisions=0;var matches=0;var mismatches=0;var missing=0;var issues=new List<string>();
        foreach(var hour in repository.History(cohort.RulesHash,48))foreach(var batch in repository.Decisions(cohort.RulesHash,hour.Hour))
        {
            batches++;decisions+=batch.Decisions.Count;try
            {
                var bundle=repository.ReadInput(cohort.RulesHash,batch.InputHash);if(bundle is null){missing+=batch.Decisions.Count;issues.Add(batch.Symbol+" 缺原始输入");continue;}
                foreach(var d in batch.Decisions)
                {
                    var frame=QuantSignalEngine.Frame(d.Symbol,bundle.Candles,d.CapturedAt,cohort.Parameters);var oi=QuantSignalEngine.Oi(d.Symbol,bundle.Oi,d.Hour,d.CapturedAt,cohort.Parameters);
                    var actual=QuantSignalEngine.Analyze(d.Market,frame,oi,d.CapturedAt,"Forward",cohort.RulesHash,batch.InputHash,cohort.Parameters).Single(x=>x.Strategy==d.Strategy&&x.Direction==d.Direction) with{InputArchived=true};
                    if(JsonSerializer.Serialize(actual)==JsonSerializer.Serialize(d))matches++;else{mismatches++;if(issues.Count<20)issues.Add(d.Symbol+" "+d.Strategy+" "+d.Direction+" 重算不同");}
                }
            }
            catch(Exception ex)when(ex is IOException or InvalidDataException or JsonException or InvalidOperationException){missing+=batch.Decisions.Count;if(issues.Count<20)issues.Add(batch.Symbol+" "+ex.GetType().Name);}
        }
        return new(batches,decisions,matches,mismatches,missing,issues.Take(20).ToArray());
    }
}
