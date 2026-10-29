using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Scanner;

public static class OnChainEvidence
{
    public static bool Fresh<T>(FeatureValue<T> f,DateTimeOffset now)where T:struct=>f.Status==DataStatus.Valid&&f.Value is not null&&f.SourceTime<=now&&now-f.SourceTime<=TimeSpan.FromMinutes(45);
    public static decimal? Concentration(OnChainFeatures f,DateTimeOffset now)=>f.TrustedIdentity&&f.ConcentrationUsable&&Fresh(f.AdjustedTop10Percent,now)?f.AdjustedTop10Percent.Value:null;
    public static decimal? HolderChange(OnChainFeatures f,DateTimeOffset now)=>f.TrustedIdentity&&Fresh(f.HolderChange24hPercent,now)?f.HolderChange24hPercent.Value:null;
}
public sealed class SpeculativeOnChainDetector(SpeculativeOptions options,SpeculativeDetector basic)
{
    public const string Version="Speculative-1.1";
    public SpeculativeAssessment Detect(SpeculativeInput input,DateTimeOffset now)
    {
        var old=basic.Detect(input,now);var chain=input.Market.Features.OnChain;
        var concentration=OnChainEvidence.Concentration(chain,now);var change=OnChainEvidence.HolderChange(chain,now);
        double? C(decimal? value,double full)=>value is null?null:Math.Clamp((double)value.Value/full,0,1);
        ScoreComponent Update(ScoreComponent c)=>c.Family switch
        {
            "Concentration" or "HolderConcentration"=>c with{Strength=C(concentration,80),Confirmed=concentration>=40,Description="经分类覆盖门槛验证的 AdjustedTop10，非项目方控制比例",SourceTime=chain.AdjustedTop10Percent.SourceTime},
            "HolderChange"=>c with{Strength=C(change is null?null:Math.Abs(change.Value),5),Confirmed=Math.Abs(change??0)>=2.5m,Description="同提供者、同链同合约约 24h 人数变化绝对值",SourceTime=chain.HolderChange24hPercent.SourceTime},
            "HolderDecline"=>c with{Strength=C(change is null?null:Math.Max(0,-change.Value),5),Confirmed=change<=-2.5m,Description="同提供者 24h 持币人数减少；缺基线不补零",SourceTime=chain.HolderChange24hPercent.SourceTime},_=>c
        };
        var anomaly=old.AnomalyComponents.Select(Update).ToArray();var risk=old.RiskComponents.Select(Update).ToArray();
        bool Fresh(FeatureValue<decimal> f,int seconds)=>f.Status==DataStatus.Valid&&f.Value is not null&&f.SourceTime<=now&&now-f.SourceTime<=TimeSpan.FromSeconds(seconds);
        var cap=input.Market.Features.Capital;var mc=cap.MarketCapUsd.Value;
        var necessary=input.VenueIdentityVerified&&cap.TrustedIdentity&&Fresh(cap.MarketCapUsd,3600)&&mc>0
            &&Fresh(input.Price,input.Exchange=="Binance"?30:180)&&input.Price.Value>0&&Fresh(input.OpenInterestUsd,660)&&input.OpenInterestUsd.Value>=0
            &&Fresh(input.Volume24hUsd,input.Exchange=="Binance"?30:180)&&input.Volume24hUsd.Value>=0;
        int Coverage(ScoreComponent[] components)=>(int)components.Where(c=>c.Strength is not null).Sum(c=>c.Weight);
        double? Score(ScoreComponent[] components)=>necessary&&Coverage(components)>=options.MinimumCoverage?Math.Round(components.Sum(c=>c.Weight*(c.Strength??0)),2):null;
        var score=Score(anomaly);var riskScore=Score(risk);var overheated=risk.Any(c=>c.Strength>=1&&c.Family is "ExtremeFunding" or "ExtremeOiMarketCap" or "Overheating");
        var candidate=score>=options.EntryScore&&mc>=options.MinMarketCapUsd&&mc<=options.MaxMarketCapUsd&&input.Volume24hUsd.Value>=options.MinVolume24hUsd&&input.OpenInterestUsd.Value>=options.MinOpenInterestUsd
            &&anomaly.Count(c=>c.Confirmed&&c.Family is "VolumeMarketCap" or "OiMarketCap" or "OiAnomaly" or "PriceVolume")>=2&&!overheated;
        return old with{StrategyVersion=Version,AnomalyScore=score,RiskScore=riskScore,AnomalyCoverage=Coverage(anomaly),RiskCoverage=Coverage(risk),
            Stage=score is null?"Unknown":overheated?"Overheated":candidate?"Ignition":"Observing",IsCandidate=candidate,Status=score is null?DataStatus.Missing:DataStatus.Valid,
            AnomalyComponents=anomaly,RiskComponents=risk,MissingFamilies=anomaly.Concat(risk).Where(c=>c.Strength is null).Select(c=>c.Family).Distinct().ToArray(),
            Reason=old.Reason+"；V1.1 链上部分仅使用可信身份、有效时间与覆盖通过的字段，钱包行为仍未知"};
    }
}
public sealed class WatchOnChainStrategyEngine(WatchStrategyEngine basic)
{
    public const string Version="WatchTower-1.1";
    public WatchResult Evaluate(WatchInput input,DateTimeOffset now)
    {
        var result=basic.Evaluate(input,now);var chain=input.Quote.Market.Features.OnChain;
        var concentration=OnChainEvidence.Concentration(chain,now);var change=OnChainEvidence.HolderChange(chain,now);
        var missing=result.MissingEvidence.Where(s=>!s.StartsWith("OnChainScore",StringComparison.Ordinal)).ToList();
        if(concentration is null)missing.Add("链上集中度缺少可信身份、新鲜数据或至少 50% 的已核对标签覆盖");
        if(change is null)missing.Add("尚无同索引器约 24h 的持币人数变化基线");
        double? score=concentration is null&&change is null?null:Math.Round((concentration is null?0:60*(1-Math.Clamp((double)concentration.Value/100,0,1)))
            +(change is null?0:40*Math.Clamp(((double)change.Value+5)/10,0,1)),2);
        var risk=result.Risk;var state=result.State;var reasons=result.RiskReasons.Where(r=>!r.StartsWith("尚不能给出整体低风险",StringComparison.Ordinal)).ToList();var positive=result.PositiveReasons.ToList();var confidence=result.Confidence;
        reasons.Add("钱包行为仍未知；已知链上分布不等于整体低风险");
        if(result.Status==DataStatus.Valid)
        {
            confidence+=(concentration is null?0:10)+(change is null?0:5);
            if(change>0)positive.Add($"同索引器持币人数约 24h 增长 {change:0.00}%（不代表每个地址都是独立用户）");
            if(concentration>=80||change<=-5)
            {
                reasons.Add(concentration>=80?"经标签口径调整后 Top10 占比较高；不是项目方控制比例":"持币地址数约 24h 减少至少 5%，需核对数据与筹码变化");
                if(risk!=SignalRisk.Critical){risk=SignalRisk.High;state=concentration>=80&&change<=-5?StrategyState.ReduceRisk:state==StrategyState.ReduceRisk?state:StrategyState.RiskIncreasing;}
            }
        }
        return result with{Version=Version,OnChainScore=result.Status==DataStatus.Valid?score:null,State=state,Risk=risk,Confidence=confidence,
            MissingEvidence=missing,RiskReasons=reasons,PositiveReasons=positive,NextTriggers=result.NextTriggers.Concat(["在链上页面核对标签覆盖与历史人数，钱包转账仍由后续阶段实现"]).ToArray()};
    }
}
