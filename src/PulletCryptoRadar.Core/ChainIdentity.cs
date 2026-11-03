using System.Numerics;
using System.Text.RegularExpressions;

namespace PulletCryptoRadar.Core;
public static class ChainIdentity
{
    public static string Platform(string chain)=>chain switch{"solana"=>"solana","bsc"=>"binance-smart-chain","ethereum"=>"ethereum","base"=>"base","arbitrum"=>"arbitrum-one","polygon"=>"polygon-pos",_=>throw new ArgumentException("不支持的链")};
    public static StringComparer Comparer(string chain)=>chain=="solana"?StringComparer.Ordinal:StringComparer.OrdinalIgnoreCase;
    public static bool Same(string chain,string? first,string? second)=>Comparer(chain).Equals(first,second);
    public static string Normalize(string chain,string address)=>chain=="solana"?address:address.ToLowerInvariant();
    public static bool Address(string chain,string? address)
    {
        _=Platform(chain);if(address is null)return false;
        if(chain!="solana")return Regex.IsMatch(address,@"\A0x[0-9a-fA-F]{40}\z");
        if(address.Length is <32 or >44)return false;
        const string alphabet="123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
        var value=BigInteger.Zero;
        foreach(var c in address){var digit=alphabet.IndexOf(c);if(digit<0)return false;value=value*58+digit;}
        var leading=address.TakeWhile(c=>c=='1').Count();
        return leading+(value.IsZero?0:value.GetByteCount(isUnsigned:true))==32;
    }
}
