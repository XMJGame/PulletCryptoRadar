using System.Globalization;
using System.Numerics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Providers.OnChain;
public static class MonitorAbi
{
    public static BigInteger UInt(string hex)=>System.Text.RegularExpressions.Regex.IsMatch(hex??"",@"\A0x[0-9a-fA-F]{64}\z")?
        BigInteger.Parse("0"+hex![2..],NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture):throw new InvalidDataException("ABI uint256 返回长度或编码无效");
    public static string Address(string hex)
    {if(hex.Length!=66||!hex.StartsWith("0x")||hex[2..26]!=new string('0',24)||!ChainNetworks.Address("0x"+hex[26..]))throw new InvalidDataException("ABI address 无效");return "0x"+hex[26..].ToLowerInvariant();}
    public static bool Bool(string hex)=>UInt(hex) switch{var n when n==0=>false,var n when n==1=>true,_=>throw new InvalidDataException("ABI bool 无效")};
    public static VestingAllocation Allocation(int index,string hex)
    {
        if(index is <0 or >5||hex.Length!=2+6*64||!hex.StartsWith("0x"))throw new InvalidDataException("锁仓分配 ABI 长度无效");
        var words=Enumerable.Range(0,6).Select(i=>"0x"+hex.Substring(2+i*64,64)).ToArray();
        var cap=UInt(words[0]);var released=UInt(words[1]);var period=UInt(words[3]);var next=UInt(words[4]);var amount=UInt(words[5]);
        if(released>cap||period>10L*365*86400||next>253402300799)throw new InvalidDataException("锁仓状态数值无效");
        return new(index,new[]{"投资者","市场运营","金库","团队","流动性","生态奖励"}[index],cap.ToString(CultureInfo.InvariantCulture),released.ToString(CultureInfo.InvariantCulture),Address(words[2]),(long)period,
            next>0?DateTimeOffset.FromUnixTimeSeconds((long)next):null,amount.ToString(CultureInfo.InvariantCulture));
    }
    public static string AddressSetHash(IReadOnlyList<string> addresses)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(",",addresses.Select(a=>a.ToLowerInvariant()).Distinct().Order(StringComparer.Ordinal)))));
}
public sealed class TokenMonitorProvider(HttpClient http,TimeProvider time):ITokenMonitorProvider
{
    private int calls;private long bytes;
    private async Task<JsonElement> Rpc(string method,object?[] args,CancellationToken ct)
    {
        if(++calls>64)throw new InvalidDataException("持续研究单轮64次RPC上限，断点不前移");
        if(calls>1)await Task.Delay(TimeSpan.FromMilliseconds(100),time,ct);
        using var request=new HttpRequestMessage(HttpMethod.Post,""){Content=JsonContent.Create(new{jsonrpc="2.0",id=calls,method,@params=args})};
        using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
        if(!response.IsSuccessStatusCode)
        {
            var delay=response.Headers.RetryAfter?.Delta??response.Headers.RetryAfter?.Date-time.GetUtcNow()??TimeSpan.FromMinutes(30);
            if(delay<TimeSpan.FromMinutes(15))delay=TimeSpan.FromMinutes(15);
            throw new OnChainProviderException($"持续研究RPC HTTP {(int)response.StatusCode}",time.GetUtcNow()+delay);
        }
        await using var input=await response.Content.ReadAsStreamAsync(ct);using var output=new MemoryStream();var buffer=new byte[16384];int count;
        while((count=await input.ReadAsync(buffer,ct))>0)
        {bytes+=count;if(output.Length+count>2_000_000||bytes>16_000_000)throw new InvalidDataException("持续研究RPC响应超过读取上限，未跳过区块");output.Write(buffer,0,count);}
        using var doc=JsonDocument.Parse(output.ToArray());var root=doc.RootElement;
        if(root.TryGetProperty("error",out _))throw new OnChainProviderException("持续研究RPC方法/历史范围不可用，保留断点",time.GetUtcNow().AddMinutes(30));
        if(!root.TryGetProperty("id",out var id)||!id.TryGetInt32(out var value)||value!=calls||!root.TryGetProperty("result",out var result)||result.ValueKind==JsonValueKind.Null)
            throw new InvalidDataException("持续研究RPC返回身份无效");
        return result.Clone();
    }
    private static string Hex(long n)=>"0x"+n.ToString("x",CultureInfo.InvariantCulture);
    private async Task<(string Hash,DateTimeOffset Time)> Block(long n,CancellationToken ct)
    {
        var b=await Rpc("eth_getBlockByNumber",[Hex(n),false],ct);var hash=b.GetProperty("hash").GetString()!;
        var at=DateTimeOffset.FromUnixTimeSeconds(TransferLogParser.Number(b.GetProperty("timestamp").GetString()!));
        if(TransferLogParser.Number(b.GetProperty("number").GetString()!)!=n||!TransferLogParser.Hash(hash)||at>time.GetUtcNow())throw new InvalidDataException("持续研究区块锚点无效");return(hash,at);
    }
    private async Task<string> Call(string address,string data,long block,CancellationToken ct)=>(await Rpc("eth_call",[new{to=address,data},Hex(block)],ct)).GetString()??throw new InvalidDataException("合约读取结果为空");
    public async Task<TokenMonitorSnapshot> FetchAsync(TokenMonitorTarget target,IReadOnlyList<string> addresses,TokenMonitorSnapshot? previous,CancellationToken ct)
    {
        calls=0;bytes=0;
        if(target.Platform!="binance-smart-chain"||!ChainNetworks.Address(target.ContractAddress)||addresses.Count is <1 or >16||addresses.Any(a=>!ChainNetworks.Address(a)))throw new ArgumentException("持续研究链、完整合约或观察地址无效");
        var tracked=addresses.Select(a=>a.ToLowerInvariant()).Distinct().Order(StringComparer.Ordinal).ToArray();var setHash=MonitorAbi.AddressSetHash(tracked);
        if(TransferLogParser.Number((await Rpc("eth_chainId",[],ct)).GetString()!)!=56)throw new InvalidDataException("持续研究RPC链不是BSC 56");
        var head=TransferLogParser.Number((await Rpc("eth_blockNumber",[],ct)).GetString()!);var safe=head-64;if(safe<0)throw new InvalidDataException("确认区块不足");
        var anchor=await Block(safe,ct);if(time.GetUtcNow()-anchor.Time>TimeSpan.FromMinutes(15))throw new InvalidDataException("RPC确认区块失鲜");
        if(previous is not null)
        {
            if(previous.TargetId!=target.Id||previous.ContractAddress!=target.ContractAddress||previous.Window.NextBlock-1>safe)throw new InvalidDataException("持续研究断点身份/高度不符");
            if((await Block(previous.Window.NextBlock-1,ct)).Hash!=previous.Window.EndBlockHash)throw new FlowReorgException();
        }
        var decimalsValue=MonitorAbi.UInt(await Call(target.ContractAddress,"0x313ce567",safe,ct));if(decimalsValue>36)throw new InvalidDataException("代币精度超出支持范围");
        var supply=MonitorAbi.UInt(await Call(target.ContractAddress,"0x18160ddd",safe,ct));if(supply<=0)throw new InvalidDataException("总供应量无效");
        var balances=new List<MonitorBalance>();
        foreach(var a in tracked)
        {var raw=MonitorAbi.UInt(await Call(target.ContractAddress,"0x70a08231"+a[2..].PadLeft(64,'0'),safe,ct));if(raw>supply)throw new InvalidDataException("地址余额大于总供应量");balances.Add(new(a,raw.ToString(CultureInfo.InvariantCulture)));}
        VestingSnapshot? vesting=null;
        if(target.VestingContract is {} vest)
        {
            if(!ChainNetworks.Address(vest)||target.VestingRuntimeSha256 is not {Length:64})throw new InvalidDataException("锁仓合约缺少代码核对基线");
            var code=(await Rpc("eth_getCode",[vest,Hex(safe)],ct)).GetString()!;
            if(!System.Text.RegularExpressions.Regex.IsMatch(code,@"\A0x(?:[0-9a-fA-F]{2})+\z"))throw new InvalidDataException("锁仓合约代码为空或无效");
            var digest=Convert.ToHexStringLower(SHA256.HashData(Convert.FromHexString(code[2..])));
            if(digest!=target.VestingRuntimeSha256)throw new InvalidDataException("锁仓合约运行代码改变/与已审阅源码不符，停止解析并保留旧记录");
            var token=MonitorAbi.Address(await Call(vest,"0xfc0c546a",safe,ct));if(!token.Equals(target.ContractAddress,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("锁仓token()不是所选代币");
            var admin=MonitorAbi.Address(await Call(vest,"0xf851a440",safe,ct));
            var tokenSet=MonitorAbi.Bool(await Call(vest,"0x40624824",safe,ct));
            if(!tokenSet)throw new InvalidDataException("锁仓tokenSet状态未确认");
            var tge=MonitorAbi.Bool(await Call(vest,"0xdf90b6fb",safe,ct));var allocations=new List<VestingAllocation>();
            for(var i=0;i<6;i++)allocations.Add(MonitorAbi.Allocation(i,await Call(vest,"0x51d1eb8b"+i.ToString("x64",CultureInfo.InvariantCulture),safe,ct)));
            var balance=balances.FirstOrDefault(b=>b.Address==vest.ToLowerInvariant())?.RawBalance??MonitorAbi.UInt(await Call(target.ContractAddress,"0x70a08231"+vest[2..].PadLeft(64,'0'),safe,ct)).ToString(CultureInfo.InvariantCulture);
            vesting=new(vest,digest,token,admin,tokenSet,tge,balance,allocations,target.SourceUrl??"",target.PermissionReview??"权限未核验");
        }
        var from=previous?.Window.NextBlock??Math.Max(0,safe-1999);var to=Math.Min(safe,from+19999);
        var window=previous?.Window;var transfers=new List<TokenTransfer>();
        if(from<=to)
        {
            JsonElement[] logs=[];var topics=tracked.Select(a=>"0x"+a[2..].PadLeft(64,'0')).ToArray();
            for(var split=0;split<16;split++)
            {
                var outbound=await Rpc("eth_getLogs",[new{address=target.ContractAddress,fromBlock=Hex(from),toBlock=Hex(to),topics=new object?[]{TransferLogParser.Topic,topics}}],ct);
                var inbound=await Rpc("eth_getLogs",[new{address=target.ContractAddress,fromBlock=Hex(from),toBlock=Hex(to),topics=new object?[]{TransferLogParser.Topic,null,topics}}],ct);
                if(outbound.ValueKind!=JsonValueKind.Array||inbound.ValueKind!=JsonValueKind.Array)throw new InvalidDataException("转账日志不是数组");
                logs=outbound.EnumerateArray().Concat(inbound.EnumerateArray()).Select(l=>l.Clone()).ToArray();
                if(logs.Length<=1000&&logs.Select(l=>l.GetProperty("blockNumber").GetString()).Distinct().Count()<=16)break;
                if(to==from)throw new InvalidDataException("单区块转账超出本机上限，未跳过该区块");to=from+(to-from)/2;
            }
            if(logs.Length>1000||logs.Select(l=>l.GetProperty("blockNumber").GetString()).Distinct().Count()>16)throw new InvalidDataException("日志拆分上限，断点不前移");
            var headers=new Dictionary<long,(string Hash,DateTimeOffset Time)>();
            foreach(var n in logs.Select(l=>TransferLogParser.Number(l.GetProperty("blockNumber").GetString()!)).Append(from).Append(to).Distinct())
            {if(n<from||n>to)throw new InvalidDataException("RPC日志越界");headers[n]=n==safe?anchor:await Block(n,ct);}
            var binding=new ChainBinding(0,"bsc",target.ContractAddress,target.CoinGeckoId,true,true,0,time.GetUtcNow(),"Fixed research target",[]);
            transfers.AddRange(TransferLogParser.Parse(binding,JsonSerializer.SerializeToElement(logs),from,to,headers,time.GetUtcNow()));
            if(transfers.Any(t=>!tracked.Contains(t.From)&&!tracked.Contains(t.To)))throw new InvalidDataException("RPC返回观察地址范围外转账");
            var start=window?.AddressSetHash==setHash?window.StartTime:headers[from].Time;
            var startBlock=window?.AddressSetHash==setHash?window.StartBlock:from;
            window=new(startBlock,to+1,headers[to].Hash,start,headers[to].Time,setHash);
        }
        if(window is null)throw new InvalidDataException("未建立转账窗口");
        if((await Block(safe,ct)).Hash!=anchor.Hash||(await Block(window.NextBlock-1,ct)).Hash!=window.EndBlockHash)throw new FlowReorgException();
        return new(Guid.NewGuid(),target.Id,target.ContractAddress,safe,anchor.Hash,anchor.Time,time.GetUtcNow(),(int)decimalsValue,supply.ToString(CultureInfo.InvariantCulture),balances,vesting,window,transfers,calls);
    }
}
