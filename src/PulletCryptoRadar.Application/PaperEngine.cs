using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Application;

public static class PaperEngine
{
    public static bool Fresh(MarketView? m,DateTimeOffset now)=>m?.Price>0&&m.MarkPrice>0&&m.PriceSourceTime<=now&&m.PriceReceivedTime<=now
        &&now-m.PriceSourceTime<=TimeSpan.FromSeconds(15)&&now-m.PriceReceivedTime<=TimeSpan.FromSeconds(15)
        &&m.FundingSourceTime<=now&&now-m.FundingSourceTime<=TimeSpan.FromSeconds(15);
    public static DateOnly Day(DateTimeOffset time)=>DateOnly.FromDateTime(time.ToOffset(TimeSpan.FromHours(8)).DateTime);
    public static decimal EstimatedLiquidation(PaperPosition p,PaperRules r)=>p.Direction=="Short"?Math.Max(0,(p.Quantity*p.EntryPrice+p.RemainingMargin)/(p.Quantity*(1+r.MaintenanceRate))):Math.Max(0,(p.Quantity*p.EntryPrice-p.RemainingMargin)/(p.Quantity*(1-r.MaintenanceRate)));
    public static string? Reject(RadarObservation o,DateTimeOffset now)
    {
        var m=o.Input;var a=o.Up;var returns=m.Features.Returns.GetValueOrDefault(60);
        if(!m.Instrument.Active||!Fresh(m,now))return "行情或标记价不新鲜，禁止模拟成交";
        if(a.StrategyVersion!="UpMove-2.0"||a.Status!=DataStatus.Valid||!a.IsCandidate||a.Score<65||a.DataConfidence<70)return "上涨观察尚未满足模拟规则（V2、候选、分数65、完整度70）";
        if(a.Stage is not("Ignition" or "Breakout")||a.Risk>=SignalRisk.High)return "阶段过热、风险过高或不在模拟入场阶段";
        if(a.ConfirmedFamilies.Count<3||a.Components.Any(c=>c.SourceTime>now))return "独立确认不足或存在未来输入";
        if(o.Down.IsCandidate||o.Down.Score>=40)return "存在反向压力，不模拟追入";
        if(returns?.Status!=DataStatus.Valid||returns.Value is null||returns.SourceTime>now||now-returns.SourceTime>TimeSpan.FromSeconds(90))return "1h 涨幅证据缺失，不能判断追高程度";
        if(returns.Value>8||returns.Value<0)return "1h 涨幅不在 0%～8% 假设区间，跳过追高或反向行情";
        return null;
    }
    public static PaperBatch Tick(PaperState state,RadarSnapshot? radar,IReadOnlyDictionary<int,MarketView> markets,DateTimeOffset now)
    {
        if(now<state.UpdatedAt)throw new ArgumentException("模拟时钟不能倒退");
        var rules=state.Rules;var cash=state.Cash;var positions=new List<PaperPosition>();var pending=new List<PaperIntent>();
        var trades=new List<PaperTrade>();var decisions=new List<PaperDecision>();var used=state.UsedSignals.ToHashSet();
        var latest=radar?.Observations.ToDictionary(o=>o.Input.Instrument.AssetId)??[];
        foreach(var old in state.Positions)
        {
            var m=markets.GetValueOrDefault(old.AssetId);if(!Fresh(m,now)||m!.PriceSourceTime<=old.LastQuoteTime){positions.Add(old);continue;}
            var periods=Math.Max(0,now.ToUnixTimeSeconds()/28800-old.LastFundingAt.ToUnixTimeSeconds()/28800);
            var funding=Math.Min(old.RemainingMargin,old.Quantity*old.EntryPrice*rules.FundingCost8h*periods);
            var p=old with{RemainingMargin=old.RemainingMargin-funding,FundingCost=old.FundingCost+funding,LastFundingAt=now,LastMark=m.MarkPrice!.Value,LastQuoteTime=m.PriceSourceTime!.Value};
            var markPnl=p.Quantity*(p.LastMark-p.EntryPrice);var liquidated=p.RemainingMargin+markPnl<=rules.MaintenanceRate*p.Quantity*p.LastMark;
            var reverse=radar?.AsOf<=now&&now-radar.AsOf<TimeSpan.FromSeconds(90)&&latest.GetValueOrDefault(p.AssetId)?.Down.IsCandidate==true;
            var reason=liquidated?"模拟强平（估算维持保证金）":m.Price<=p.StopPrice?"模拟止损":m.Price>=p.TakePrice?"模拟止盈":reverse?"反向候选退出":now-p.OpenedAt>=TimeSpan.FromHours(rules.MaximumHoldHours)?"持有超时退出":null;
            if(reason is null){positions.Add(p);continue;}
            var fill=m.Price!.Value*(1-rules.SlippageRate);var gross=p.Quantity*(fill-p.EntryPrice);var fee=p.Quantity*fill*rules.FeeRate;
            // Isolated paper model caps the returned margin at zero. This is not an exchange insolvency guarantee.
            var returned=liquidated?0:Math.Max(0,p.RemainingMargin+gross-fee);cash+=returned;
            var trade=new PaperTrade(p.Id,p.SignalKey,p.AssetId,p.Symbol,p.OpenedAt,now,p.EntryPrice,fill,p.Quantity,p.InitialMargin,rules.Leverage,gross,p.OpenFee+fee,p.FundingCost,returned-p.InitialMargin-p.OpenFee,reason,
                now-old.LastQuoteTime>TimeSpan.FromSeconds(30),rules.Version,p.Intent){Bucket=p.Bucket,SettlementAdjustment=returned-(p.RemainingMargin+gross-fee)};
            trades.Add(trade);decisions.Add(new(now,p.Symbol,"模拟平仓",reason+"；按下一可用报价含滑点成交，不假设能成交在止损线",p.SignalKey));
        }
        var today=state.RecentTrades.Concat(trades).Where(t=>Day(t.ClosedAt)==Day(now)).ToArray();
        var unrealized=positions.Sum(p=>Math.Max(-p.RemainingMargin,p.Quantity*(p.LastMark-p.EntryPrice)));
        var dailyBlocked=today.Sum(t=>t.NetPnl)+unrealized-positions.Sum(p=>p.OpenFee+p.FundingCost)<=-rules.DailyLossLimit||today.Length>=5;
        foreach(var intent in state.Pending)
        {
            var m=markets.GetValueOrDefault(intent.AssetId);
            if(!state.Enabled||!rules.ResearchValidated||dailyBlocked||now-intent.CreatedAt>TimeSpan.FromMinutes(2)||positions.Count>=rules.MaximumPositions)
            {decisions.Add(new(now,intent.Symbol,"取消模拟意向","已暂停、超时、仓位上限或当日风控限制",intent.Key));continue;}
            if(!Fresh(m,now)||m!.PriceSourceTime<=intent.CreatedAt||m.PriceReceivedTime<=intent.CreatedAt){pending.Add(intent);continue;}
            var current=latest.GetValueOrDefault(intent.AssetId);
            if(current is null||radar?.AsOf>now||now-radar?.AsOf>TimeSpan.FromSeconds(90)||Reject(current,now) is {} reason)
            {decisions.Add(new(now,intent.Symbol,"取消模拟意向","执行时信号或数据门槛已失效",intent.Key));continue;}
            if(intent.Evidence is null||m.Price>intent.Evidence.Input.Price*1.01m){decisions.Add(new(now,intent.Symbol,"跳过","信号后已上涨超过1%或原始证据缺失，不假设按旧价成交",intent.Key));continue;}
            var entry=m.Price!.Value*(1+rules.SlippageRate);var notional=rules.MarginPerTrade*rules.Leverage;var qty=notional/entry;var fee=notional*rules.FeeRate;
            if(cash<rules.MarginPerTrade+fee){decisions.Add(new(now,intent.Symbol,"跳过","可用模拟余额不足保证金及手续费",intent.Key));continue;}
            cash-=rules.MarginPerTrade+fee;
            positions.Add(new(Guid.NewGuid(),intent.Key,intent.AssetId,intent.Symbol,now,entry,qty,rules.MarginPerTrade,rules.MarginPerTrade,fee,0,now,m.MarkPrice!.Value,m.PriceSourceTime!.Value,
                entry*(1-rules.StopPercent/100),entry*(1+rules.TakePercent/100),intent){Bucket=Enumerable.Range(1,10).First(n=>!positions.Any(p=>p.Bucket==n))});
            decisions.Add(new(now,intent.Symbol,"模拟开仓","100 USDT 逐仓保证金 ×10；风险未知项随证据保留",intent.Key));
        }
        var scanId=state.LastScanId;
        if(radar is not null&&radar.ScanId!=scanId&&radar.AsOf>=state.StartedAt&&radar.AsOf<=now&&now-radar.AsOf<=TimeSpan.FromSeconds(90))
        {
            scanId=radar.ScanId;
            foreach(var o in radar.Observations.Where(o=>o.Up.IsCandidate).OrderByDescending(o=>o.Up.Score).Take(10))
            {
                var key=$"{o.Input.Instrument.AssetId}:{o.EpisodeId}:{o.Up.StrategyVersion}";
                if(used.Contains(key)||positions.Any(p=>p.AssetId==o.Input.Instrument.AssetId)||pending.Any(p=>p.AssetId==o.Input.Instrument.AssetId))continue;
                var reason=Reject(o,now);
                if(reason is not null){decisions.Add(new(now,o.Input.Instrument.Symbol,"跳过",reason,key));continue;}
                if(!state.Enabled||!rules.ResearchValidated||dailyBlocked||positions.Count+pending.Count>=rules.MaximumPositions)continue;
                if(o.EpisodeId is null){decisions.Add(new(now,o.Input.Instrument.Symbol,"跳过","缺少独立 Episode，无法保证不重复开仓",key));continue;}
                used.Add(key);pending.Add(new(key,radar.ScanId,o.Input.Instrument.AssetId,o.Input.Instrument.Symbol,now,o));
                decisions.Add(new(now,o.Input.Instrument.Symbol,"等待下一报价","研究候选满足模拟假设，等待信号之后的新鲜行情；尚未成交",key));
            }
        }
        var complete=positions.All(p=>Fresh(markets.GetValueOrDefault(p.AssetId),now));
        var equity=cash+positions.Sum(p=>Math.Max(0,p.RemainingMargin+p.Quantity*(p.LastMark-p.EntryPrice)));
        var curve=state.Curve.ToList();if(curve.Count==0||now-curve[^1].Time>=TimeSpan.FromMinutes(1))curve.Add(new(now,equity,complete));
        var next=state with{Revision=state.Revision+1,UpdatedAt=now,Cash=cash,LastScanId=scanId,Positions=positions,Pending=pending,
            UsedSignals=used.Order().ToArray(),RecentTrades=state.RecentTrades.Concat(trades).TakeLast(100).ToArray(),Decisions=state.Decisions.Concat(decisions).TakeLast(40).ToArray(),Curve=curve.TakeLast(1440).ToArray()};
        return new(next,state.Revision,trades);
    }
}
