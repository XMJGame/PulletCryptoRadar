using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Application;
public static class ExecutionCostEngine
{
    public static decimal Floor(decimal value,decimal step)=>step>0?decimal.Floor(value/step)*step:throw new ArgumentException("Step must be positive");
    public static decimal Ceiling(decimal value,decimal step)=>step>0?decimal.Ceiling(value/step)*step:throw new ArgumentException("Step must be positive");
    public static ExecutionQuote Quote(ExecutionQuoteRequest request,ContractTradingRules? rules,ExecutionOrderBook? book,DateTimeOffset now,ExecutionCostModel? model=null)
    {
        model??=new();decimal? qty=null,vwap=null,fill=null,notional=null,fee=null,spread=null,impact=null,stop=null,loss=null;
        ExecutionQuote Result(string status,string reason)=>new(model.Version,request.Symbol,request.Side,status,reason,now,request.Quantity,qty,qty is null?null:request.Quantity-qty,
            vwap,fill,notional,fee,spread,impact,stop,loss,book?.TransactionAt,rules?.ObservedAt,book,rules);
        if(request.Side is not ("Buy" or "Sell")||request.Quantity<=0||request.Quantity>1_000_000_000_000_000m||model.FeeRate is <0 or >.01m||model.ResidualSlippageRate is <0 or >.01m)
            return Result("Invalid","方向、数量或成本配置无效");
        if(rules is null||rules.Symbol!=request.Symbol||rules.Issue is not null||rules.TickSize<=0||rules.StepSize<=0||rules.MarketStepSize<0)
            return Result("Unknown","缺少该永续合约的有效交易过滤器");
        if(rules.ObservedAt>now||now-rules.ObservedAt>TimeSpan.FromHours(model.RulesMaximumAgeHours)||rules.Status!="TRADING")
            return Result("Blocked","交易规则失鲜、未来输入或合约已非TRADING");
        // Satisfy BOTH LOT_SIZE and MARKET_LOT_SIZE. Repeated flooring is not sufficient for non-dividing grids.
        decimal step;try{step=CommonStep(rules.StepSize,rules.MarketStepSize);qty=Floor(request.Quantity,step);}
        catch(OverflowException){return Result("Invalid","交易数量网格超出十进制可表达范围");}
        if(qty<Math.Max(rules.MinQuantity,rules.MarketMinQuantity)||qty<=0)return Result("Blocked","按数量步长向下取整后低于最小数量");
        if(qty>Math.Min(rules.MaxQuantity,rules.MarketMaxQuantity))return Result("Blocked","数量超过市价单/合约数量上限");
        if(book is null||book.Symbol!=request.Symbol||book.TransactionAt>book.OutputAt||book.OutputAt>now||book.ReceivedAt>now
            ||now-book.TransactionAt>TimeSpan.FromSeconds(model.BookMaximumAgeSeconds)||book.Bids.Count==0||book.Asks.Count==0)
            return Result("Unknown","盘口缺失、失鲜或源时间尚未到达；不以最新成交价冒充买卖价");
        if(book.Bids.Concat(book.Asks).Any(l=>l.Price<=0||l.Quantity<=0||l.Price%rules.TickSize!=0)||book.Bids[0].Price>=book.Asks[0].Price
            ||book.Bids.Zip(book.Bids.Skip(1)).Any(p=>p.First.Price<=p.Second.Price)||book.Asks.Zip(book.Asks.Skip(1)).Any(p=>p.First.Price>=p.Second.Price))
            return Result("Invalid","盘口交叉、档位无效或顺序不正确");
        var mid=(book.Bids[0].Price+book.Asks[0].Price)/2;spread=(book.Asks[0].Price-book.Bids[0].Price)/mid*10_000;
        if(spread>model.MaximumSpreadBps)return Result("Blocked","买卖点差超过成本模型上限");
        var remaining=qty.Value;decimal cost=0;var levels=request.Side=="Buy"?book.Asks:book.Bids;
        foreach(var level in levels){var take=Math.Min(remaining,level.Quantity);cost+=take*level.Price;remaining-=take;if(remaining==0)break;}
        if(remaining>0)return Result("Unknown","已观测20档深度不足以覆盖全量，不假定全部按买一/卖一成交");
        vwap=cost/qty;var best=levels[0].Price;var side=request.Side=="Buy"?1m:-1m;impact=side*(vwap/best-1)*10_000;
        if(impact>model.MaximumImpactBps)return Result("Blocked","盘口冲击超过成本模型上限");
        fill=vwap*(1+side*model.ResidualSlippageRate);notional=qty*fill;fee=notional*model.FeeRate;
        if(fill<rules.MinPrice||rules.MaxPrice>0&&fill>rules.MaxPrice)return Result("Blocked","估算成交价超出合约价格范围");
        if(!request.ReduceOnly&&notional<rules.MinimumNotional)return Result("Blocked","取整后的名义金额低于最低成交额");
        if(request.StopPrice is {} input)
        {
            if(input<=0)return Result("Invalid","止损价无效");stop=side>0?Floor(input,rules.TickSize):Ceiling(input,rules.TickSize);
            if(stop<rules.MinPrice||rules.MaxPrice>0&&stop>rules.MaxPrice||side*(fill-stop)<=0)return Result("Blocked","止损取整后方向或价格范围无效");
            loss=qty*(side*(fill-stop)+fill*model.FeeRate+stop*(model.FeeRate+model.ResidualSlippageRate));
        }
        return Result("Estimated","只读成本核验：盘口VWAP加保守残余滑点，手续费为配置假设；不是成交或开仓许可");
    }
    public static decimal CommonStep(decimal a,decimal b)
    {
        if(a<=0||b<0)throw new ArgumentException("Invalid quantity grids");if(b==0)return a;
        // decimal arithmetic only. Scale to integers and take their least common multiple.
        decimal scale=1;while(a*scale!=decimal.Truncate(a*scale)||b*scale!=decimal.Truncate(b*scale))scale*=10;
        var x=a*scale;var y=b*scale;var first=x;var second=y;while(y!=0){var next=x%y;x=y;y=next;}
        return first/x*second/scale;
    }
}
