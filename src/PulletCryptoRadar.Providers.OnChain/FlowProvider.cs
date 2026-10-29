using System.Globalization;
using System.Numerics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Providers.OnChain;

public sealed class FlowProvider(HttpClient http,FlowOptions options,TimeProvider time):IFlowProvider
{
    private int _calls;
    private async Task<JsonElement> Rpc(string method,object[] parameters,CancellationToken ct)
    {
        if(++_calls>160)throw new InvalidDataException("单轮 RPC 请求预算已用完，断点未前移");
        if(_calls>1)await Task.Delay(TimeSpan.FromMilliseconds(150),time,ct);
        using var response=await http.PostAsJsonAsync("",new{jsonrpc="2.0",id=_calls,method,@params=parameters},ct);
        if(!response.IsSuccessStatusCode)throw new OnChainProviderException($"转账 RPC HTTP {(int)response.StatusCode}，停止本轮请求",time.GetUtcNow().AddMinutes(15));
        if(response.Content.Headers.ContentLength>8_000_000)throw new InvalidDataException("RPC 响应超过单页大小限制");
        await using var stream=await response.Content.ReadAsStreamAsync(ct);using var document=await JsonDocument.ParseAsync(stream,cancellationToken:ct);
        var root=document.RootElement;
        if(root.TryGetProperty("error",out _))throw new OnChainProviderException("RPC 返回错误；可能是限额、历史覆盖或方法限制，断点未前移",time.GetUtcNow().AddMinutes(15));
        if(root.GetProperty("id").GetInt32()!=_calls||!root.TryGetProperty("result",out var value)||value.ValueKind==JsonValueKind.Null)throw new InvalidDataException("RPC 响应缺少匹配结果");
        return value.Clone();
    }
    private static string Hex(long n)=>"0x"+n.ToString("x",CultureInfo.InvariantCulture);
    private async Task<(string Hash,DateTimeOffset Time)> Block(long n,CancellationToken ct)
    {
        var b=await Rpc("eth_getBlockByNumber",[Hex(n),false],ct);
        if(TransferLogParser.Number(b.GetProperty("number").GetString()!)!=n)throw new InvalidDataException("区块编号不匹配");
        var hash=b.GetProperty("hash").GetString()!;if(!TransferLogParser.Hash(hash))throw new InvalidDataException("区块哈希无效");
        var timestamp=DateTimeOffset.FromUnixTimeSeconds(TransferLogParser.Number(b.GetProperty("timestamp").GetString()!));
        if(timestamp>time.GetUtcNow())throw new InvalidDataException("区块时间处于未来");return(hash,timestamp);
    }
    public async Task<FlowBatch> FetchAsync(ChainBinding binding,FlowCursor? cursor,IReadOnlyList<string> inspect,CancellationToken ct)
    {
        _calls=0;
        if(binding.Chain!="bsc"||!ChainNetworks.Address(binding.ContractAddress))throw new ArgumentException("当前资金流采集只支持已绑定 BSC 合约");
        if(TransferLogParser.Number((await Rpc("eth_chainId",[],ct)).GetString()!)!=56)throw new InvalidDataException("RPC 链 ID 不是 BSC 56");
        var head=TransferLogParser.Number((await Rpc("eth_blockNumber",[],ct)).GetString()!);var safe=head-options.Confirmations;
        if(safe<0||cursor is not null&&(cursor.Chain!=binding.Chain||cursor.ContractAddress!=binding.ContractAddress||safe<cursor.NextBlock-1))throw new InvalidDataException("RPC 高度倒退或断点身份不匹配");
        if(cursor is not null&&(await Block(cursor.NextBlock-1,ct)).Hash!=cursor.LastBlockHash)throw new FlowReorgException();
        var from=cursor?.NextBlock??Math.Max(0,safe-options.InitialBlocks+1);var to=Math.Min(safe,from+options.PageBlocks-1);
        if(to<from)throw new InvalidDataException("确认区块尚未前移");
        JsonElement logs=default;
        for(var split=0;split<10;split++)
        {
            logs=await Rpc("eth_getLogs",[new{address=binding.ContractAddress,fromBlock=Hex(from),toBlock=Hex(to),topics=new[]{TransferLogParser.Topic}}],ct);
            if(logs.ValueKind!=JsonValueKind.Array)throw new InvalidDataException("日志响应不是数组");
            if(logs.GetArrayLength()<=5000&&logs.EnumerateArray().Select(l=>l.GetProperty("blockNumber").GetString()).Distinct().Count()<=120)break;
            if(to==from)throw new InvalidDataException("单区块日志超出本机处理上限，未跳过该区块");to=from+(to-from)/2;
        }
        var headers=new Dictionary<long,(string Hash,DateTimeOffset Time)>();
        foreach(var n in logs.EnumerateArray().Select(l=>TransferLogParser.Number(l.GetProperty("blockNumber").GetString()!)).Append(to).Append(from).Distinct())
        {if(n<from||n>to)throw new InvalidDataException("RPC 返回范围外日志");headers[n]=await Block(n,ct);}
        var rows=TransferLogParser.Parse(binding,logs,from,to,headers,time.GetUtcNow());var end=headers[to];
        var inspections=new List<AddressInspection>();
        foreach(var address in inspect.Distinct(StringComparer.OrdinalIgnoreCase).Take(10))
        {
            if(!ChainNetworks.Address(address))continue;
            var raw=(await Rpc("eth_getCode",[address,Hex(to)],ct)).GetString()!;
            if(!System.Text.RegularExpressions.Regex.IsMatch(raw,@"\A0x(?:[0-9a-fA-F]{2})*\z"))throw new InvalidDataException("无效合约字节码");
            var hasCode=raw.Length>2;var digest=Convert.ToHexStringLower(SHA256.HashData(Convert.FromHexString(raw[2..])));
            inspections.Add(new("bsc",address.ToLowerInvariant(),to,end.Hash,hasCode,digest,time.GetUtcNow(),"BSC RPC eth_getCode / chainId 56",
                hasCode?"该区块存在合约代码；所有者、项目归属、LP/锁仓用途仍待证据核对":"该区块未返回合约代码；不证明归属或实际控制人"));
        }
        if((await Block(to,ct)).Hash!=end.Hash)throw new FlowReorgException();
        return new(new("bsc",binding.ContractAddress,cursor?.StartBlock??from,to+1,end.Hash,end.Time,head,time.GetUtcNow()){StartTime=cursor?.StartTime??headers[from].Time,ConfirmationDepth=options.Confirmations},cursor?.NextBlock,rows,inspections);
    }
}
public static class TransferLogParser
{
    public const string Topic="0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef";
    public static bool Hash(string value)=>System.Text.RegularExpressions.Regex.IsMatch(value??"",@"\A0x[0-9a-fA-F]{64}\z");
    public static long Number(string hex)=>hex.StartsWith("0x",StringComparison.Ordinal)&&hex.Length>2&&long.TryParse(hex[2..],NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture,out var n)&&n>=0?n:throw new InvalidDataException("无效 RPC 整数");
    public static IReadOnlyList<TokenTransfer> Parse(ChainBinding b,JsonElement logs,long from,long to,IReadOnlyDictionary<long,(string Hash,DateTimeOffset Time)> headers,DateTimeOffset received)
    {
        var rows=new Dictionary<(string,int),TokenTransfer>();
        foreach(var l in logs.EnumerateArray())
        {
            var address=l.GetProperty("address").GetString()!;var topics=l.GetProperty("topics").EnumerateArray().Select(v=>v.GetString()!).ToArray();
            var tx=l.GetProperty("transactionHash").GetString()!.ToLowerInvariant();var hash=l.GetProperty("blockHash").GetString()!.ToLowerInvariant();
            var block=Number(l.GetProperty("blockNumber").GetString()!);var index=Number(l.GetProperty("logIndex").GetString()!);var data=l.GetProperty("data").GetString()!;
            if(!address.Equals(b.ContractAddress,StringComparison.OrdinalIgnoreCase)||topics.Length!=3||topics[0]!=Topic||!Hash(tx)||!Hash(hash)||!Hash(data)
                ||topics.Skip(1).Any(t=>!Hash(t)||t[2..26]!=new string('0',24))||block<from||block>to||index>int.MaxValue
                ||l.GetProperty("removed").GetBoolean()||!headers.TryGetValue(block,out var h)||!h.Hash.Equals(hash,StringComparison.OrdinalIgnoreCase)||h.Time>received)
                throw new InvalidDataException("Transfer 日志身份、主题、区块或移除标记无效");
            var raw=BigInteger.Parse("0"+data[2..],NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
            var row=new TokenTransfer(b.Chain,b.ContractAddress,block,hash,tx,(int)index,h.Time,"0x"+topics[1][26..].ToLowerInvariant(),"0x"+topics[2][26..].ToLowerInvariant(),raw,"BSC RPC",received);
            if(rows.TryGetValue((tx,(int)index),out var old)&&old!=row)throw new InvalidDataException("相同交易日志发生冲突");rows[(tx,(int)index)]=row;
        }
        return rows.Values.OrderBy(r=>r.BlockNumber).ThenBy(r=>r.LogIndex).ToArray();
    }
}
