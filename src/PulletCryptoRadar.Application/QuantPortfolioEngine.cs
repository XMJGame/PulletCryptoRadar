using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Application;
public static class QuantPortfolioEngine
{
    public static string Trend(QuantFrame? frame)=>frame?.Facts is not {} f?"Unknown":f.FourHourEma20>f.FourHourEma60&&f.FourHourClose>=f.FourHourEma20?"Up":
        f.FourHourEma20<f.FourHourEma60&&f.FourHourClose<=f.FourHourEma20?"Down":"Mixed";
    public static QuantMarketContext Context(IReadOnlyDictionary<string,QuantFrame> frames,IReadOnlyList<MarketView> markets,DateTimeOffset now,QuantParameters p)
    {
        var hour=DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds()/3600*3600);
        var fresh=markets.Select(QuantRules.Input).Where(m=>m.Active&&!QuantRules.Stable(m.BaseAsset)&&m.QuoteVolume24h>=p.MinimumVolume24h&&m.Return24h is not null&&QuantRules.FreshPrice(m,now)).ToArray();
        var btc=frames.GetValueOrDefault("BTCUSDT");var eth=frames.GetValueOrDefault("ETHUSDT");var bt=Trend(btc);var et=Trend(eth);
        var baseline=btc?.Issue is null&&btc?.Hour==hour&&eth?.Issue is null&&eth?.Hour==hour&&fresh.Any(m=>m.Symbol=="BTCUSDT")&&fresh.Any(m=>m.Symbol=="ETHUSDT")&&fresh.Length>=20;
        var state=!baseline?"Unknown":btc!.Facts!.AtrPercent>p.BtcStressAtrFraction*100||eth!.Facts!.AtrPercent>p.EthStressAtrFraction*100?"Pressure":
            bt=="Up"&&et=="Up"?"Uptrend":bt=="Down"&&et=="Down"?"Downtrend":"Mixed";
        return new(now,hour,state,bt,et,fresh.Length,markets.Count,fresh.Length>=20?100m*fresh.Count(m=>m.Return24h>0)/fresh.Length:null,
            fresh.Length>0?fresh.Min(m=>m.PriceAt):null,fresh.Length>0?fresh.Max(m=>m.PriceAt):null,
            "BTC/ETH闭合4h背景、1h ATR与新鲜24h涨跌广度；环境标签是待验证特征，不是胜率或未来方向保证");
    }
    public static QuantPortfolio Portfolio(PaperOverview paper,DateTimeOffset now)=>new(paper.State.Revision,now,paper.CompleteEquity,paper.State.Cash,paper.State.PeakEquity,
        paper.State.Positions.Select(p=>new QuantReservation(p.Symbol,p.Direction,QuantPaperEngine.Risk(p,paper.State.Rules),StructuralPaperEngine.RemainingOriginalMargin(p),false))
            .Concat(paper.State.Pending.Select(p=>new QuantReservation(p.Symbol,p.QuantIntent?.Decision.Direction??p.StructuralEvidence?.Direction??"Long",p.QuantIntent?.Assessment.Plan?.EstimatedRisk??p.StructuralEvidence?.Plan?.EstimatedRisk??paper.State.Rules.RiskPerTrade,
                p.QuantIntent?.Assessment.Plan?.Margin??p.StructuralEvidence?.Plan?.Margin??paper.State.Rules.MarginPerTrade,true))).ToArray());
    public static decimal? Correlation(QuantFrame? a,QuantFrame? b,int hours=72)
    {
        if(a is null||b is null||a.Issue is not null||b.Issue is not null||a.Hour!=b.Hour)return null;
        var left=a.Bars.TakeLast(hours+1).Zip(a.Bars.TakeLast(hours+1).Skip(1)).ToDictionary(x=>x.Second.EndTime,x=>(double)(x.Second.Close/x.First.Close-1));
        var right=b.Bars.TakeLast(hours+1).Zip(b.Bars.TakeLast(hours+1).Skip(1)).ToDictionary(x=>x.Second.EndTime,x=>(double)(x.Second.Close/x.First.Close-1));
        var pairs=left.Where(x=>right.ContainsKey(x.Key)).Select(x=>(X:x.Value,Y:right[x.Key])).ToArray();if(pairs.Length<Math.Min(60,hours))return null;
        var mx=pairs.Average(x=>x.X);var my=pairs.Average(x=>x.Y);var vx=pairs.Sum(x=>(x.X-mx)*(x.X-mx));var vy=pairs.Sum(x=>(x.Y-my)*(x.Y-my));
        if(vx<=1e-20||vy<=1e-20)return null;var value=pairs.Sum(x=>(x.X-mx)*(x.Y-my))/Math.Sqrt(vx*vy);
        return double.IsFinite(value)?(decimal)Math.Clamp(value,-1,1):null;
    }
    public static decimal EnvironmentScale(QuantMarketContext c,string direction)
    {
        if(c.State is "Unknown" or "Pressure")return 0;
        var scale=c.State=="Mixed"?.75m:c.State=="Uptrend"&&direction=="Long"||c.State=="Downtrend"&&direction=="Short"?1:.5m;
        if(c.PositiveBreadthPercent is {} b&&(direction=="Long"&&b<30||direction=="Short"&&b>70))scale=Math.Min(scale,.5m);
        return scale;
    }
    public static QuantAssessment Assess(QuantHypothesis d,QuantMarketInput market,QuantMarketContext context,QuantPortfolio portfolio,
        IReadOnlyDictionary<string,QuantFrame> frames,ExecutionPlanningInputs inputs,DateTimeOffset now,QuantParameters p,ExecutionCostModel costs,QuantSizingLimits? limits=null)
    {
        var checks=new List<SignalCondition>();ExecutionQuote? quote=null;var hash=QuantRules.Hash(p,costs);
        QuantAssessment End(string state,string reason,QuantPlan? plan=null)=>new(d.Id,d.Symbol,d.Strategy,d.Direction,d.Mode,now,state,reason,plan,quote,checks)
            {Market=market,Context=context,Portfolio=portfolio};
        bool Check(string key,string name,decimal? value,string requirement,bool pass){checks.Add(new(key,name,value,requirement,pass));return pass;}
        if(d.Status!="PriceCandidate")return End("NoCandidate",d.Reason);
        if(d.Mode is not ("Forward" or "CurrentPreview" or "AwaitBaseline"))return End("Rejected","历史情景或未知模式不能进入实时组合计划");
        if(d.Mode=="AwaitBaseline")return End("AwaitData","尚未冻结前瞻输入，等待基线，不作有效计划");
        if(d.Mode=="Forward"&&!d.InputArchived)return End("AwaitData","尚无不可变原始输入档案，不能形成前瞻计划");
        if(d.RulesHash!=hash||d.CapturedAt>now||d.Hour>d.CapturedAt||d.Symbol!=market.Symbol||d.Direction is not ("Long" or "Short"))return End("Rejected","版本、身份或时间证据无效");
        if(now>d.ExpiresAt)return End("Expired","结构候选已超时，不按旧价补计划");
        if(limits is not null&&(limits.MaximumQuantity<=0||limits.MaximumLeverage<1||limits.MaximumRisk<=0||limits.RequiredStop<=0))return End("Rejected","原批准上限无效，不能复核成交");
        var equity=portfolio.Equity??0;
        if(!Check("Equity","参考账户权益完整且新鲜",portfolio.Equity,"权益>0且账本≤30秒",equity>0&&portfolio.AsOf<=now&&now-portfolio.AsOf<=TimeSpan.FromSeconds(30)))return End("AwaitData","参考账户权益/持仓报价不完整");
        if(portfolio.Reservations.Any(r=>r.Symbol==d.Symbol))return End("Rejected","原盘已有同币持仓/意向，影子组合不重复占用");
        if(!Check("Capacity","持仓及意向占用",portfolio.Reservations.Count,$"<{p.MaximumPositions}笔",portfolio.Reservations.Count<p.MaximumPositions))return End("Rejected","参考组合已满");
        if(!Check("DirectionCount","同向占用",portfolio.Reservations.Count(r=>r.Direction==d.Direction),$"<{p.MaximumSameDirection}笔",portfolio.Reservations.Count(r=>r.Direction==d.Direction)<p.MaximumSameDirection))return End("Rejected","同向仓位达到上限");
        var environment=EnvironmentScale(context,d.Direction);
        if(!Check("Environment","市场环境风险倍率",environment,">0且环境同一闭合小时",environment>0&&context.Hour==d.Hour&&context.AsOf<=now&&now-context.AsOf<=TimeSpan.FromSeconds(90)))
            return End(context.State=="Unknown"?"AwaitData":"Rejected","基准环境未知或压力环境，暂不分配新风险");
        if(!Check("Oi","OI数量证据风险倍率",d.Oi.RiskScale,">0，极端变化拦截",d.Oi.RiskScale>0&&d.Oi.Status is "Increasing" or "Decreasing"))return End(d.Oi.Status=="Unknown"?"AwaitData":"Rejected",d.Oi.Meaning);
        var fresh=QuantRules.FreshPrice(market,now)&&market.MarkPrice>0&&market.FundingRate is not null&&market.MarkAt is {} mt&&market.MarkReceivedAt is {} mr&&mt<=now&&mr<=now&&mt-mr<=TimeSpan.FromSeconds(2)&&now-mt<=TimeSpan.FromSeconds(45)&&now-mr<=TimeSpan.FromSeconds(45);
        if(!Check("FreshMarket","价格、标记价与最新公布费率",market.FundingRate,"价格≤90秒、标记价/费率≤45秒",fresh))return End("AwaitData","等待新鲜价格、标记价与费率");
        var side=d.Direction=="Long"?1m:-1m;var rate=market.FundingRate!.Value;
        if(!Check("Funding","最新公布费率%",rate*100,$"绝对≤{p.MaximumAbsoluteFundingRate*100:G}%，付费方向≤{p.MaximumPayFundingRate*100:G}%",Math.Abs(rate)<=p.MaximumAbsoluteFundingRate&&side*rate<=p.MaximumPayFundingRate))return End("Rejected","资金费拥挤或付费成本超过本版本阈值");
        if(d.Mode=="Forward"&&(market.PriceAt<=d.CapturedAt||market.PriceReceivedAt<=d.CapturedAt||market.MarkAt<=d.CapturedAt||market.MarkReceivedAt<=d.CapturedAt||inputs.Book is null||inputs.Book.TransactionAt<=d.CapturedAt||inputs.Book.ReceivedAt<=d.CapturedAt))
            return End("AwaitData","等待严格晚于冻结信号的下一次价格、标记价和盘口，尚非成交");
        if(d.Stop is null||d.Facts is not {Atr:>0} f)return End("Rejected","结构失效位置或ATR无效");
        if(!Check("Chase","现价距信号闭合价ATR数",Math.Abs(market.Price!.Value-f.Close)/f.Atr,"≤0.75ATR",Math.Abs(market.Price.Value-f.Close)<=.75m*f.Atr))return End("Rejected","现价离信号已过远，不追价");
        var drawdown=portfolio.PeakEquity>0?Math.Max(0,1-equity/portfolio.PeakEquity):0;
        var drawScale=drawdown>=.15m?.25m:drawdown>=.10m?.5m:drawdown>=.05m?.75m:1;
        var scale=p.PilotRiskScale*environment*d.Oi.RiskScale*drawScale*(Math.Abs(rate)>.001m?.5m:1);
        var total=portfolio.Reservations.Sum(r=>r.PlannedRisk);var directional=portfolio.Reservations.Where(r=>r.Direction==d.Direction).Sum(r=>r.PlannedRisk);
        var correlated=portfolio.Reservations.Where(r=>Correlation(frames.GetValueOrDefault(d.Symbol),frames.GetValueOrDefault(r.Symbol),p.CorrelationHours) is not {} corr||Math.Abs(corr)>=p.CorrelationThreshold).Sum(r=>r.PlannedRisk);
        var budget=Math.Min(Math.Min(p.AbsoluteRiskCap,equity*p.RiskFraction)*scale,
            Math.Min(Math.Min(p.AbsoluteTotalRiskCap,equity*p.TotalRiskFraction)-total,Math.Min(Math.Min(p.AbsoluteDirectionRiskCap,equity*p.DirectionRiskFraction)-directional,Math.Min(p.AbsoluteCorrelationRiskCap,equity*p.CorrelationRiskFraction)-correlated)));
        if(limits is not null)budget=Math.Min(budget,limits.MaximumRisk);
        if(!Check("RiskRoom","单笔及组合剩余计划风险U",budget,">0，相关未知按相关处理",budget>0))return End("Rejected","总风险、同向或高相关风险额度不足");
        var fundingIntervals=(decimal)Math.Ceiling((double)p.MaximumHoldHours/(inputs.AdjustedFundingIntervalHours??1));
        var fundingFraction=Math.Max(0,side*rate)*1.5m*fundingIntervals;
        var availableMargin=Math.Min(p.MaximumMargin,p.MaximumMargin*p.MaximumPositions-portfolio.Reservations.Sum(r=>r.Margin));
        if(availableMargin<=0||portfolio.Cash<=0)return End("Rejected","参考组合保证金额度或可用现金不足");
        string? lastFailure=null;
        for(var leverage=Math.Min(p.MaximumLeverage,limits?.MaximumLeverage??p.MaximumLeverage);leverage>=1;leverage--)
        {
            var reference=market.Price.Value*(1+side*costs.ResidualSlippageRate);var unit=Math.Max(0,side*(reference-d.Stop.Value))+reference*(2*costs.FeeRate+2*costs.ResidualSlippageRate+p.GapRiskFraction+fundingFraction);
            if(unit<=0)continue;var quantity=Math.Min(budget/unit,Math.Min(availableMargin*leverage/reference,portfolio.Cash/(reference/leverage+reference*costs.FeeRate)));
            if(limits is not null)quantity=Math.Min(quantity,limits.MaximumQuantity);
            for(var attempt=0;attempt<8&&quantity>0;attempt++)
            {
                quote=ExecutionCostEngine.Quote(new(d.Symbol,side>0?"Buy":"Sell",quantity,d.Stop),inputs.Rules,inputs.Book,now,costs);
                if(quote.Status!="Estimated"){lastFailure=quote.Reason;break;}
                var q=quote.Quantity!.Value;var entry=quote.EstimatedFill!.Value;var stop=quote.RoundedStop!.Value;var margin=q*entry/leverage;
                if(limits is not null&&stop!=limits.RequiredStop)return End("Rejected","当前交易规则取整改变原批准止损，不能移动结构复核成交");
                var fund=q*entry*fundingFraction;var risk=quote.PlannedLossIncludingCosts!.Value+fund+q*entry*p.GapRiskFraction;
                if(risk>budget||margin>availableMargin||margin+quote.Fee>portfolio.Cash)
                {quantity=q*Math.Min(budget/risk,Math.Min(availableMargin/margin,portfolio.Cash/(margin+quote.Fee!.Value)));continue;}
                var effectiveMargin=entry/leverage-entry*costs.FeeRate-entry*fundingFraction;
                var liq=side>0?(entry-effectiveMargin)/(1-p.AssumedMaintenanceFraction):(entry+effectiveMargin)/(1+p.AssumedMaintenanceFraction);
                if(effectiveMargin<=0||side*(stop-liq)<=p.LiquidationBufferAtr*f.Atr){lastFailure="结构失效价与估算强平价间距不足，尝试降低杠杆";break;}
                if(inputs.Rules?.MarketTakeBound is {} bound&&Math.Abs(entry/market.MarkPrice!.Value-1)>bound){lastFailure="估算成交价偏离标记价超过合约marketTakeBound";break;}
                var distance=side*(entry-stop);var levels=d.Levels.Where(l=>l.Kind==(side>0?"Resistance":"Support")&&l.ConfirmedAt<=d.Hour).Select(l=>l.Price);
                var target1=levels.Where(v=>side*(v-entry)>=2*distance).OrderBy(v=>side*v).Cast<decimal?>().FirstOrDefault();var first=target1??entry+side*2*distance;
                var target2=levels.Where(v=>side*(v-first)>=distance).OrderBy(v=>side*v).Cast<decimal?>().FirstOrDefault();var second=target2??entry+side*Math.Max(4*distance,side*(first-entry)+distance);
                var tick=inputs.Rules!.TickSize;first=side>0?ExecutionCostEngine.Floor(first,tick):ExecutionCostEngine.Ceiling(first,tick);second=side>0?ExecutionCostEngine.Floor(second,tick):ExecutionCostEngine.Ceiling(second,tick);
                if(first<=0||second<=0||side*(first-entry)<=0||side*(second-first)<=0||inputs.Rules.MinPrice>0&&Math.Min(first,second)<inputs.Rules.MinPrice||inputs.Rules.MaxPrice>0&&Math.Max(first,second)>inputs.Rules.MaxPrice){lastFailure="取整目标超出当前合约价格范围或无有效收益空间";break;}
                var netReward=q*(side*(first-entry)-entry*costs.FeeRate-first*(costs.FeeRate+costs.ResidualSlippageRate))-fund;
                if(netReward<1.2m*risk){lastFailure="首目标扣除费用及资金费压力后不足1.2倍计划风险";break;}
                Check("EstimatedLiquidation","止损与估算强平距离ATR数",side*(stop-liq)/f.Atr,$">{p.LiquidationBufferAtr:G}ATR，维持保证金率为假设",true);
                Check("FinalRisk","含成本、资金费及跳空预留风险U",risk,$"≤{budget:G}U",risk<=budget);
                return End("ShadowReady","仅影子风险通过，不创建仓位；强平、资金费压力及手续费仍包含模型假设",
                    new(leverage,q,entry,stop,margin,quote.Fee!.Value,first,second,target1 is null?"2R风险投影":"已确认历史结构",target2 is null?"4R或更远风险投影":"已确认历史结构",risk,fund,scale,liq,p.MaximumHoldHours));
            }
        }
        return End(quote?.Status=="Unknown"?"AwaitData":"Rejected",lastFailure??"向下取整后没有满足风险、保证金与数量约束的方案");
    }
}
