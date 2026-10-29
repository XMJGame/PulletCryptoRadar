using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Providers.Market;

public sealed class AlternativeOptions
{
    public bool Enabled { get; set; } = true;
    public int PollSeconds { get; set; } = 120;
    public int DeepCandidates { get; set; } = 25;
    public int CoinbaseCandidates { get; set; } = 5;
    public bool IsValid() => PollSeconds is >= 120 and <= 1800 && DeepCandidates is >= 1 and <= 80 && CoinbaseCandidates is >= 1 and <= 10;
}
public sealed class AlternativeProvider(HttpClient http, TimeProvider time, IOptions<AlternativeOptions> options) : IAlternativeMarketProvider
{
    private DateTimeOffset _next, _contractsAt, _productsAt;
    private readonly Dictionary<string, DateTimeOffset> _paused = [];
    private readonly SemaphoreSlim _gate = new(1,1);
    private JsonElement _contracts;
    private readonly HashSet<string> _products = [];
    private async Task<(JsonElement Json, DateTimeOffset Source, DateTimeOffset Received)> Get(string url, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var host = new Uri(url).Host;
            if (_paused.GetValueOrDefault(host)>time.GetUtcNow()) throw new HttpRequestException($"{host} 冷却至 {_paused[host]:O}");
            if (_next>time.GetUtcNow()) await Task.Delay(_next-time.GetUtcNow(),time,ct);
            _next=time.GetUtcNow().AddSeconds(1);
            using var response=await http.GetAsync(url,HttpCompletionOption.ResponseHeadersRead,ct);
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden)
            {
                var delay=response.Headers.RetryAfter?.Delta??response.Headers.RetryAfter?.Date-time.GetUtcNow()??TimeSpan.FromMinutes(5);
                _paused[host]=time.GetUtcNow()+ (delay<TimeSpan.FromMinutes(2)?TimeSpan.FromMinutes(2):delay);
            }
            response.EnsureSuccessStatusCode();
            var received=time.GetUtcNow(); var date=response.Headers.Date??throw new InvalidDataException("行情接口缺少 HTTP Date，不能虚构源时间");
            if(date>received||received-date>TimeSpan.FromMinutes(3))throw new InvalidDataException("行情响应时间无效或缓存已过期");
            await using var stream=await response.Content.ReadAsStreamAsync(ct);
            using var json=await JsonDocument.ParseAsync(stream,cancellationToken:ct);
            return(json.RootElement.Clone(),date,received);
        }
        finally{_gate.Release();}
    }
    public async Task<IReadOnlyList<VenueQuote>> GetGateAsync(IReadOnlyList<string> priorityBases,CancellationToken ct)
    {
        const string root="https://api.gateio.ws/api/v4/futures/usdt/";
        if(_contracts.ValueKind!=JsonValueKind.Array||time.GetUtcNow()-_contractsAt>TimeSpan.FromHours(24))
        {var c=await Get(root+"contracts",ct);_contracts=c.Json;_contractsAt=c.Received;}
        var ticker=await Get(root+"tickers",ct);
        var quotes=AlternativeParser.GateTickers(ticker.Json,_contracts,ticker.Source,ticker.Received).ToList();
        var targets=quotes.Where(q=>priorityBases.Contains(q.BaseAsset,StringComparer.Ordinal)).OrderBy(q=>Array.IndexOf(priorityBases.ToArray(),q.BaseAsset))
            .Take(options.Value.DeepCandidates).ToArray();
        foreach(var q in targets)
        {
            try
            {
                var stats=await Get(root+$"contract_stats?contract={Uri.EscapeDataString(q.Symbol)}&interval=5m&limit=100",ct);
                var enriched=AlternativeParser.GateStats(q,stats.Json,stats.Received);
                quotes[quotes.FindIndex(x=>x.Symbol==q.Symbol)]=enriched;
            }
            catch(HttpRequestException){if(_paused.GetValueOrDefault("api.gateio.ws")>time.GetUtcNow())break;}
        }
        return quotes;
    }
    public async Task<IReadOnlyList<VenueQuote>> GetCoinbaseAsync(IReadOnlyList<string> bases,CancellationToken ct)
    {
        const string root="https://api.exchange.coinbase.com/";
        if(_products.Count==0||time.GetUtcNow()-_productsAt>TimeSpan.FromHours(24))
        {
            var products=await Get(root+"products",ct);_products.Clear();
            foreach(var p in products.Json.EnumerateArray())if(AlternativeParser.Text(p,"status")=="online"&&AlternativeParser.Text(p,"quote_currency")=="USD")_products.Add(AlternativeParser.Text(p,"id"));
            _productsAt=products.Received;
        }
        var result=new List<VenueQuote>();
        foreach(var b in bases.Distinct(StringComparer.Ordinal).Where(b=>_products.Contains(b+"-USD")).Take(options.Value.CoinbaseCandidates))
        {
            var ticker=await Get(root+$"products/{Uri.EscapeDataString(b+"-USD")}/ticker",ct);
            if(AlternativeParser.CoinbaseTicker(b,ticker.Json,ticker.Received) is {} quote)result.Add(quote);
        }
        return result;
    }
}
public static class AlternativeParser
{
    public static string Text(JsonElement e,string key)=>e.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString()??"":"";
    private static decimal? Num(JsonElement e,string key)=>e.TryGetProperty(key,out var v)&&decimal.TryParse(v.ToString(),NumberStyles.Float,CultureInfo.InvariantCulture,out var n)?n:null;
    private static decimal? Positive(JsonElement e,string key)=>Num(e,key) is {} n&&n>=0?n:null;
    private static FeatureValue<T> Missing<T>() where T:struct=>FeatureValue<T>.Missing("Gate USDT perpetual");
    public static IReadOnlyList<VenueQuote> GateTickers(JsonElement tickers,JsonElement contracts,DateTimeOffset source,DateTimeOffset received)
    {
        if(source>received)throw new InvalidDataException("未来响应时间");
        var metadata=contracts.EnumerateArray().Where(c=>Text(c,"type")=="direct"&&Text(c,"status")=="trading"
            && (!c.TryGetProperty("in_delisting",out var d)||d.ValueKind!=JsonValueKind.True)).ToDictionary(c=>Text(c,"name"),StringComparer.Ordinal);
        var result=new List<VenueQuote>();
        foreach(var t in tickers.EnumerateArray())
        {
            var symbol=Text(t,"contract");if(!symbol.EndsWith("_USDT",StringComparison.Ordinal)||!metadata.TryGetValue(symbol,out var c))continue;
            var period=Positive(c,"funding_interval");var rate=Num(t,"funding_rate");
            // No conversion from total_size: its convention differs from one-sided OI. Use timestamped contract_stats instead.
            result.Add(new("Gate",symbol,symbol[..^5],"Perpetual",Positive(t,"last"),Positive(t,"volume_24h_quote"),Num(t,"change_percentage"),
                period>0?rate*28800/period:null,source,received,"HTTP Date observation; USDT quote treated as USD proxy",
                Missing<decimal>(),Missing<decimal>(),Missing<double>(),Missing<decimal>(),Missing<decimal>()));
        }
        return result;
    }
    public static VenueQuote GateStats(VenueQuote quote,JsonElement json,DateTimeOffset received)
    {
        var points=json.EnumerateArray().Select(p=>(Json:p,Time:Num(p,"time"))).Where(p=>p.Time>0&&p.Time<=received.ToUnixTimeSeconds())
            .OrderBy(p=>p.Time).DistinctBy(p=>p.Time).ToArray();
        if(points.Length==0)return quote;
        var latest=points[^1];var source=DateTimeOffset.FromUnixTimeSeconds((long)latest.Time!.Value);
        var status=received-source>TimeSpan.FromMinutes(11)?DataStatus.Stale:DataStatus.Valid;
        FeatureValue<decimal> Field(decimal? value)=>new(value,value is null?DataStatus.Missing:status,source,"Gate contract_stats; same venue");
        var prior=points.LastOrDefault(p=>p.Time==latest.Time-900);
        decimal? Change(decimal? a,decimal? b)=>a>=0&&b>0?(a/b-1)*100:null;
        var change=prior.Time is null?null:Change(Positive(latest.Json,"open_interest"),Positive(prior.Json,"open_interest"));
        var changes=new List<double>();
        for(var i=1;i<points.Length-1;i++)if(points[i].Time-points[i-1].Time==300&&Change(Positive(points[i].Json,"open_interest"),Positive(points[i-1].Json,"open_interest")) is {} n)changes.Add((double)n);
        var lastChange=points.Length>=2&&points[^1].Time-points[^2].Time==300?Change(Positive(latest.Json,"open_interest"),Positive(points[^2].Json,"open_interest")):null;
        static double Median(IEnumerable<double> values){var v=values.Order().ToArray();return v.Length%2==1?v[v.Length/2]:(v[v.Length/2-1]+v[v.Length/2])/2;}
        double? z=null;
        if(changes.Count>=30&&lastChange is {}){var median=Median(changes);var mad=Median(changes.Select(x=>Math.Abs(x-median)));if(mad>1e-12)z=((double)lastChange.Value-median)/(1.4826*mad);}
        decimal? Share(JsonElement p,string key)=>Positive(p,key) is {} r?r/(1+r):null;
        var top=prior.Time is null?null:(Share(latest.Json,"top_lsr_size")-Share(prior.Json,"top_lsr_size"))*100;
        return quote with{OpenInterestUsd=Field(Positive(latest.Json,"open_interest_usd")),OiChange15m=Field(change),
            OiRobustZ=new(z,z is null?DataStatus.Missing:status,source,"Gate OI 5m change RZ; >=30 baseline changes"),LongShare=Field(Share(latest.Json,"lsr_account")),TopPositionChange15m=Field(top)};
    }
    public static VenueQuote? CoinbaseTicker(string baseAsset,JsonElement ticker,DateTimeOffset received)
    {
        if(!DateTimeOffset.TryParse(Text(ticker,"time"),CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var source)||source>received)return null;
        // Coinbase volume is base currency, not USD quote turnover. Never label price * base volume as measured quote turnover.
        return new("Coinbase",baseAsset+"-USD",baseAsset,"Spot",Positive(ticker,"price"),null,null,null,source,received,"Last trade time; spot reference only",
            FeatureValue<decimal>.Missing("Spot has no perpetual OI"),Missing<decimal>(),Missing<double>(),Missing<decimal>(),Missing<decimal>());
    }
}
