using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Application;

public sealed class PaperService(IPaperRepository repository,IPaperPersistence persistence,IMarketRuntimeStore market,IRadarSnapshotSource radar,TimeProvider time)
{
    private readonly SemaphoreSlim _gate=new(1,1);private PaperState? _state;private IReadOnlyList<PaperTrade> _trades=[];
    private async Task Ensure(CancellationToken ct)
    {
        if(_state is not null)return;_trades=repository.Trades();var restored=repository.Restore();if(restored is not null){_state=restored;return;}
        var now=time.GetUtcNow();var initial=new PaperState{Revision=1,StartedAt=now,UpdatedAt=now,Decisions=[new(now,"账户","初始化","1000 USDT；10份，每笔100保证金，10倍逐仓；仅模拟")],Curve=[new(now,1000,true)]};
        await persistence.SaveAsync(new(initial,0,[]),ct);_state=initial;
    }
    public async Task TickAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);try{await Ensure(ct);var batch=PaperEngine.Tick(_state!,radar.GetSnapshot(),market.GetAll().ToDictionary(m=>m.Instrument.AssetId),time.GetUtcNow());await persistence.SaveAsync(batch,ct);_state=batch.State;if(batch.Trades.Count>0)_trades=batch.Trades.Concat(_trades).ToArray();}finally{_gate.Release();}
    }
    public async Task SetEnabledAsync(bool enabled,CancellationToken ct)
    {
        if(enabled)throw new ArgumentException("两套策略的历史研究、结构止损及分批退出尚未完成验收，禁止启用原型自动开仓");
        await _gate.WaitAsync(ct);try{await Ensure(ct);var old=_state!;var now=time.GetUtcNow();var next=old with{Enabled=enabled,Revision=old.Revision+1,UpdatedAt=now,Pending=enabled?old.Pending:[],Decisions=old.Decisions.Append(new(now,"账户",enabled?"恢复模拟":"暂停新仓","仅影响虚拟新仓；已有仓位继续风险退出")).TakeLast(40).ToArray()};await persistence.SaveAsync(new(next,old.Revision,[]),ct);_state=next;}finally{_gate.Release();}
    }
    public async Task<PaperOverview> OverviewAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);try
        {
            await Ensure(ct);var s=_state!;var now=time.GetUtcNow();var cash=s.Cash;var equity=cash;var complete=true;
            foreach(var p in s.Positions){var quote=market.Get(p.AssetId);var fresh=PaperEngine.Fresh(quote,now);complete&=fresh;var mark=fresh?quote!.MarkPrice!.Value:p.LastMark;equity+=Math.Max(0,p.RemainingMargin+p.Quantity*(mark-p.EntryPrice));}
            var margin=s.Positions.Sum(p=>p.InitialMargin);var realized=_trades.Sum(t=>t.NetPnl);var wins=_trades.Count(t=>t.NetPnl>0);
            var days=_trades.GroupBy(t=>PaperEngine.Day(t.ClosedAt)).Select(g=>new PaperDaily(g.Key.ToString("yyyy-MM-dd"),g.Count(),g.Count(t=>t.NetPnl>0),g.Sum(t=>t.NetPnl),g.Sum(t=>t.Fees),g.Sum(t=>t.FundingCost),
                _trades.Count<30?"样本不足30笔：只复盘，不据单笔或单日修改规则":"已有初步样本：比较回撤与成本；新规则须另开版本前瞻验证，不改写本账本")).OrderByDescending(d=>d.Date).Take(30).ToList();
            if(!days.Any(d=>d.Date==PaperEngine.Day(now).ToString("yyyy-MM-dd")))days.Insert(0,new(PaperEngine.Day(now).ToString("yyyy-MM-dd"),0,0,0,0,0,"今天尚无已平仓交易；等待符合规则的实时信号，不补做历史成交"));
            return new(s,equity,complete?equity:null,realized,complete?equity-cash-s.Positions.Sum(p=>p.RemainingMargin):null,margin,_trades.Sum(t=>t.Fees)+s.Positions.Sum(p=>p.OpenFee),_trades.Count,wins,_trades.Count>0?100m*wins/_trades.Count:null,
                "虚拟 USDT 线性多头 / 逐仓估算；行情来自 Binance 永续，非真实委托或成交",days);
        }finally{_gate.Release();}
    }
}
