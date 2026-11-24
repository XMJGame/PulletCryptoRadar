using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Infrastructure;

public sealed partial class BinanceHistoricalArchive
{
    public async Task<(ArchiveProof Proof,string Csv)> DerivativeAsync(string symbol,string kind,DateTimeOffset period,CancellationToken token)
    {
        if(!System.Text.RegularExpressions.Regex.IsMatch(symbol,@"\A[A-Z0-9]{1,30}USDT\z")||period.Offset!=TimeSpan.Zero||period.TimeOfDay!=TimeSpan.Zero||
            period>=clock.GetUtcNow()||kind is not("metrics" or "fundingRate" or "markPriceKlines")||kind!="metrics"&&period.Day!=1)
            throw new ArgumentException("Invalid derivative archive selection.");
        var stamp=period.ToString(kind=="metrics"?"yyyy-MM-dd":"yyyy-MM",CultureInfo.InvariantCulture);
        var name=kind=="markPriceKlines"?$"{symbol}-1h-{stamp}.zip":$"{symbol}-{kind}-{stamp}.zip";
        var url=$"https://data.binance.vision/data/futures/um/{(kind=="metrics"?"daily":"monthly")}/{kind}/{symbol}/{(kind=="markPriceKlines"?"1h/":"")}{name}";
        var folder=Path.Combine(directory,kind);Directory.CreateDirectory(folder);var path=Path.Combine(folder,name);var manifest=path+".json";
        byte[] bytes;ArchiveProof proof;
        if(File.Exists(path)&&File.Exists(manifest))
        {
            proof=JsonSerializer.Deserialize<ArchiveProof>(await File.ReadAllTextAsync(manifest,token))!;
            bytes=await File.ReadAllBytesAsync(path,token);
            if(proof.Url!=url||proof.Symbol!=symbol||proof.Kind!=kind||proof.Period!=stamp||proof.DownloadedAt>clock.GetUtcNow()||
                !proof.Sha256.Equals(Convert.ToHexString(SHA256.HashData(bytes)),StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Derivative archive cache identity/checksum mismatch.");
        }
        else
        {
            var checksum=Encoding.UTF8.GetString(await BudgetedDownload(url+".CHECKSUM",4096,token));
            var parts=checksum.Trim().Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);
            if(parts.Length!=2||parts[1].TrimStart('*')!=name||!System.Text.RegularExpressions.Regex.IsMatch(parts[0],@"\A[0-9a-fA-F]{64}\z"))
                throw new InvalidDataException("Derivative official checksum identity invalid.");
            bytes=await BudgetedDownload(url,1_000_000,token);var hash=Convert.ToHexString(SHA256.HashData(bytes));
            if(!hash.Equals(parts[0],StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Derivative archive SHA256 mismatch.");
            proof=new(symbol,kind,stamp,url,hash,bytes.Length,clock.GetUtcNow());
            // Validate container before committing the cache. Content schema is checked by the caller as well.
            _=Unzip(name,bytes);
            await File.WriteAllBytesAsync(path,bytes,token);await File.WriteAllTextAsync(manifest,JsonSerializer.Serialize(proof),token);
        }
        return(proof,Unzip(name,bytes));
    }
    private async Task<byte[]> BudgetedDownload(string url,int limit,CancellationToken token)
    {
        // Retain the ledger's hard limits. Wait only for a minute window; long provider/global cooldown remains explicit failure.
        for(var attempt=0;attempt<3;attempt++)
        {
            try{return await Download(url,limit,token);}
            catch(InvalidOperationException error) when(error.Message.StartsWith("Archive budget: ProviderMinuteLimit;",StringComparison.Ordinal)&&attempt<2)
            {await Task.Delay(TimeSpan.FromSeconds(31),token);}
        }
        throw new InvalidOperationException("Archive request could not be reserved.");
    }
    private static string Unzip(string name,byte[] bytes)
    {
        using var memory=new MemoryStream(bytes);using var zip=new ZipArchive(memory,ZipArchiveMode.Read);
        if(zip.Entries.Count!=1||zip.Entries[0].Name!=Path.ChangeExtension(name,".csv")||zip.Entries[0].Length>4_000_000)
            throw new InvalidDataException("Unexpected derivative archive contents.");
        using var reader=new StreamReader(zip.Entries[0].Open());return reader.ReadToEnd();
    }
    public static IReadOnlyList<OpenInterestPoint> ParseMetrics(string symbol,DateTimeOffset day,string csv,DateTimeOffset received)
    {
        using var reader=new StringReader(csv);
        var header=reader.ReadLine()?.Split(',')??[];
        var time=Array.IndexOf(header,"create_time");var ticker=Array.IndexOf(header,"symbol");
        var quantity=Array.IndexOf(header,"sum_open_interest");var notional=Array.IndexOf(header,"sum_open_interest_value");
        if(time<0||ticker<0||quantity<0||notional<0)throw new InvalidDataException("OI archive header is incomplete.");
        var result=new List<OpenInterestPoint>();string? line;
        while((line=reader.ReadLine()) is not null)
        {
            var fields=line.Split(',');if(fields.Length!=header.Length)throw new InvalidDataException("OI schema mismatch.");
            var at=DateTimeOffset.ParseExact(fields[time],"yyyy-MM-dd HH:mm:ss",CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal|DateTimeStyles.AdjustToUniversal);
            var q=decimal.Parse(fields[quantity],NumberStyles.Float,CultureInfo.InvariantCulture);var n=decimal.Parse(fields[notional],NumberStyles.Float,CultureInfo.InvariantCulture);
            if(fields[ticker]!=symbol||at<day||at>=day.AddDays(1)||at>received||at.ToUnixTimeMilliseconds()%300000!=0||q<0||n<0||
                result.Count>0&&result[^1].SourceTime>=at)throw new InvalidDataException("OI identity/time/value invalid.");
            result.Add(new(BinanceIdentity.AssetId(symbol),at,received,q,n));
        }
        if(result.Count==0)throw new InvalidDataException("Empty OI archive.");
        return result;
    }
    public static IReadOnlyList<ArchivedFunding> ParseFunding(DateTimeOffset month,string csv,DateTimeOffset received)
    {
        using var reader=new StringReader(csv);if(reader.ReadLine()!="calc_time,funding_interval_hours,last_funding_rate")
            throw new InvalidDataException("Funding archive header mismatch.");
        var result=new List<ArchivedFunding>();string? line;
        while((line=reader.ReadLine()) is not null)
        {
            var fields=line.Split(',');if(fields.Length!=3)throw new InvalidDataException("Funding schema mismatch.");
            var at=DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(fields[0],CultureInfo.InvariantCulture));
            var hours=int.Parse(fields[1],CultureInfo.InvariantCulture);var rate=decimal.Parse(fields[2],NumberStyles.Float,CultureInfo.InvariantCulture);
            if(at<month||at>=month.AddMonths(1)||at>received||hours is <1 or >24||Math.Abs(rate)>1||result.Count>0&&result[^1].At>=at)
                throw new InvalidDataException("Funding time/rate invalid.");
            result.Add(new(at,hours,rate));
        }
        if(result.Count==0)throw new InvalidDataException("Empty funding archive.");return result;
    }
    public static IReadOnlyList<HourCandle> ParseMark(string symbol,DateTimeOffset month,string csv,DateTimeOffset received)
    {
        var name=$"{symbol}-1h-{month:yyyy-MM}.csv";
        using var memory=new MemoryStream();
        using(var zip=new ZipArchive(memory,ZipArchiveMode.Create,true))
        {using var writer=new StreamWriter(zip.CreateEntry(name).Open());writer.Write(csv);}
        return Parse(symbol,month,memory.ToArray(),received);
    }
}
