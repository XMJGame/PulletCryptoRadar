using System.Globalization;
using System.Numerics;
using System.Text.Json;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Providers.OnChain;

public static class ChainNetworks
{
    public static string Platform(string chain)=>ChainIdentity.Platform(chain);
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
        if(!ChainIdentity.Address(binding.Chain,binding.ContractAddress))throw new ArgumentException("无效合约 / Mint 地址");
        if(binding.Chain=="solana")
        {
            var solana=await Get(SecurityUrl(binding),ct);
            return OnChainParser.Solana(binding,solana.Json,solana.Source,solana.Received);
        }
        if(binding.Chain is "bsc" or "ethereum")
        {
            var host=binding.Chain=="bsc"?"https://api.binplorer.com/":"https://api.ethplorer.io/";
            var key=Environment.GetEnvironmentVariable("PULLET_EXPLORER_API_KEY");key=string.IsNullOrWhiteSpace(key)?"freekey":key;
            var info=await Get($"{host}getTokenInfo/{binding.ContractAddress}?apiKey={Uri.EscapeDataString(key)}",ct);
            var holders=await Get($"{host}getTopTokenHolders/{binding.ContractAddress}?apiKey={Uri.EscapeDataString(key)}&limit=100",ct);
            var primary=OnChainParser.Explorer(binding,info.Json,holders.Json,info.Source<holders.Source?info.Source:holders.Source,holders.Received);
            // The reservation already covers three requests. Supplemental attribution stays separate
            // from the primary holder series and never creates verified address labels.
            try
            {
                var extra=await Get(SecurityUrl(binding),ct);
                return primary with{Security=OnChainParser.Security(binding,extra.Json,extra.Source,extra.Received)};
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
            catch(Exception ex)when(ex is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException or OnChainProviderException)
            {
                var retry=ex is OnChainProviderException p?p.RetryAt:time.GetUtcNow().AddMinutes(30);
                return primary with{Security=new("GoPlus",binding.Chain,binding.ContractAddress,SecurityUrl(binding),null,time.GetUtcNow(),null,
                    new Dictionary<string,bool?>(),[],"补充核验未完成；保留主索引器数据，未知风险不视为安全",retry)};
            }
        }
        var result=await Get(SecurityUrl(binding),ct);
        return OnChainParser.GoPlus(binding,result.Json,result.Source,result.Received) with{Security=OnChainParser.Security(binding,result.Json,result.Source,result.Received)};
    }
    public static string SecurityUrl(ChainBinding binding)=>binding.Chain=="solana"
        ?$"https://api.gopluslabs.io/api/v1/solana/token_security?contract_addresses={binding.ContractAddress}"
        :$"https://api.gopluslabs.io/api/v1/token_security/{ChainNetworks.Id(binding.Chain)}?contract_addresses={binding.ContractAddress}";
}
public static class OnChainParser
{
    public static OnChainSample Solana(ChainBinding binding,JsonElement response,DateTimeOffset observed,DateTimeOffset received)
    {
        if(binding.Chain!="solana"||!ChainIdentity.Address("solana",binding.ContractAddress)||Text(response,"code")!="1"
            ||!response.TryGetProperty("result",out var result)||result.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("Solana 响应身份或状态无效");
        var matches=result.EnumerateObject().Where(p=>p.Name==binding.ContractAddress).ToArray();
        if(matches.Length!=1||matches[0].Value.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("Solana 未精确返回指定 Mint；禁止忽略大小写");
        var token=matches[0].Value;
        if(!token.TryGetProperty("holders",out var holders)||holders.ValueKind!=JsonValueKind.Array)throw new InvalidDataException("Solana 未提供持币条目");
        var hints=new List<SecurityHolderHint>();var accounts=new HashSet<string>(StringComparer.Ordinal);var entries=new HashSet<(string,string)>();decimal previous=100,sum=0;
        bool? Flag(JsonElement value,string name)=>Text(value,name) switch{"1"=>true,"0"=>false,_=>null};
        foreach(var row in holders.EnumerateArray())
        {
            var owner=Text(row,"account");var account=Text(row,"token_account");
            if(!ChainIdentity.Address("solana",owner)||(account.Length>0&&(!ChainIdentity.Address("solana",account)||!accounts.Add(account)))||!entries.Add((owner,account))
                ||!decimal.TryParse(Text(row,"percent"),NumberStyles.Float,CultureInfo.InvariantCulture,out var fraction)||fraction is <0 or >1)
                throw new InvalidDataException("Solana 账户、重复 Token Account 或占比无效");
            var percent=fraction*100;if(percent>previous)throw new InvalidDataException("Solana 返回条目未按余额排序");previous=percent;sum+=percent;
            hints.Add(new(owner,percent,Text(row,"tag"),null,Flag(row,"is_locked"),account.Length>0?account:null));
        }
        var count=Count(Text(token,"holder_count"));
        if(hints.Count>10||count<hints.Count||sum>100.000000000001m||observed>received||received-observed>TimeSpan.FromMinutes(45))throw new InvalidDataException("Solana 返回覆盖或观测时间无效");
        var nested=new[]{"mintable","freezable","metadata_mutable","balance_mutable_authority","closable","default_account_state_upgradable","transfer_fee_upgradable","transfer_hook_upgradable"};
        var flags=nested.ToDictionary(k=>k,k=>token.TryGetProperty(k,out var v)&&v.ValueKind==JsonValueKind.Object?Flag(v,"status"):null);
        flags["non_transferable"]=Flag(token,"non_transferable");
        var security=new TokenSecurityEvidence("GoPlus Solana",binding.Chain,binding.ContractAddress,OnChainProvider.SecurityUrl(binding),observed,received,count,flags,hints);
        // Missing account IDs may be an owner aggregate overlapping known token accounts.
        // Preserve every claim in Security, but use the larger lower bound instead of adding them twice.
        var rows=hints.GroupBy(h=>h.Address,StringComparer.Ordinal).Select(g=>new TokenHolder(g.Key,"",
                Math.Max(g.Where(h=>h.TokenAccount is not null).Sum(h=>h.Percent),g.Where(h=>h.TokenAccount is null).Select(h=>h.Percent).DefaultIfEmpty(0).Max()),
                string.Join("; ",g.Select(h=>h.ReportedTag).Where(t=>t.Length>0).Distinct())))
            .OrderByDescending(h=>h.Percent).ToArray();
        // SPL token accounts are not globally aggregated owners. Do not manufacture an owner Top10.
        return new(Guid.NewGuid(),binding.Chain,binding.ContractAddress,"GoPlus Solana",observed,received,
            "HTTP Date observation / Solana Beta; returned accounts grouped locally by owner; missing Token Account IDs stay unknown; global owner ranking and unique-wallet count unverified",count,"",null,rows,10,false,security);
    }
    public static TokenSecurityEvidence Security(ChainBinding binding,JsonElement response,DateTimeOffset observed,DateTimeOffset received)
    {
        // Reuse strict identity, ranking and fraction validation. These remain provider claims.
        var sample=GoPlus(binding,response,observed,received);
        var token=response.GetProperty("result").EnumerateObject().Single(p=>p.Name.Equals(binding.ContractAddress,StringComparison.OrdinalIgnoreCase)).Value;
        bool? Flag(JsonElement value,string name)=>Text(value,name) switch{"1"=>true,"0"=>false,_=>null};
        var flags=new[]{"is_open_source","is_proxy","is_mintable","is_honeypot","cannot_sell_all","transfer_pausable","owner_change_balance","hidden_owner","selfdestruct"}
            .ToDictionary(k=>k,k=>Flag(token,k));
        var hints=token.GetProperty("holders").EnumerateArray().Zip(sample.Holders,(raw,h)=>new SecurityHolderHint(h.Address,h.Percent,h.ReportedTag,Flag(raw,"is_contract"),Flag(raw,"is_locked"))).ToArray();
        return new("GoPlus",binding.Chain,binding.ContractAddress,OnChainProvider.SecurityUrl(binding),observed,received,sample.HolderCount,flags,hints);
    }
    public static string Text(JsonElement e,string key)=>e.ValueKind==JsonValueKind.Object&&e.TryGetProperty(key,out var v)&&v.ValueKind is JsonValueKind.String or JsonValueKind.Number?v.ToString():"";
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
        if(Text(response,"code")!="1"||!response.TryGetProperty("result",out var result)||result.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("GoPlus 响应非成功数据");
        var matches=result.EnumerateObject().Where(p=>p.Name.Equals(binding.ContractAddress,StringComparison.OrdinalIgnoreCase)).ToArray();
        if(matches.Length!=1||matches[0].Value.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("GoPlus 未唯一返回指定合约");
        var token=matches[0].Value;
        if(!token.TryGetProperty("holders",out var holders)||holders.ValueKind!=JsonValueKind.Array)throw new InvalidDataException("GoPlus 未提供持有人数组");
        var rows=new List<TokenHolder>();var addresses=new HashSet<string>(StringComparer.OrdinalIgnoreCase);decimal previous=100,sum=0;
        foreach(var row in holders.EnumerateArray())
        {
            var address=Text(row,"address").ToLowerInvariant();
            if(!ChainNetworks.Address(address)||!addresses.Add(address)||!decimal.TryParse(Text(row,"percent"),NumberStyles.Float,CultureInfo.InvariantCulture,out var fraction)||fraction is <0 or >1)throw new InvalidDataException("GoPlus 地址或比例无效");
            var percent=fraction*100;if(percent>previous)throw new InvalidDataException("GoPlus Top10 未排序");previous=percent;sum+=percent;
            rows.Add(new(address,"",percent,Text(row,"tag")));
        }
        var count=Count(Text(token,"holder_count"));
        if(rows.Count>10||count<rows.Count||sum>100.000000000001m||observed>received||received-observed>TimeSpan.FromMinutes(45))throw new InvalidDataException("GoPlus Top10 覆盖或时间无效");
        return new(Guid.NewGuid(),binding.Chain,binding.ContractAddress,"GoPlus",observed,received,"HTTP Date observation; provider exposes Top10 only; no block timestamp",
            count,"",null,rows,10,true);
    }
}
