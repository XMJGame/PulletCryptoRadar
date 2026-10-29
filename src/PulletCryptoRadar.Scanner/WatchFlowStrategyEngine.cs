using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Scanner;

public sealed class WatchFlowStrategyEngine(WatchOnChainStrategyEngine basic)
{
    public const string Version="WatchTower-1.2";
    public WatchResult Evaluate(WatchInput input,DateTimeOffset now)
    {
        var old=basic.Evaluate(input,now);var f=input.Flow;var risks=old.RiskReasons.Where(s=>!s.StartsWith("钱包行为仍未知",StringComparison.Ordinal)).ToList();
        var missing=old.MissingEvidence.Where(s=>!s.StartsWith("WalletRiskScore",StringComparison.Ordinal)).ToList();
        var usable=f.TrustedIdentity&&f.Fresh&&f.ThroughTime<=now&&now-f.ThroughTime<=TimeSpan.FromMinutes(10);
        var direct=usable&&f.ProjectToExchangeSupplyPercent>=.01m;var outflow=usable&&f.ProjectOutflowSupplyPercent>=.1m;
        var risk=old.Risk;var state=old.State;double? wallet=null;
        if(direct||outflow)
        {
            wallet=direct?80:65;
            risks.Add(direct?$"最近 1h 已观测已核对项目类地址直接转入交易所，占总供应量下界 {f.ProjectToExchangeSupplyPercent:0.####}%；转入不证明卖出":$"最近 1h 已观测已核对项目类地址流出，占总供应量下界 {f.ProjectOutflowSupplyPercent:0.####}%");
            if(old.Status==DataStatus.Valid&&risk!=SignalRisk.Critical){risk=SignalRisk.High;state=state==StrategyState.ReduceRisk?state:StrategyState.RiskIncreasing;}
        }
        else missing.Add("钱包风险 Unknown：可能缺少新鲜采集、已核对归属或达到阈值的已观测事件；不能据此判定没有风险");
        risks.Add("资金流仅为本地观测下界，交易所转入不是卖出证明，多跳归属仍未知");
        return old with{Version=Version,WalletRiskScore=old.Status==DataStatus.Valid?wallet:null,Risk=risk,State=state,RiskReasons=risks,MissingEvidence=missing,
            NextTriggers=old.NextTriggers.Where(t=>!t.Contains("钱包转账仍由后续阶段实现",StringComparison.Ordinal)).Concat(["查看资金流页面的交易、区块范围及地址标签证据"]).ToArray()};
    }
}
