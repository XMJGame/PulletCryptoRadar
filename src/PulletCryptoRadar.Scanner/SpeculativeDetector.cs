using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Scanner;

public sealed class SpeculativeDetector(SpeculativeOptions options)
{
    public const string Version = "Speculative-1.0";
    public SpeculativeAssessment Detect(SpeculativeInput input, DateTimeOffset now)
    {
        var cap = input.Market.Features.Capital;
        bool Fresh<T>(FeatureValue<T> value, int seconds) where T : struct => value.Status == DataStatus.Valid && value.Value is not null
            && value.SourceTime <= now && now - value.SourceTime <= TimeSpan.FromSeconds(seconds);
        decimal? D(FeatureValue<decimal> f, int seconds = 660) => Fresh(f, seconds) ? f.Value : null;
        double? Z(FeatureValue<double> f) => Fresh(f, 660) && double.IsFinite(f.Value!.Value) ? f.Value : null;
        var identity = input.VenueIdentityVerified && cap.TrustedIdentity;
        var mc = identity && Fresh(cap.MarketCapUsd, 3600) && cap.MarketCapUsd.Value > 0 ? cap.MarketCapUsd.Value : null;
        var price = D(input.Price, input.Exchange == "Binance" ? 30 : 180); if(price<=0)price=null;
        var vol = D(input.Volume24hUsd, input.Exchange == "Binance" ? 30 : 180); if(vol<0)vol=null;
        var oi = D(input.OpenInterestUsd); if(oi<0)oi=null;
        decimal? Ratio(decimal? n) { try { return n >= 0 && mc > 0 ? n / mc : null; } catch (OverflowException) { return null; } }
        var vm = Ratio(vol); var om = Ratio(oi); var fdv = identity ? D(cap.FdvUsd, 3600) : null; var fm = Ratio(fdv);
        var oz = Z(input.OiRobustZ); var pz = Z(input.PriceRobustZ); var vz = Z(input.VolumeRobustZ);
        var funding = D(input.FundingRate8h, input.Exchange == "Binance" ? 30 : 180);
        var top = D(input.TopPositionChange15m); var crowd = D(input.LongShare);
        var r1h = D(input.Return1h); var r24 = D(input.Return24h, input.Exchange == "Binance" ? 30 : 180);
        double Clamp(double x) => Math.Clamp(x, 0, 1);
        double? Scale(decimal? value, decimal full) => value is null ? null : Clamp((double)value.Value / (double)full);
        ScoreComponent C(string name, int weight, double? strength, string description, DateTimeOffset? source)
            => new(name, weight, strength, strength >= 0.5, description, source);
        DateTimeOffset? Oldest(DateTimeOffset? a, DateTimeOffset? b) => a is null || b is null ? null : a < b ? a : b;
        var a = new List<ScoreComponent>
        {
            C("LowMarketCap",10,mc is null?null:Clamp(1-Math.Log((double)(mc.Value/options.MinMarketCapUsd))/Math.Log((double)(options.MaxMarketCapUsd/options.MinMarketCapUsd))),"经核对市值；对数尺度，低市值特征不等于低风险",cap.MarketCapUsd.SourceTime),
            C("VolumeMarketCap",15,Scale(vm,options.VolumeRatioFull),$"{input.Exchange} 永续 24h 成交额/市值 {vm:0.0000}",Oldest(input.Volume24hUsd.SourceTime,cap.MarketCapUsd.SourceTime)),
            C("OiMarketCap",20,Scale(om,options.OiRatioFull),$"{input.Exchange} OI/市值 {om:0.0000}",Oldest(input.OpenInterestUsd.SourceTime,cap.MarketCapUsd.SourceTime)),
            C("OiAnomaly",15,oz is null?null:Clamp(oz.Value/3),$"同交易所 OI 数量变化 RZ {oz:0.00}",input.OiRobustZ.SourceTime),
            C("PriceVolume",10,pz is null||vz is null?null:(Clamp(Math.Abs(pz.Value)/3)+Clamp(vz.Value/3))/2,"同交易所价格/成交异常，不拼接其他交易所基线",Oldest(input.PriceRobustZ.SourceTime,input.VolumeRobustZ.SourceTime)),
            C("Funding",5,Scale(funding is null?null:Math.Abs(funding.Value),options.ExtremeFunding8h),"资金费率按 8h 标准化，仅作异常强度",input.FundingRate8h.SourceTime),
            C("TopTrader",5,Scale(top is null?null:Math.Abs(top.Value),3),"Top Position 15m 占比变化（百分点），不是链上筹码集中度",input.TopPositionChange15m.SourceTime),
            C("Concentration",10,null,"链上筹码集中度尚未采集",null),C("HolderChange",5,null,"Holder 变化尚未采集",null),C("Wallet",5,null,"钱包行为尚未采集",null)
        };
        var risk = new List<ScoreComponent>
        {
            C("Overheating",15,new[]{Scale(r1h is null?null:Math.Abs(r1h.Value),20),Scale(r24 is null?null:Math.Abs(r24.Value),50)}.Max(),"已观测 1h/24h 涨跌幅绝对值；缺失周期不补值，不代表后续方向",input.Return1h.SourceTime is null?input.Return24h.SourceTime:input.Return24h.SourceTime is null?input.Return1h.SourceTime:Oldest(input.Return1h.SourceTime,input.Return24h.SourceTime)),
            C("ExtremeFunding",10,Scale(funding is null?null:Math.Abs(funding.Value),options.ExtremeFunding8h),"8h 标准化极端资金费率",input.FundingRate8h.SourceTime),
            C("LongCrowding",10,crowd is null?null:Clamp(((double)crowd.Value-0.5)/0.35),"同交易所多头拥挤程度",input.LongShare.SourceTime),
            C("ExtremeOiMarketCap",15,Scale(om,options.ExtremeOiRatio),"OI/市值极端水平",Oldest(input.OpenInterestUsd.SourceTime,cap.MarketCapUsd.SourceTime)),
            C("Liquidity",10,vol is null?null:1-Scale(vol,options.MinVolume24hUsd*2),"24h 成交额流动性代理，非盘口深度或可成交保证",input.Volume24hUsd.SourceTime),
            C("Fdv",10,fm is null?null:Clamp(((double)fm.Value-1)/(double)(options.HighFdvRatio-1)),"FDV/市值稀释风险代理",Oldest(cap.FdvUsd.SourceTime,cap.MarketCapUsd.SourceTime)),
            C("HolderConcentration",10,null,"Top Holder 未知",null),C("ProjectOutflow",10,null,"项目钱包流出未知",null),C("ExchangeInflow",5,null,"交易所净流入未知",null),C("HolderDecline",5,null,"Holder 减少未知",null)
        };
        int Coverage(List<ScoreComponent> c) => (int)c.Where(x=>x.Strength is not null).Sum(x=>x.Weight);
        // Missing weights stay missing: scores are observed contributions out of the fixed 100, never renormalized.
        double? Score(List<ScoreComponent> c) => mc is null || price is null || oi is null || vol is null || Coverage(c)<options.MinimumCoverage
            ? null : Math.Round(c.Sum(x=>x.Weight*(x.Strength??0)),2);
        var anomaly = Score(a); var riskScore = Score(risk);
        var eligible = mc >= options.MinMarketCapUsd && mc <= options.MaxMarketCapUsd && vol >= options.MinVolume24hUsd && oi >= options.MinOpenInterestUsd;
        var active = anomaly >= options.EntryScore && eligible && a.Count(x=>x.Confirmed && x.Family is "VolumeMarketCap" or "OiMarketCap" or "OiAnomaly" or "PriceVolume")>=2;
        var overheated = risk.Any(x=>x.Strength>=1 && x.Family is "ExtremeFunding" or "ExtremeOiMarketCap" or "Overheating");
        var stage = anomaly is null?"Unknown":overheated?"Overheated":active?"Ignition":"Observing";
        return new(Version,anomaly,riskScore,Coverage(a),Coverage(risk),stage,active&&!overheated,anomaly is null?DataStatus.Missing:DataStatus.Valid,
            a,risk,a.Concat(risk).Where(x=>x.Strength is null).Select(x=>x.Family).Distinct().ToArray(),
            !identity?"资产或跨交易所身份待人工核对":mc is null?"可信市值缺失或过期":anomaly is null?"同交易所必要输入或覆盖不足":overheated?"异常与过热风险并存，停止进入观察候选":"固定权重的已观测贡献；缺失链上风险不能解释为安全");
    }
}
