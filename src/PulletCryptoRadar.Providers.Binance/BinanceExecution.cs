using System.Text.Json;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Providers.Binance;

public sealed partial class BinanceRestClient : IExecutionEvidenceProvider
{
    public async Task<ContractRulesSnapshot> GetTradingRulesAsync(CancellationToken ct)
    {using var doc=await GetAsync("/fapi/v1/exchangeInfo",1,ct,true);return ExecutionParser.Rules(doc.RootElement,time.GetUtcNow());}
    public async Task<ExecutionOrderBook> GetOrderBookAsync(string symbol,CancellationToken ct)
    {using var doc=await GetAsync($"/fapi/v1/depth?symbol={Uri.EscapeDataString(symbol)}&limit=20",2,ct,true);return ExecutionParser.Book(doc.RootElement,symbol,time.GetUtcNow());}
    public async Task<FundingRateWindow> GetFundingHistoryAsync(string symbol,DateTimeOffset from,DateTimeOffset through,CancellationToken ct)
    {
        if(from>through||through-from>TimeSpan.FromDays(8)||through>time.GetUtcNow())throw new ArgumentException("Funding query window invalid");
        using var doc=await GetAsync($"/fapi/v1/fundingRate?symbol={Uri.EscapeDataString(symbol)}&startTime={from.ToUnixTimeMilliseconds()}&endTime={through.ToUnixTimeMilliseconds()}&limit=1000",1,ct,true);
        return ExecutionParser.Funding(doc.RootElement,symbol,from,through,time.GetUtcNow());
    }
    public async Task<FundingIntervalSnapshot> GetFundingIntervalsAsync(CancellationToken ct)
    {using var doc=await GetAsync("/fapi/v1/fundingInfo",1,ct,true);return ExecutionParser.Intervals(doc.RootElement,time.GetUtcNow());}
}

public static class ExecutionParser
{
    private static decimal Number(JsonElement row,string key)=>BinanceParser.Decimal(row.GetProperty(key));
    public static ContractRulesSnapshot Rules(JsonElement root,DateTimeOffset now)
    {
        var result=new List<ContractTradingRules>();
        foreach(var row in root.GetProperty("symbols").EnumerateArray().Where(x=>x.GetProperty("quoteAsset").GetString()=="USDT"&&x.GetProperty("contractType").GetString()=="PERPETUAL"))
        {
            var symbol=row.GetProperty("symbol").GetString()??throw new InvalidDataException("Contract symbol missing");
            var status=row.GetProperty("status").GetString()??"Unknown";
            try
            {
                var filters=row.GetProperty("filters").EnumerateArray().ToDictionary(f=>f.GetProperty("filterType").GetString()!,StringComparer.Ordinal);
                var price=filters["PRICE_FILTER"];var lot=filters["LOT_SIZE"];
                var hasMarket=filters.TryGetValue("MARKET_LOT_SIZE",out var market);
                var notional=filters.TryGetValue("MIN_NOTIONAL",out var minimum)?Number(minimum,"notional"):
                    filters.TryGetValue("NOTIONAL",out var other)?Number(other,"minNotional"):throw new InvalidDataException("Minimum notional filter missing");
                var tick=Number(price,"tickSize");var step=Number(lot,"stepSize");
                var mstep=hasMarket?Number(market,"stepSize"):step;var mmin=hasMarket?Number(market,"minQty"):Number(lot,"minQty");var mmax=hasMarket?Number(market,"maxQty"):Number(lot,"maxQty");
                // A zero MARKET_LOT_SIZE step disables only that step check; LOT_SIZE still applies.
                if(tick<=0||step<=0||mstep<0||Number(lot,"minQty")<=0||Number(lot,"maxQty")<=0||mmin<0||mmax<=0||notional<=0)
                    throw new InvalidDataException("Nonpositive exchange filter");
                result.Add(new(symbol,status,tick,Number(price,"minPrice"),Number(price,"maxPrice"),step,Number(lot,"minQty"),Number(lot,"maxQty"),
                    mstep,mmin,mmax,notional,row.TryGetProperty("marketTakeBound",out var bound)?BinanceParser.Decimal(bound):null,now,null));
            }
            catch(Exception ex)when(ex is KeyNotFoundException or InvalidDataException or FormatException or ArgumentException or OverflowException)
            {result.Add(new(symbol,status,0,0,0,0,0,0,0,0,0,0,null,now,"交易过滤器不完整或无效，不采用precision字段替代"));}
        }
        if(result.Count==0||result.Select(r=>r.Symbol).Distinct(StringComparer.Ordinal).Count()!=result.Count)throw new InvalidDataException("Empty or duplicate contract rules");
        return new(now,result);
    }
    public static ExecutionOrderBook Book(JsonElement root,string symbol,DateTimeOffset now)
    {
        if(root.TryGetProperty("symbol",out var s)&&s.GetString()!=symbol)throw new InvalidDataException("Book symbol mismatch");
        var at=BinanceParser.Time(root.GetProperty("T"));var output=BinanceParser.Time(root.GetProperty("E"));
        var id=root.GetProperty("lastUpdateId").GetInt64();
        BookLevel[] Levels(string key)=>root.GetProperty(key).EnumerateArray().Select(x=>new BookLevel(BinanceParser.Decimal(x[0]),BinanceParser.Decimal(x[1]))).ToArray();
        var bids=Levels("bids");var asks=Levels("asks");
        // Exchange and local clocks differ. Preserve the raw small lead; consumers quarantine it until local time catches up.
        if(id<0||at>output||output>now.AddSeconds(2)||now-output>TimeSpan.FromSeconds(20)||bids.Length is 0 or >20||asks.Length is 0 or >20
            ||bids.Concat(asks).Any(x=>x.Price<=0||x.Quantity<=0)||bids[0].Price>=asks[0].Price
            ||bids.Zip(bids.Skip(1)).Any(p=>p.First.Price<=p.Second.Price)||asks.Zip(asks.Skip(1)).Any(p=>p.First.Price>=p.Second.Price))
            throw new InvalidDataException("Order book invalid, crossed, unordered or stale");
        return new(symbol,id,at,output,now,bids,asks);
    }
    public static FundingRateWindow Funding(JsonElement root,string symbol,DateTimeOffset from,DateTimeOffset through,DateTimeOffset now)
    {
        var rows=new List<FundingRateRecord>();var count=0;
        foreach(var row in root.EnumerateArray())
        {
            count++;var at=BinanceParser.Time(row.GetProperty("fundingTime"));var rate=Number(row,"fundingRate");var mark=Number(row,"markPrice");
            var kind=row.TryGetProperty("rateType",out var type)?type.GetString()??"Regular":"Regular";
            if(row.GetProperty("symbol").GetString()!=symbol||at<from||at>through||at>now||Math.Abs(rate)>1||mark<=0||kind is not ("Regular" or "Special"))
                throw new InvalidDataException("Funding identity, time, rate or settlement mark invalid");
            rows.Add(new(symbol,at,rate,mark,kind,now));
        }
        var grouped=rows.GroupBy(r=>(r.FundingAt,r.RateType)).ToArray();
        if(grouped.Any(g=>g.Select(r=>(r.Rate,r.MarkPrice)).Distinct().Count()!=1))throw new InvalidDataException("Conflicting funding records");
        return new(symbol,from,through,now,count<1000,grouped.Select(g=>g.First()).OrderBy(r=>r.FundingAt).ToArray());
    }
    public static FundingIntervalSnapshot Intervals(JsonElement root,DateTimeOffset now)
    {
        var rows=root.EnumerateArray().Select(r=>new FundingIntervalRule(r.GetProperty("symbol").GetString()!,r.GetProperty("fundingIntervalHours").GetInt32(),
            Number(r,"adjustedFundingRateCap"),Number(r,"adjustedFundingRateFloor"))).ToArray();
        if(rows.Any(r=>string.IsNullOrWhiteSpace(r.Symbol)||r.IntervalHours is <1 or >24||r.Cap<r.Floor)||rows.Select(r=>r.Symbol).Distinct().Count()!=rows.Length)
            throw new InvalidDataException("Funding interval adjustment invalid");
        return new(now,rows);
    }
}
