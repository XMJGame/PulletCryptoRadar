using PulletCryptoRadar.Core;
namespace PulletCryptoRadar.Application;

// Price/cost accounting from immutable saved inputs. It cannot identify counterfactual fills or the cause of a price move.
public static class QuantExitAttributionEngine
{
    public const string Version="Quant-Exit-Attribution-1";
    private static bool Equal(decimal a,decimal b)=>Math.Abs(a-b)<=.000000000000000001m*Math.Max(1,Math.Max(Math.Abs(a),Math.Abs(b)));
    public static QuantExitAttribution Build(QuantLivePosition meta,string direction,IReadOnlyList<PaperTrade> trades,QuantLifecycle lifecycle)
    {
        var budget=Budget(meta,direction);var legs=trades.Select(t=>Leg(t,meta.Plan.Stop)).ToArray();
        var confirmed=lifecycle.Status=="Complete"&&lifecycle.FinalNet is not null;
        var detailed=legs.Length>0&&legs.All(l=>l.BookStatus=="Verified");
        var eligible=confirmed&&budget.Status=="Verified";
        decimal? actualLoss=confirmed?-lifecycle.FinalNet:null;
        decimal? signed=eligible?actualLoss-meta.Plan.EstimatedRisk:null;
        decimal? beyond=signed is {} difference?Math.Max(0,difference):null;
        var parts=new List<QuantExitDifference>();
        decimal? Known(decimal value)=>eligible?value:null;
        void Part(string key,string name,decimal? value,string meaning)=>parts.Add(new(key,name,value,meaning));
        if(detailed)
        {
            Part("StopToMid","原止损→退出盘口中价",Known(legs.Sum(l=>l.OriginalStopToMid!.Value)),"包含目标/跟踪退出政策及市场口径/时点差异，不能全部叫滑点或延迟损失");
            Part("HalfSpread","盘口中价→对手最优价",Known(legs.Sum(l=>l.HalfSpread!.Value)),"保存盘口中的半点差金额，不是订单真实成交证据");
            Part("Depth","对手最优价→盘口VWAP",Known(legs.Sum(l=>l.Depth!.Value)),"保存档位覆盖该数量时的模型深度影响");
            Part("ResidualDifference","模型残余滑点−原预留",eligible?legs.Sum(l=>l.Residual!.Value)-budget.ResidualReserve:null,"实际为模型估价中的配置项，原止损残余预留扣一次");
        }
        else
        {
            Part("StopToFill","原止损→模拟退出价",Known(legs.Sum(l=>l.PriceDifference??0)),"只有合计价差；缺少完整可信盘口时不拆点差/深度/残余");
            Part("ResidualReserve","减原残余滑点预留",eligible?-budget.ResidualReserve:null,"合计价差已含模型残余，预算端单独扣回预留");
        }
        var exitFees=lifecycle.ExitFees;var entryFees=exitFees is {} paid?trades.Sum(t=>t.Fees)-paid:(decimal?)null;
        Part("EntryFeeDifference","已分摊开仓费−原开仓费",eligible?entryFees-budget.EntryFee:null,"开仓成本已含在入场及原风险中，不把入场点差再次计入退出");
        Part("ExitFeeDifference","退出费−原退出费预留",eligible?exitFees-budget.ExitFeeReserve:null,"按真实保存的模拟成交价格与数量计费，费率仍为原模型假设");
        Part("FundingDifference","实际资金费−原资金费预留",eligible?lifecycle.SettledFunding+lifecycle.LateFundingAdjustment-budget.FundingReserve:null,"正付负收，含迟到已平分配；原预留不是实际花费");
        Part("GapReserve","减原跳空风险预留",eligible?-budget.GapReserve:null,"这是预算抵减，不是证明发生过同额跳空或产生一笔收入");
        decimal? sum=eligible&&parts.All(p=>p.Value is not null)?parts.Sum(p=>p.Value!.Value):null;
        var remainder=sum-signed;
        var inconsistent=lifecycle.Status=="Inconsistent"||budget.Status=="Inconsistent"||legs.Any(l=>l.BookStatus=="Inconsistent"||l.BridgeDifference is {} r&&!Equal(r,0))||remainder is {} delta&&!Equal(delta,0);
        var status=inconsistent?"Inconsistent":budget.Status!="Verified"?"UnsupportedPlan":legs.Length==0?"NoExitSamples":
            lifecycle.Status=="Open"?"AwaitCompletion":lifecycle.Status=="AwaitFunding"?"AwaitFunding":!confirmed?"Incomplete":"Reconciled";
        return new(Version,status,detailed?"SavedBook":"LedgerAggregate",budget,confirmed?lifecycle.FinalNet:null,actualLoss,signed,beyond,
            beyond is {} extra&&meta.Plan.EstimatedRisk>0?100*extra/meta.Plan.EstimatedRisk:null,sum,remainder,
            legs.Count(l=>l.ObservationStatus=="Recorded"),legs.Count(l=>l.ObservationStatus=="HistoricalPartial"),legs.Count(l=>l.ObservationStatus=="Invalid"),parts,legs,
            ["正值表示相对原整仓止损风险预算增亏，负值表示改善或预留未使用；按有符号值相加，不能把每项正值截出后相加当作实际超损。盈利或分批目标退出也可相对原止损产生大幅负差异，这是正常政策结果。",
             "完整交易才核对实际净亏损−原计划风险；仍持仓、缺数量、待补资金费或账目不一致不确认整笔差额。迟到费用可更新截至本次已知的结果，原成交不回写。",
             "价格细分是保存数据的算术分解，不是可成交的反事实收益。判定参考价至退出盘口中价含市场时点与价格口径差，不能据此认定网络、程序等待或某个操盘行为造成了同额损失。",
             "旧Stop记录没有当时止损/判定价时，仅保留原止损至退出盘口的合计差异，不假定当时未发生跟踪止损；已锁定的判定可对应多腿执行，不把它当作每腿新的首次穿越。",
             "QuoteGap只表示持仓期间曾观测到报价间隔，不证明本次亏损由断档造成；本机时序另在持仓核对中列示，不能用来源与本机时钟相减推算网络延迟。"]);
    }
    private static QuantExitBudget Budget(QuantLivePosition meta,string direction)
    {
        var p=meta.Plan;var c=meta.Costs;var quote=meta.EntryQuote;
        if(direction is not ("Long" or "Short")||c.Version!="Execution-Cost-1"||meta.Parameters.Version!="Quant-Shadow-1"
            ||p.Quantity<=0||p.Entry<=0||p.Stop<=0||p.EstimatedRisk<=0||p.Fee<0||p.FundingReserve<0||meta.Parameters.GapRiskFraction<0
            ||c.FeeRate is <0 or >.01m||c.ResidualSlippageRate is <0 or >.01m)
            return new("Unsupported",p.EstimatedRisk,null,null,null,null,null,null,null,null,"原模型版本或有效原风险资料不足，不套当前配置补算历史");
        var side=direction=="Long"?1m:-1m;var stop=side*p.Quantity*(p.Entry-p.Stop);
        var exit=p.Quantity*p.Stop*c.FeeRate;var residual=p.Quantity*p.Stop*c.ResidualSlippageRate;var gap=p.Quantity*p.Entry*meta.Parameters.GapRiskFraction;
        var sum=stop+p.Fee+exit+residual+p.FundingReserve+gap;var delta=p.EstimatedRisk-sum;
        var valid=stop>0&&quote is not null&&quote.Version==c.Version&&quote.Status=="Estimated"&&quote.Quantity==p.Quantity&&quote.EstimatedFill==p.Entry&&quote.RoundedStop==p.Stop
            &&quote.Fee is {} fee&&Equal(p.Fee,fee)&&Equal(p.Fee,p.Quantity*p.Entry*c.FeeRate)&&quote.PlannedLossIncludingCosts is {} loss
            &&Equal(loss,stop+p.Fee+exit+residual)&&Equal(delta,0);
        return new(valid?"Verified":"Inconsistent",p.EstimatedRisk,stop,p.Fee,exit,residual,p.FundingReserve,gap,sum,delta,
            valid?"由保存的成交计划、入场成本结果和原参数核对；未用当前费率、资金费或市场数据重估":"原预算、入场成本结果或组成不一致，不能确认风险差异桥接");
    }
    private static QuantExitPriceLeg Leg(PaperTrade t,decimal initialStop)
    {
        var meta=t.Quant!;var quote=t.ExitQuote;var book=quote?.Book;var side=t.Direction=="Long"?1m:-1m;
        var issues=new List<string>();var priceValid=t.Direction is "Long" or "Short"&&t.Quantity>0&&t.ExitPrice>0&&initialStop>0;
        decimal? priceDifference=priceValid?side*t.Quantity*(initialStop-t.ExitPrice):null;
        decimal? mid=null,best=null,vwap=null,half=null,depth=null,residual=null,stopToMid=null,remainder=null;
        var bookStatus="Missing";
        if(meta.Costs.Version!="Execution-Cost-1")
        {bookStatus="Unsupported";issues.Add("该保存成本模型版本尚未支持，不套现行公式细分历史盘口成本");}
        else if(quote?.Vwap is not null&&book is not null&&book.Bids.Count>0&&book.Asks.Count>0)
        {
            var valid=priceValid&&quote.Status=="Estimated"&&quote.Version==meta.Costs.Version&&quote.Symbol==t.Symbol&&book.Symbol==t.Symbol
                &&quote.Side==(side>0?"Sell":"Buy")&&quote.Quantity==t.Quantity&&quote.EstimatedFill==t.ExitPrice&&quote.Fee is >=0
                &&book.Bids.Concat(book.Asks).All(l=>l.Price>0&&l.Quantity>0)&&book.Bids[0].Price<book.Asks[0].Price
                &&!book.Bids.Zip(book.Bids.Skip(1)).Any(p=>p.First.Price<=p.Second.Price)&&!book.Asks.Zip(book.Asks.Skip(1)).Any(p=>p.First.Price>=p.Second.Price)
                &&book.TransactionAt<=book.OutputAt&&book.OutputAt<=t.ClosedAt&&book.ReceivedAt<=t.ClosedAt
                &&meta.Costs.ResidualSlippageRate is >=0 and <=.01m&&meta.Costs.FeeRate is >=0 and <=.01m;
            var remaining=t.Quantity;decimal amount=0;var levels=side>0?book.Bids:book.Asks;
            foreach(var l in levels){var take=Math.Min(remaining,l.Quantity);amount+=take*l.Price;remaining-=take;if(remaining<=0)break;}
            if(valid&&remaining==0)
            {
                var savedVwap=amount/t.Quantity;
                valid=Equal(savedVwap,quote.Vwap.Value)&&Equal(savedVwap*(1-side*meta.Costs.ResidualSlippageRate),t.ExitPrice)
                    &&quote.Fee is {} fee&&Equal(fee,t.Quantity*t.ExitPrice*meta.Costs.FeeRate);
                if(valid)
                {
                    mid=(book.Bids[0].Price+book.Asks[0].Price)/2;best=levels[0].Price;vwap=quote.Vwap;
                    stopToMid=side*t.Quantity*(initialStop-mid);half=side*t.Quantity*(mid-best);
                    depth=side*t.Quantity*(best-vwap);residual=side*t.Quantity*(vwap-t.ExitPrice);
                    remainder=stopToMid+half+depth+residual-priceDifference;
                }
            }
            else valid=false;
            bookStatus=valid?"Verified":"Inconsistent";
            if(!valid)issues.Add("保存盘口、方向、深度、VWAP、模型估价或费用无法共同核对，细分保持未知");
        }
        else issues.Add("缺少完整保存盘口/VWAP，不用当前盘口追补历史");
        var timing=QuantExitTimingEngine.Build(t);var observation=meta.ExitObservation;var observationStatus=timing.Status;
        var reason=meta.PendingExit;
        if(observation is not null&&(observationStatus!="Recorded"||observation.Version!="Quant-Exit-Timing-1"||observation.Reason!=reason||observation.SourceAt!=meta.ExitAfter
            ||observation.TriggerPrice<=0||observation.SourceAt is null||observation.SourceAt>observation.ObservedAt||reason=="Stop"&&observation.StopAtObservation<=0))observationStatus="Invalid";
        decimal? reference=null,observed=null,shift=null,toObserved=null,toMid=null;var referenceBasis="Unknown";
        if(reason=="Target1"){reference=meta.Plan.Target1;referenceBasis="OriginalTarget1";}
        else if(reason=="Target2"){reference=meta.Plan.Target2;referenceBasis="OriginalTarget2";}
        if(observationStatus=="Recorded"&&observation is not null)
        {
            observed=observation.TriggerPrice;
            if(reason=="Stop"){reference=observation.StopAtObservation;referenceBasis="RecordedStop";}
            else if(reason is "TimeLimit" or "LiquidationRisk"){reference=observed;referenceBasis=reason=="TimeLimit"?"RecordedTimePrice":"RecordedRiskMark";}
        }
        else issues.Add(observation is null?"历史缺少判定记录：当时Stop、判定价及后续细分未知":"判定记录版本、归属或时序未通过，价格细分未知");
        if(reference>0&&priceValid)
        {shift=side*t.Quantity*(initialStop-reference);if(observed is not null)toObserved=side*t.Quantity*(reference-observed);}
        if(observed is not null&&mid is not null)toMid=side*t.Quantity*(observed-mid);
        return new(t.Id,t.ClosedAt,t.Reason,t.Quantity,t.FinalExit,t.QuoteGap,bookStatus,observationStatus,initialStop,reference,referenceBasis,observed,mid,best,vwap,t.ExitPrice,
            stopToMid,shift,toObserved,toMid,half,depth,residual,priceDifference,remainder,issues);
    }
}
