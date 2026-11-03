using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;
namespace PulletCryptoRadar.Infrastructure;
public sealed class QuantShadowRepository(IOptions<StorageOptions> options):IQuantShadowRepository
{
    private readonly object gate=new();
    private string Root(string hash)
    {if(hash.Length!=64||!hash.All(Uri.IsHexDigit))throw new InvalidDataException("Invalid quant rules hash");return Path.GetFullPath(Path.Combine(options.Value.Directory,"QuantShadow",hash));}
    private T? Read<T>(string path,bool zipped=false)
    {
        if(!File.Exists(path))return default;using var f=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        if(f.Length>8*1024*1024)throw new InvalidDataException("Quant file too large");
        using var gz=zipped?new GZipStream(f,CompressionMode.Decompress,true):null;using var buffer=new MemoryStream();var input=(Stream?)gz??f;var block=new byte[8192];int read;
        while((read=input.Read(block))>0){buffer.Write(block,0,read);if(buffer.Length>16*1024*1024)throw new InvalidDataException("Quant expanded file too large");}
        return JsonSerializer.Deserialize<T>(buffer.ToArray())??throw new InvalidDataException("Quant evidence is null");
    }
    private bool Write<T>(string path,T value,bool immutable=false,bool zipped=false)
    {
        if(immutable&&File.Exists(path))return false;Directory.CreateDirectory(Path.GetDirectoryName(path)!);var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var f=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {if(zipped){using(var gz=new GZipStream(f,CompressionLevel.Optimal,true))JsonSerializer.Serialize(gz,value);}else JsonSerializer.Serialize(f,value);f.Flush(true);}
            File.Move(temp,path,!immutable);return true;
        }
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
    public QuantCohort OpenCohort(string hash,QuantParameters parameters,DateTimeOffset now)
    {
        lock(gate)
        {
            if(QuantRules.Hash(parameters)!=hash)throw new InvalidDataException("Quant parameter hash mismatch");var path=Path.Combine(Root(hash),"cohort.json");
            if(Read<QuantCohort>(path) is {} old)
            {if(old.RulesHash!=hash||QuantRules.Hash(old.Parameters)!=hash||old.StartedAt>now||old.Id==Guid.Empty)throw new InvalidDataException("Invalid quant cohort");return old;}
            var cohort=new QuantCohort(Guid.NewGuid(),now,parameters.Version,hash,parameters);Write(path,cohort,true);return cohort;
        }
    }
    private string HourPath(string hash,DateTimeOffset hour)=>Path.Combine(Root(hash),"Forward",hour.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
    public QuantUniverse? Universe(string hash,DateTimeOffset hour){lock(gate)return Read<QuantUniverse>(Path.Combine(HourPath(hash,hour),"universe.json"));}
    public QuantUniverse FreezeUniverse(string hash,QuantUniverse universe)
    {lock(gate){var path=Path.Combine(HourPath(hash,universe.Hour),"universe.json");Write(path,universe,true);return Read<QuantUniverse>(path)!;}}
    public string SaveInput(string hash,QuantInputBundle input)
    {
        lock(gate)
        {
            var digest=QuantRules.HashValue(input);var path=Path.Combine(Root(hash),"Inputs",digest+".json.gz");
            if(!Write(path,input,true,true)&&QuantRules.HashValue(Read<QuantInputBundle>(path,true))!=digest)throw new InvalidDataException("Quant input integrity mismatch");return digest;
        }
    }
    public QuantInputBundle? ReadInput(string hash,string inputHash)
    {
        if(inputHash.Length!=64||!inputHash.All(Uri.IsHexDigit))throw new InvalidDataException("Invalid quant input hash");
        lock(gate){var result=Read<QuantInputBundle>(Path.Combine(Root(hash),"Inputs",inputHash+".json.gz"),true);if(result is not null&&QuantRules.HashValue(result)!=inputHash)throw new InvalidDataException("Quant input integrity mismatch");return result;}
    }
    public bool SaveDecision(QuantDecisionBatch batch)
    {
        lock(gate)
        {
            var cohort=Read<QuantCohort>(Path.Combine(Root(batch.RulesHash),"cohort.json"))??throw new InvalidDataException("Missing cohort");
            if(batch.CohortId!=cohort.Id||batch.Mode!="Forward"||batch.Hour<cohort.StartedAt||batch.CapturedAt<batch.Hour||batch.CapturedAt>=batch.Hour.AddMinutes(cohort.Parameters.DecisionWindowMinutes)
                ||batch.Decisions.Count!=4||batch.Decisions.Any(d=>d.Hour!=batch.Hour||d.Mode!="Forward"||!d.InputArchived||d.InputHash!=batch.InputHash))throw new InvalidDataException("Invalid forward quant decision");
            return Write(Path.Combine(HourPath(batch.RulesHash,batch.Hour),"decision-"+QuantRules.HashValue(batch.Symbol)+".json.gz"),batch,true,true);
        }
    }
    public IReadOnlyList<QuantDecisionBatch> Decisions(string hash,DateTimeOffset hour)
    {lock(gate){var dir=HourPath(hash,hour);return !Directory.Exists(dir)?[]:Directory.GetFiles(dir,"decision-*.json.gz").Select(p=>Read<QuantDecisionBatch>(p,true)!).ToArray();}}
    public IReadOnlyList<QuantHistoryHour> History(string hash,int hours)
    {
        lock(gate)
        {
            var dir=Path.Combine(Root(hash),"Forward");if(!Directory.Exists(dir))return [];
            return Directory.GetDirectories(dir).OrderDescending(StringComparer.Ordinal).Take(Math.Clamp(hours,1,168)).Select(d=>
            {
                var rows=Directory.GetFiles(d,"decision-*.json.gz").Select(p=>Read<QuantDecisionBatch>(p,true)!).ToArray();var hour=DateTimeOffset.FromUnixTimeSeconds(long.Parse(Path.GetFileName(d),System.Globalization.CultureInfo.InvariantCulture));
                return new QuantHistoryHour(hour,rows.Length,rows.Sum(r=>r.Decisions.Count),rows.Sum(r=>r.Decisions.Count(x=>x.Status=="PriceCandidate")),rows.Sum(r=>r.Decisions.Count(x=>x.Status=="Insufficient")));
            }).ToArray();
        }
    }
    public void SaveAssessment(string hash,QuantAssessment assessment)
    {
        if(assessment.Mode!="Forward")return;lock(gate)Write(Path.Combine(Root(hash),"Assessments",QuantRules.HashValue(assessment.DecisionId)+"-"+assessment.AsOf.ToUnixTimeSeconds()/10+".json.gz"),assessment,true,true);
    }
    public void SaveLatest(QuantSnapshot snapshot){lock(gate)Write(Path.Combine(Root(snapshot.RulesHash),"latest.json.gz"),snapshot,zipped:true);}
    public QuantSnapshot? Latest(string hash){lock(gate)return Read<QuantSnapshot>(Path.Combine(Root(hash),"latest.json.gz"),true);}
    public IReadOnlyList<QuantAssessment> ReadAssessments(string hash,DateTimeOffset from,DateTimeOffset through)
    {
        lock(gate)
        {
            var dir=Path.Combine(Root(hash),"Assessments");if(!Directory.Exists(dir))return [];
            var first=from.ToUnixTimeSeconds()/10;var last=through.ToUnixTimeSeconds()/10;
            return Directory.EnumerateFiles(dir,"*.json.gz").Where(path=>
            {
                var name=Path.GetFileName(path);var split=name.LastIndexOf('-');
                return split>=0&&long.TryParse(name.AsSpan(split+1,name.Length-split-9),out var bucket)&&bucket>=first&&bucket<=last;
            }).Select(path=>Read<QuantAssessment>(path,true)!).Where(a=>a.AsOf>=from&&a.AsOf<=through).ToArray();
        }
    }
}
