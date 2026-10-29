using System.Globalization;
using System.Numerics;
using System.Text.Json;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Providers.OnChain;

public static class ChainNetworks
{
    public static string Platform(string chain)=>chain switch{"bsc"=>"binance-smart-chain","ethereum"=>"ethereum","base"=>"base","arbitrum"=>"arbitrum-one","polygon"=>"polygon-pos",_=>throw new ArgumentException("不支持的 EVM 链")};
    public static int Id(string chain)=>chain switch{"bsc"=>56,"ethereum"=>1,"base"=>8453,"arbitrum"=>42161,"polygon"=>137,_=>throw new ArgumentException("不支持的 EVM 链")};
    public static bool Address(string? value)=>value is not null&&System.Text.RegularExpressions.Regex.IsMatch(value,@"\A0x[0-9a-fA-F]{40}\z");
}
public sealed class OnChainProvider(HttpClient http,TimeProvider time):IOnChainProvider
{
    private readonly SemaphoreSlim _gate=new(1,1);private DateTimeOffset _next;
    private async Task<(JsonElement Json,DateTimeOffset Source,DateTimeOffset Received)> Get(string url,CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if(_next>time.GetUtcNow())await Task.Delay(_next-time.GetUtcNow(),time,ct);_next=time.GetUtcNow().AddSeconds(1.1);
            using var response=await http.GetAsync(url,HttpCompletionOption.ResponseHeadersRead,ct);
            if(!response.IsSuccessStatusCode)
            {
                var delay=response.Headers.RetryAfter?.Delta??response.Headers.RetryAfter?.Date-time.GetUtcNow()??TimeSpan.FromMinutes(30);
                if(delay<TimeSpan.FromMinutes(5))delay=TimeSpan.FromMinutes(5);
                throw new OnChainProviderException($"链上接口 HTTP {(int)response.StatusCode}，已停止本轮请求",time.GetUtcNow()+delay);
            }
            var received=time.GetUtcNow();var source=response.Headers.Date??throw new InvalidDataException("链上响应缺少 HTTP Date，不能伪造观测时间");
            if(source>received||received-source>TimeSpan.FromMinutes(45))throw new InvalidDataException("链上响应时间未来或过期");
            await using var stream=await response.Content.ReadAsStreamAsync(ct);using var json=await JsonDocument.ParseAsync(stream,cancellationToken:ct);
            if(json.RootElement.TryGetProperty("error",out _))throw new OnChainProviderException("索引器返回错误对象；请检查额度、权限或合约覆盖",time.GetUtcNow().AddMinutes(30));
            return(json.RootElement.Clone(),source,received);
        }
        finally{_gate.Release();}
    }
    public async Task<OnChainSample> FetchAsync(ChainBinding binding,CancellationToken ct)
    {
        if(!ChainNetworks.Address(binding.ContractAddress))throw new ArgumentException("无效合约地址");
        if(binding.Chain is "bsc" or "ethereum")
        {
            var host=binding.Chain=="bsc"?"https://api.binplorer.com/":"https://api.ethplorer.io/";
            var key=Environment.GetEnvironmentVariable("PULLET_EXPLORER_API_KEY");key=string.IsNullOrWhiteSpace(key)?"freekey":key;
            var info=await Get($"{host}getTokenInfo/{binding.ContractAddress}?apiKey={Uri.EscapeDataString(key)}",ct);
            var holders=await Get($"{host}getTopTokenHolders/{binding.ContractAddress}?apiKey={Uri.EscapeDataString(key)}&limit=100",ct);
            return OnChainParser.Explorer(binding,info.Json,holders.Json,info.Source<holders.Source?info.Source:holders.Source,holders.Received);
        }
        var result=await Get($"https://api.gopluslabs.io/api/v1/token_security/{ChainNetworks.Id(binding.Chain)}?contract_addresses={binding.ContractAddress}",ct);
        return OnChainParser.GoPlus(binding,result.Json,result.Source,result.Received);
    }
}
public static class OnChainParser
{
    public static string Text(JsonElement e,string key)=>e.TryGetProperty(key,out var v)&&v.ValueKind is JsonValueKind.String or JsonValueKind.Number?v.ToString():"";
    private static BigInteger Integer(string value)=>BigInteger.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out var n)&&n>=0&&n<(BigInteger.One<<256)?n:throw new InvalidDataException("无效 uint256 原始余额或供应量");
    private static long? Count(string value)=>long.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out var n)&&n>=0?n:null;
    public static OnChainSample Explorer(ChainBinding binding,JsonElement info,JsonElement response,DateTimeOffset observed,DateTimeOffset received)
    {
        if(!Text(info,"address").Equals(binding.ContractAddress,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("索引器响应的合约身份不符");
        var supply=Integer(Text(info,"totalSupply"));if(supply<=0)throw new InvalidDataException("供应量为零，不能计算占比");
        var updated=long.TryParse(Text(info,"lastUpdated"),out var ts)?DateTimeOffset.FromUnixTimeSeconds(ts):observed;
        if(updated>received||observed>received)throw new InvalidDataException("未来索引时间");
        var rows=new List<TokenHolder>();var previous=supply;var addresses=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var sum=BigInteger.Zero;
        foreach(var row in response.GetProperty("holders").EnumerateArray())
        {
            var address=Text(row,"address").ToLowerInvariant();var balance=Integer(Text(row,"rawBalance"));
            if(!ChainNetworks.Address(address)||!addresses.Add(address)||balance>previous)throw new InvalidDataException("持有人地址重复、格式错误或未按余额排序");
            previous=balance;sum+=balance;if(sum>supply)throw new InvalidDataException("已返回余额大于供应量");
            // All arithmetic stays exact until a bounded 18-decimal percentage conversion.
            var percent=(decimal)(balance*BigInteger.Pow(10,20)/supply)/1_000_000_000_000_000_000m;
            rows.Add(new(address,balance.ToString(CultureInfo.InvariantCulture),percent));
        }
        if(rows.Count>100)throw new InvalidDataException("超出请求的 Top100 范围");
        var count=Count(Text(info,"holdersCount"));if(count<rows.Count)throw new InvalidDataException("持有人数量小于返回地址数");
        return new(Guid.NewGuid(),binding.Chain,binding.ContractAddress,binding.Chain=="bsc"?"Binplorer":"Ethplorer",updated<observed?updated:observed,received,
            "Indexer lastUpdated / HTTP Date minimum; observation, not a verified block timestamp",count,supply.ToString(CultureInfo.InvariantCulture),
            int.TryParse(Text(info,"decimals"),out var decimals)&&decimals is >=0 and <=255?decimals:null,rows,100,true);
    }
    public static OnChainSample GoPlus(ChainBinding binding,JsonElement response,DateTimeOffset observed,DateTimeOffset received)
    {
        if(Text(response,"code")!="1"||!response.TryGetProperty("result",out var result))throw new InvalidDataException("GoPlus 响应非成功数据");
        var token=result.EnumerateObject().FirstOrDefault(p=>p.Name.Equals(binding.ContractAddress,StringComparison.OrdinalIgnoreCase)).Value;
        if(token.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("GoPlus 未返回指定合约");
        var rows=new List<TokenHolder>();var addresses=new HashSet<string>(StringComparer.OrdinalIgnoreCase);decimal previous=100,sum=0;
        foreach(var row in token.GetProperty("holders").EnumerateArray())
        {
            var address=Text(row,"address").ToLowerInvariant();
            if(!ChainNetworks.Address(address)||!addresses.Add(address)||!decimal.TryParse(Text(row,"percent"),NumberStyles.Float,CultureInfo.InvariantCulture,out var fraction)||fraction is <0 or >1)throw new InvalidDataException("GoPlus 地址或比例无效");
            var percent=fraction*100;if(percent>previous)throw new InvalidDataException("GoPlus Top10 未排序");previous=percent;sum+=percent;
            rows.Add(new(address,"",percent,Text(row,"tag")));
        }
        if(rows.Count>10||sum>100.000000000001m||observed>received)throw new InvalidDataException("GoPlus Top10 覆盖或时间无效");
        return new(Guid.NewGuid(),binding.Chain,binding.ContractAddress,"GoPlus",observed,received,"HTTP Date observation; provider exposes Top10 only; no block timestamp",
            Count(Text(token,"holder_count")),"",null,rows,10,true);
    }
}
