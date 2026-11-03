using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Providers.Market;
public sealed class TokenCheckProvider(HttpClient http,CoinGeckoPacer pacer,TimeProvider time,IOptions<CoinGeckoOptions> options):ITokenCheckProvider
{
    private async Task<(JsonElement Json,DateTimeOffset Source,DateTimeOffset Received)> Get(string url,bool gecko,CancellationToken ct)
    {
        if(gecko)await pacer.WaitAsync(ct);
        using var request=new HttpRequestMessage(HttpMethod.Get,url);
        if(gecko&&Environment.GetEnvironmentVariable("PULLET_COINGECKO_API_KEY") is {Length:>0} key)request.Headers.Add(new Uri(options.Value.BaseUrl).Host=="pro-api.coingecko.com"?"x-cg-pro-api-key":"x-cg-demo-api-key",key);
        using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
        if((int)response.StatusCode==429)
        {
            var pause=response.Headers.RetryAfter?.Delta??response.Headers.RetryAfter?.Date-time.GetUtcNow()??TimeSpan.FromMinutes(5);
            if(pause<TimeSpan.FromMinutes(1))pause=TimeSpan.FromMinutes(1);
            if(gecko)await pacer.PauseAsync(pause,ct);
            throw new OnChainProviderException("快查来源限流，等待冷却",time.GetUtcNow()+pause);
        }
        response.EnsureSuccessStatusCode();var received=time.GetUtcNow();var source=response.Headers.Date??throw new InvalidDataException("快查来源缺少 HTTP Date");
        if(source>received||received-source>TimeSpan.FromMinutes(45))throw new InvalidDataException("快查来源时间未来或过期");
        await using var stream=await response.Content.ReadAsStreamAsync(ct);using var json=await JsonDocument.ParseAsync(stream,cancellationToken:ct);
        return(json.RootElement.Clone(),source,received);
    }
    public async Task<TokenProjectSnapshot> ProjectAsync(string coinId,CancellationToken ct)
    {
        var r=await Get($"{options.Value.BaseUrl}coins/{Uri.EscapeDataString(coinId)}?localization=false&tickers=false&market_data=true&community_data=false&developer_data=false&sparkline=false",true,ct);
        return TokenCheckParser.Project(r.Json,coinId,r.Received);
    }
    public async Task<TokenVenueIdentity> IdentityAsync(string symbol,CancellationToken ct)
    {
        if(!System.Text.RegularExpressions.Regex.IsMatch(symbol,@"\A[A-Z0-9]{1,20}\z"))throw new ArgumentException("无效交易所币种代码");
        var url=$"https://api.gateio.ws/api/v4/spot/currencies/{symbol}";var r=await Get(url,false,ct);
        return TokenCheckParser.Identity(r.Json,symbol,r.Source,r.Received,url);
    }
}
public static class TokenCheckParser
{
    private static string T(JsonElement e,string key)=>e.ValueKind==JsonValueKind.Object&&e.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString()!:"";
    private static decimal? N(JsonElement e,string key)=>e.ValueKind==JsonValueKind.Object&&e.TryGetProperty(key,out var v)&&decimal.TryParse(v.ToString(),NumberStyles.Float,CultureInfo.InvariantCulture,out var n)?n:null;
    private static decimal? Usd(JsonElement e,string key)=>e.TryGetProperty(key,out var v)?N(v,"usd"):null;
    public static TokenProjectSnapshot Project(JsonElement root,string id,DateTimeOffset received)
    {
        if(T(root,"id")!=id||!DateTimeOffset.TryParse(T(root,"last_updated"),CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var source)||source>received)
            throw new InvalidDataException("项目资料 ID 或源时间不符");
        var p=new Dictionary<string,string>();
        if(root.TryGetProperty("platforms",out var platforms)&&platforms.ValueKind==JsonValueKind.Object)
            foreach(var row in platforms.EnumerateObject())if(row.Value.ValueKind==JsonValueKind.String&&row.Value.GetString() is {Length:>0} a)p[row.Name]=a;
        var links=new List<string>();
        if(root.TryGetProperty("links",out var l)&&l.TryGetProperty("homepage",out var home)&&home.ValueKind==JsonValueKind.Array)
            foreach(var h in home.EnumerateArray())if(h.ValueKind==JsonValueKind.String&&Uri.TryCreate(h.GetString(),UriKind.Absolute,out var u)&&u.Scheme=="https"&&u.UserInfo.Length==0)links.Add(u.AbsoluteUri);
        if(!root.TryGetProperty("market_data",out var m)||m.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("项目来源未提供市场字段");
        decimal? Positive(decimal? n)=>n is >0?n:null;
        return new(id,source,received,Positive(Usd(m,"current_price")),N(m,"price_change_percentage_24h"),Positive(Usd(m,"market_cap")),Positive(Usd(m,"fully_diluted_valuation")),
            Positive(Usd(m,"total_volume")),Positive(N(m,"circulating_supply")),Positive(N(m,"total_supply")),p,links.Distinct().Take(3).ToArray());
    }
    public static TokenVenueIdentity Identity(JsonElement root,string symbol,DateTimeOffset source,DateTimeOffset received,string url)
    {
        if(T(root,"currency")!=symbol||source>received||received-source>TimeSpan.FromMinutes(45))throw new InvalidDataException("交易所币种身份或时间不符");
        var contracts=new Dictionary<string,string>();
        if(root.TryGetProperty("chains",out var chains)&&chains.ValueKind==JsonValueKind.Array)
            foreach(var c in chains.EnumerateArray())
            {
                var chain=T(c,"name") switch{"BSC"=>"binance-smart-chain","ETH"=>"ethereum","BASEEVM"=>"base","SOL"=>"solana","ARBEVM"=>"arbitrum-one","MATIC"=>"polygon-pos",_=>null};
                var address=T(c,"addr");if(chain is not null&&address.Length>0)
                {
                    if(contracts.TryGetValue(chain,out var old)&&old!=address)throw new InvalidDataException("交易所同链返回冲突地址");contracts[chain]=address;
                }
            }
        var delisted=root.TryGetProperty("delisted",out var d)&&d.ValueKind==JsonValueKind.True;
        return new(symbol,T(root,"name"),source,received,delisted,contracts,url);
    }
}
