using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Infrastructure;

public sealed partial class BinanceHistoricalArchive(HttpClient http,IRequestLedger ledger,TimeProvider clock,string directory)
{
    public async Task<HistoricalArchive> CandlesAsync(string symbol,DateTimeOffset month,CancellationToken token)
    {
        if(!System.Text.RegularExpressions.Regex.IsMatch(symbol,@"\A[A-Z0-9]{1,30}USDT\z")||month.Day!=1||month.TimeOfDay!=TimeSpan.Zero||month.Offset!=TimeSpan.Zero||month>=clock.GetUtcNow())
            throw new ArgumentException("Use an exact futures symbol and past UTC month.");
        var name=$"{symbol}-1h-{month:yyyy-MM}.zip";
        var url=$"https://data.binance.vision/data/futures/um/monthly/klines/{symbol}/1h/{name}";
        Directory.CreateDirectory(directory);var path=Path.Combine(directory,name);var manifestPath=path+".json";
        if(File.Exists(path)&&File.Exists(manifestPath))
        {
            var manifest=JsonSerializer.Deserialize<HistoricalArchive>(await File.ReadAllTextAsync(manifestPath,token))!;
            var cached=await File.ReadAllBytesAsync(path,token);
            if(manifest.Url!=url||manifest.Symbol!=symbol||manifest.Month!=month.ToString("yyyy-MM")||
                !string.Equals(Convert.ToHexString(SHA256.HashData(cached)),manifest.Sha256,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Cached archive checksum or identity mismatch.");
            return manifest with {Candles=Parse(symbol,month,cached,manifest.DownloadedAt)};
        }
        var checksum=Encoding.UTF8.GetString(await BudgetedDownload(url+".CHECKSUM",4096,token));
        var parts=checksum.Trim().Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);
        if(parts.Length!=2||parts[1].TrimStart('*')!=name||!System.Text.RegularExpressions.Regex.IsMatch(parts[0],@"\A[0-9a-fA-F]{64}\z"))
            throw new InvalidDataException("Invalid official checksum identity.");
        var bytes=await BudgetedDownload(url,1_000_000,token);var sha=Convert.ToHexString(SHA256.HashData(bytes));
        if(!sha.Equals(parts[0],StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Official SHA256 mismatch.");
        var at=clock.GetUtcNow();var result=new HistoricalArchive(symbol,month.ToString("yyyy-MM"),url,sha,bytes.Length,at,Parse(symbol,month,bytes,at));
        await File.WriteAllBytesAsync(path,bytes,token);
        await File.WriteAllTextAsync(manifestPath,JsonSerializer.Serialize(result with {Candles=[]}),token);
        return result;
    }
    private async Task<byte[]> Download(string url,int limit,CancellationToken token)
    {
        var id=Guid.NewGuid();var decision=ledger.TryReserve(id,"BinanceArchive",clock.GetUtcNow(),limit,RequestPriority.Background);
        if(!decision.Allowed)throw new InvalidOperationException($"Archive budget: {decision.Reason}; retry {decision.RetryAt:O}");
        var charged=limit;DateTimeOffset? cooldown=null;string? reason=null;
        try
        {
            using var response=await http.GetAsync(url,HttpCompletionOption.ResponseHeadersRead,token);
            if((int)response.StatusCode is 403 or 429){cooldown=clock.GetUtcNow().AddMinutes(15);reason="CooldownAfterRateLimit";}
            response.EnsureSuccessStatusCode();await using var stream=await response.Content.ReadAsStreamAsync(token);
            using var bytes=new MemoryStream();var buffer=new byte[4096];int count;
            while((count=await stream.ReadAsync(buffer,token))>0)
            {
                if(bytes.Length+count>limit)throw new InvalidDataException("Archive response exceeds budget.");
                await bytes.WriteAsync(buffer.AsMemory(0,count),token);
            }
            charged=(int)bytes.Length;return bytes.ToArray();
        }
        finally{ledger.CompleteWithReason(id,charged,cooldown,reason);}
    }
    public static IReadOnlyList<HourCandle> Parse(string symbol,DateTimeOffset month,byte[] bytes,DateTimeOffset received)
    {
        using var memory=new MemoryStream(bytes);using var zip=new ZipArchive(memory,ZipArchiveMode.Read);
        if(zip.Entries.Count!=1||zip.Entries[0].Name!=$"{symbol}-1h-{month:yyyy-MM}.csv"||zip.Entries[0].Length>2_000_000)
            throw new InvalidDataException("Unexpected archive contents.");
        using var reader=new StreamReader(zip.Entries[0].Open());var rows=new List<HourCandle>();string? line;
        while((line=reader.ReadLine()) is not null)
        {
            if(line.StartsWith("open_time,",StringComparison.Ordinal))continue;
            var fields=line.Split(',');if(fields.Length!=12)throw new InvalidDataException("Unexpected futures H1 schema.");
            decimal N(int i)=>decimal.Parse(fields[i],NumberStyles.Float,CultureInfo.InvariantCulture);
            var open=DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(fields[0],CultureInfo.InvariantCulture));
            var close=DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(fields[6],CultureInfo.InvariantCulture)).AddMilliseconds(1);
            if(open<month||open>=month.AddMonths(1)||close-open!=TimeSpan.FromHours(1)||close>received||
                open.ToUnixTimeMilliseconds()%3600000!=0||N(3)<=0||N(2)<N(3)||N(1)<N(3)||N(1)>N(2)||N(4)<N(3)||N(4)>N(2)||
                N(7)<0||N(10)<0||N(10)>N(7)||rows.Count>0&&rows[^1].OpenAt>=open)
                throw new InvalidDataException("Invalid candle times, price or volume.");
            rows.Add(new(BinanceIdentity.AssetId(symbol),open,close,received,N(1),N(2),N(3),N(4),N(7),N(10)));
        }
        if(rows.Count==0)throw new InvalidDataException("Empty historical archive.");
        return rows;
    }
}
