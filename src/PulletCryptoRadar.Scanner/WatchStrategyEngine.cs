using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Scanner;

public sealed class WatchOptions
{
    public int IntervalSeconds { get;set; }=15;
    public int AlertCooldownMinutes { get;set; }=30;
    public decimal SevereDrop15m { get;set; }=-15;
    public decimal SevereDrop1h { get;set; }=-25;
    public decimal ExtremeFunding8h { get;set; }=.003m;
    public decimal CrowdedLongShare { get;set; }=.85m;
    public bool SeedAriaExample { get;set; }=true;
    public bool IsValid()=>IntervalSeconds is >=5 and <=60&&AlertCooldownMinutes is >=10 and <=1440&&SevereDrop15m<0&&SevereDrop1h<SevereDrop15m
        &&ExtremeFunding8h>0&&CrowdedLongShare is >.5m and <1m;
}
public sealed class WatchStrategyEngine(WatchOptions options)
{
    public const string Version="WatchTower-1.0";
    public WatchResult Evaluate(WatchInput input,DateTimeOffset now)
    {
        var q=input.Quote;var config=input.Config;var positive=new List<string>();var risks=new List<string>();
        var missing=new List<string>{"OnChainScore / Holder / 筹码集中度尚未采集","WalletRiskScore / 项目钱包 / 交易所流入尚未采集"};
        bool Fresh<T>(FeatureValue<T> f,int seconds)where T:struct=>f.Status==DataStatus.Valid&&f.Value is not null&&f.SourceTime<=now&&now-f.SourceTime<=TimeSpan.FromSeconds(seconds);
        decimal? D(FeatureValue<decimal> f,int seconds=660)=>Fresh(f,seconds)?f.Value:null;
        var valid=config.Enabled&&q.VenueIdentityVerified&&Fresh(q.Price,q.Exchange=="Binance"?30:180)&&q.Price.Value>0;
        var state=StrategyState.WaitConfirmation;var risk=SignalRisk.Unknown;var trend="Unknown";var stage="Unknown";
        var r1=D(q.Return1h);var r15=q.Exchange=="Binance"?D(q.Market.Features.Returns.GetValueOrDefault(15)??FeatureValue<decimal>.Missing()):null;
        var oi=D(q.OiChange15m);var funding=D(q.FundingRate8h,q.Exchange=="Binance"?30:180);var longShare=D(q.LongShare);
        var vz=Fresh(q.VolumeRobustZ,660)?q.VolumeRobustZ.Value:null;
        var down=input.Down is {Status:DataStatus.Valid} d?d:null;var up=input.Up is {Status:DataStatus.Valid} u?u:null;
        var confidence=0;double? trendScore=null;double? derivativeScore=null;double? crowd=null;
        decimal? costReturn=null,toCost=null;
        var next=new List<string>();
        if(valid)
        {
            confidence=20;state=StrategyState.Observe;
            if(config.CostPrice>0)
            {try{costReturn=(q.Price.Value/config.CostPrice-1)*100;toCost=(config.CostPrice/q.Price.Value-1)*100;}catch(OverflowException){missing.Add("成本距离超出 decimal 范围");}}
            if(r1 is not null){trend=r1>1?"Up":r1< -1?"Down":"Sideways";trendScore=Math.Clamp(50+(double)r1.Value*2,0,100);confidence+=15;}
            else missing.Add("本交易所 1h 收益缺失或过期，趋势保持 Unknown");
            if(oi is not null){derivativeScore=Math.Clamp(50+(double)oi.Value*5,0,100);confidence+=15;positive.Add($"{q.Exchange} OI 数量 15m {oi:0.00}%（增加不直接代表多头资金流入）");}
            else missing.Add("本交易所 15m OI 变化未知");
            if(vz is not null){confidence+=10;if(vz>=2)positive.Add($"同交易所成交 RZ {vz:0.00}");}else missing.Add("同交易所成交异常基线未知");
            if(funding is not null||longShare is not null){confidence+=10;crowd=Math.Max(funding is null?0:Math.Clamp((double)Math.Abs(funding.Value/options.ExtremeFunding8h),0,1),longShare is null?0:Math.Clamp((double)((longShare.Value-.5m)/(options.CrowdedLongShare-.5m)),0,1))*100;}
            else missing.Add("标准化 Funding 与多头拥挤数据未知");
            stage=down?.IsCandidate==true?down.Stage:up?.Stage??"Observing";
            var severe=(r15<=options.SevereDrop15m||r1<=options.SevereDrop1h)&&oi<=-8;
            var crash=down?.Stage=="Crash"&&down.ConfirmedFamilies.Count>=2;
            var systemic=input.Market is {Status:DataStatus.Valid,Regime:MarketRegime.SystemicCrash} market&&market.AsOf<=now&&now-market.AsOf<=TimeSpan.FromSeconds(90)&&r1<=-5;
            var supportBroken=config.SupportPrice>0&&q.Price.Value<config.SupportPrice*.999m;
            var distribution=r1>5&&oi< -3;
            var high=down?.Stage=="Selloff"&&down.ConfirmedFamilies.Count>=2||supportBroken&&oi<=-3;
            if(severe||crash||systemic){state=StrategyState.CriticalRisk;risk=SignalRisk.Critical;stage="Crash";risks.Add(systemic?"系统性下跌且本标的 1h 跌幅至少 5%":"价格严重下跌与独立 OI / 下跌确认共同出现");}
            else if(high){state=StrategyState.ReduceRisk;risk=SignalRisk.High;risks.Add("支撑/下跌结构与衍生品风险共同确认");}
            else if(supportBroken||distribution||r1< -5&&oi>5){state=StrategyState.RiskIncreasing;risk=SignalRisk.High;risks.Add(supportBroken?"跌破手动支撑（含 0.1% 缓冲）":distribution?"价格走高但 OI 减少，需核对减仓/派发风险":"下跌伴随 OI 增长，需关注新增杠杆风险");}
            else if(funding is {} f&&Math.Abs(f)>=options.ExtremeFunding8h||longShare>=options.CrowdedLongShare||r1>=20)
            {state=StrategyState.AvoidChase;risk=SignalRisk.High;stage="Overheated";risks.Add("短期涨幅、标准化 Funding 或多头拥挤达到过热阈值");}
            else if(r1>1&&oi>=3&&vz>=2){state=StrategyState.PositiveObserve;positive.Add("价格、OI 与成交量三类已观测证据改善");}
            else if(r1>1&&config.CostPrice>0){state=StrategyState.HoldObserve;positive.Add("已有成本观察项的已知趋势尚未转弱，链上和钱包风险仍未知");}
            else if(up?.IsCandidate==true||down?.IsCandidate==true){state=StrategyState.WaitConfirmation;positive.Add("检测到异动，但独立确认仍不足");}
            // Unknown on-chain/wallet inputs prevent an overall Low risk label.
            if(risk==SignalRisk.Unknown)risks.Add("尚不能给出整体低风险结论：链上与钱包风险未知");
        }
        else
        {missing.Add(!config.Enabled?"此项监控已暂停":!q.VenueIdentityVerified?"备用交易所身份待核对，成本比较和策略判断暂停":"价格缺失、未来或过期，停止策略与价格提醒");}
        if(config.TargetPrice is {} target)next.Add($"目标价 {target}：新鲜同源价格上穿时记录个人提醒");
        if(config.AttentionPrice is {} attention)next.Add($"关注价 {attention}：新鲜同源价格上穿时记录个人提醒");
        if(config.SupportPrice is {} support)next.Add($"关键支撑 {support}：关注跌破及 OI 是否共同恶化");
        if(config.ResistancePrice is {} resistance)next.Add($"关键压力 {resistance}：观察突破及成交确认");
        if(next.Count==0)next.Add("可配置关注价、目标价、支撑或压力；目前继续等待同交易所趋势与 OI 确认");
        next.Add("补齐资产身份、链上与钱包证据；状态仅用于研究观察，不执行交易");
        return new(Version,now,config.AssetId,q.Market.Instrument.Symbol,q.Exchange,q.NativeSymbol,valid?DataStatus.Valid:DataStatus.Missing,
            q.Price,costReturn,toCost,trend,stage,state,risk,confidence,trendScore,derivativeScore,null,null,crowd,positive,risks,missing,next);
    }
    public (WatchCheckpoint Checkpoint,NotificationRecord? Notification) Advance(WatchInput input,WatchResult result,WatchCheckpoint? prior,DateTimeOffset now)
    {
        if(result.Status!=DataStatus.Valid)return(new WatchCheckpoint(input,result,prior?.ActiveCondition,prior?.EpisodeId??Guid.NewGuid(),prior?.LastNotificationAt){LastNotifiedCondition=prior?.LastNotifiedCondition},null);
        var previous=prior?.Result;var samePriceBasis=prior?.Input.Config.Revision==input.Config.Revision&&previous?.Exchange==result.Exchange&&previous.NativeSymbol==result.NativeSymbol
            &&previous.Status==DataStatus.Valid&&previous.Price.SourceTime<=now&&now-previous.Price.SourceTime<=TimeSpan.FromMinutes(3);
        bool Cross(decimal? level)=>level>0&&samePriceBasis&&previous!.Price.Value<level&&result.Price.Value>=level;
        var condition=result.Risk==SignalRisk.Critical?"CriticalRisk":result.State==StrategyState.ReduceRisk?"ReduceRisk":result.State==StrategyState.RiskIncreasing?"RiskIncreasing":
            Cross(input.Config.TargetPrice)?"TargetCross":Cross(input.Config.AttentionPrice)?"AttentionCross":Cross(input.Config.ResistancePrice)?"ResistanceCross":null;
        var episode=condition==prior?.ActiveCondition?prior?.EpisodeId??Guid.NewGuid():Guid.NewGuid();
        var cooldown=prior?.LastNotificationAt is {} last&&now-last<TimeSpan.FromMinutes(options.AlertCooldownMinutes);
        var escalation=condition=="CriticalRisk"&&prior?.LastNotifiedCondition!="CriticalRisk";
        var notify=input.Config.EnableAlerts&&condition is not null&&condition!=prior?.ActiveCondition&&(!cooldown||escalation);
        NotificationRecord? notification=notify?new(Guid.NewGuid(),$"Watch:{input.Config.AssetId}:{episode}:{condition}",condition=="CriticalRisk"?NotificationKind.WatchListCritical:NotificationKind.WatchListPersonal,null,input.Config.AssetId,
            result.Symbol,result.Risk>=SignalRisk.High?MoveDirection.Down:MoveDirection.Up,SignalGrade.Unknown,null,result.State.ToString(),now,now.AddSeconds(120),NotificationStatus.Pending,
            $"{result.Symbol} · 自选监控 · {condition}",$"{result.Exchange}/{result.NativeSymbol} 价格 {result.Price.Value}，状态 {result.State}，风险 {result.Risk}。{string.Join("；",result.RiskReasons.Take(2))} 仅作研究观察。",StrategyVersion:result.Version):null;
        return(new WatchCheckpoint(input,result,condition,episode,notification is null?prior?.LastNotificationAt:now){LastNotifiedCondition=notification is null?prior?.LastNotifiedCondition:condition},notification);
    }
}
