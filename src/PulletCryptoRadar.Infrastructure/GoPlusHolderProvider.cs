using System.Globalization;
using System.Text;
using System.Text.Json;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Infrastructure;

public sealed class GoPlusHolderProvider(HttpClient http,IRequestLedger ledger,TimeProvider clock)
{
    public static string? Endpoint(AssetIdentity asset)
    {
        var chain=ResearchNetworks.Single(asset);
        var path=chain?.Chain switch {ChainId.Bsc=>"56",ChainId.Ethereum=>"1",ChainId.Solana=>"solana",_=>null};
        return path is null?null:$"https://api.gopluslabs.io/api/v1/{(path=="solana"?"solana/token_security":"token_security/"+path)}?contract_addresses={chain!.Address}";
    }
    public async Task<HolderRead> ReadAsync(AssetIdentity asset,CancellationToken token)
    {
        var url=Endpoint(asset);
        var id=Guid.NewGuid();int? status=null;DateTimeOffset? responseDate=null,retry=null;string? reason=null;
        HolderRead Failure(CollectionFailure failure,string? body=null) => new(new(new(id,"GoPlus",asset.BinanceSymbol,
            url??"Holders",clock.GetUtcNow(),status,failure,body,responseDate,reason??failure.ToString(),retry,RequestPriority.Background),[]),null);
        if(url is null)return Failure(CollectionFailure.Unsupported);
        const int maximum=262144;
        var decision=ledger.TryReserve(id,"GoPlus",clock.GetUtcNow(),maximum,RequestPriority.Background);
        if(!decision.Allowed){reason=decision.Reason;retry=decision.RetryAt;return Failure(CollectionFailure.Quota);}
        var charged=maximum;
        try
        {
            using var response=await http.GetAsync(url,HttpCompletionOption.ResponseHeadersRead,token);
            status=(int)response.StatusCode;responseDate=response.Headers.Date;
            if(status is 401 or 403 or 429)
            {
                retry=response.Headers.RetryAfter?.Date??clock.GetUtcNow().Add(response.Headers.RetryAfter?.Delta??TimeSpan.FromMinutes(15));
                reason=status==429?"CooldownAfterRateLimit":"CooldownAfterForbidden";
                return Failure(status==429?CollectionFailure.RateLimited:CollectionFailure.Forbidden);
            }
            await using var stream=await response.Content.ReadAsStreamAsync(token);
            using var bytes=new MemoryStream();var buffer=new byte[4096];int count;
            while((count=await stream.ReadAsync(buffer,token))>0)
            {
                if(bytes.Length+count>maximum)return Failure(CollectionFailure.Invalid);
                await bytes.WriteAsync(buffer.AsMemory(0,count),token);
            }
            charged=(int)bytes.Length;var body=Encoding.UTF8.GetString(bytes.GetBuffer(),0,charged);
            if(!response.IsSuccessStatusCode){retry=clock.GetUtcNow().AddMinutes(15);reason="CooldownAfterTransport";return Failure(CollectionFailure.Transport,body);}
            try{return Parse(asset,body,clock.GetUtcNow(),id,url,responseDate);}
            catch(Exception error) when(error is JsonException or InvalidDataException or InvalidOperationException or
                ArgumentException or OverflowException or FormatException or KeyNotFoundException)
            {return Failure(CollectionFailure.Invalid,body);}
        }
        catch(OperationCanceledException) when(!token.IsCancellationRequested)
        {retry=clock.GetUtcNow().AddMinutes(15);reason="CooldownAfterTimeout";return Failure(CollectionFailure.Timeout);}
        catch(Exception error) when(error is HttpRequestException or IOException)
        {retry=clock.GetUtcNow().AddMinutes(15);reason="CooldownAfterTransport";return Failure(CollectionFailure.Transport);}
        finally{ledger.CompleteWithReason(id,charged,retry,reason);}
    }

    public static HolderRead Parse(AssetIdentity asset,string body,DateTimeOffset received,Guid id,string endpoint,DateTimeOffset? responseDate=null)
    {
        IdentityValidation.Validate(asset);
        var chain=ResearchNetworks.Single(asset)??throw new InvalidDataException("A reviewed single-chain identity is required.");
        if(endpoint!=Endpoint(asset))throw new InvalidDataException("Holder endpoint does not match the reviewed chain and contract.");
        using var doc=JsonDocument.Parse(body);var root=doc.RootElement;
        if(root.GetProperty("code").GetInt32()!=1)throw new InvalidDataException("GoPlus did not return success.");
        var results=root.GetProperty("result").EnumerateObject().Where(row=>ResearchNetworks.SameAddress(chain.Chain,row.Name,chain.Address)).ToArray();
        if(results.Length!=1)throw new InvalidDataException("Exact chain contract result is missing.");
        var row=results[0].Value;
        decimal Number(JsonElement item,string name) => decimal.Parse(item.GetProperty(name).ToString(),NumberStyles.Float,CultureInfo.InvariantCulture);
        bool? Flag(JsonElement item,string name) => !item.TryGetProperty(name,out var field)?null:field.ToString() switch
            {"1"=>true,"0"=>false,_=>throw new InvalidDataException("Invalid flag.")};
        var supply=Number(row,"total_supply");var holderCount=Number(row,"holder_count");
        if(supply<=0 || holderCount<1 || holderCount>long.MaxValue || decimal.Truncate(holderCount)!=holderCount)
            throw new InvalidDataException("Missing holder coverage or supply.");
        var holders=new List<HolderAddress>();
        var seen=new HashSet<string>(chain.Chain==ChainId.Solana?StringComparer.Ordinal:StringComparer.OrdinalIgnoreCase);
        var seenAccounts=new HashSet<string>(StringComparer.Ordinal);
        foreach(var holder in row.GetProperty("holders").EnumerateArray())
        {
            var address=holder.GetProperty(chain.Chain==ChainId.Solana?"account":"address").GetString()!;
            var balance=Number(holder,"balance");var percent=Number(holder,"percent");
            if(!IdentityValidation.IsAddress(chain.Chain,address)||!seen.Add(address)||balance<=0||balance>supply||percent<=0||percent>1||
                Math.Abs(balance/supply-percent)>.00011m)
                throw new InvalidDataException("Invalid, duplicate or inconsistent holder entry.");
            var accounts=chain.Chain==ChainId.Solana?holder.GetProperty("token_account").GetString()!.Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries):[];
            if(chain.Chain==ChainId.Solana && (accounts.Length==0 || accounts.Any(a=>!IdentityValidation.IsAddress(chain.Chain,a)||!seenAccounts.Add(a))))
                throw new InvalidDataException("Invalid Solana token account list.");
            holders.Add(new(address,balance,balance/supply*100,holder.TryGetProperty("tag",out var tag)?tag.GetString()??"":"",
                Flag(holder,"is_contract"),Flag(holder,"is_locked"),accounts));
        }
        if(holders.Count!=Math.Min(10,(long)holderCount)||holders.Sum(h=>h.Balance)>supply ||
            holders.Zip(holders.Skip(1)).Any(pair=>pair.First.Balance<pair.Second.Balance))
            throw new InvalidDataException("Incomplete or unordered top holder list.");
        var snapshot=new HolderSnapshot(id,asset.AssetId,chain.Chain,chain.Address,"GoPlus",received,supply,(long)holderCount,
            chain.Chain==ChainId.Solana?"ProviderTop10SolanaAccounts":"ProviderTop10Addresses",holders);
        return new(new(new(id,"GoPlus",asset.BinanceSymbol,endpoint,received,200,CollectionFailure.None,body,responseDate,
            Priority:RequestPriority.Background),[]),snapshot);
    }
}
