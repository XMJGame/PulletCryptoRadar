using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Infrastructure;
public sealed class ExecutionEvidenceRepository(IOptions<StorageOptions> options):IExecutionEvidenceRepository
{
    private readonly object gate=new();
    private string Root=>Path.GetFullPath(Path.Combine(options.Value.Directory,"ExecutionEvidence"));
    private string SymbolFile(string symbol)=>"funding-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(symbol)))+".json";
    private T? Read<T>(string name)
    {
        var path=Path.Combine(Root,name);if(!File.Exists(path))return default;
        using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        if(file.Length>32*1024*1024)throw new InvalidDataException("Execution evidence size limit exceeded");
        return JsonSerializer.Deserialize<T>(file)??throw new InvalidDataException("Execution evidence is null");
    }
    private void Write<T>(string name,T value)
    {
        Directory.CreateDirectory(Root);var path=Path.Combine(Root,name);var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{using(var f=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){JsonSerializer.Serialize(f,value);f.Flush(true);}File.Move(temp,path,true);}
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
    public ContractRulesSnapshot? Rules(){lock(gate)return Read<ContractRulesSnapshot>("rules.json");}
    public void SaveRules(ContractRulesSnapshot snapshot){lock(gate)Write("rules.json",snapshot);}
    public FundingIntervalSnapshot? Intervals(){lock(gate)return Read<FundingIntervalSnapshot>("intervals.json");}
    public void SaveIntervals(FundingIntervalSnapshot snapshot){lock(gate)Write("intervals.json",snapshot);}
    public FundingRateWindow? Funding(string symbol){lock(gate)return Read<FundingRateWindow>(SymbolFile(symbol));}
    public void SaveFunding(FundingRateWindow window){lock(gate)Write(SymbolFile(window.Symbol),window);}
    private static (string,Guid,string,DateTimeOffset,string) Key(FundingAuditPayment p)=>(p.Version,p.PositionId,p.Symbol,p.FundingAt,p.RateType);
    public int AddFundingPayments(IReadOnlyList<FundingAuditPayment> payments)
    {
        lock(gate)
        {
            var old=Read<FundingAuditPayment[]>("funding-audit.json")??[];var all=new Dictionary<(string,Guid,string,DateTimeOffset,string),FundingAuditPayment>();
            if(old.Select(Key).Distinct().Count()!=old.Length)throw new InvalidDataException("Stored funding audit has duplicate keys");
            foreach(var p in old.Concat(payments))
            {
                var expected=p.Quantity*p.MarkPrice*p.Rate*(p.Direction=="Short"?-1:1);
                if(p.Version!=PulletCryptoRadar.Application.ExecutionFundingEngine.Version||p.PositionId==Guid.Empty||p.Direction is not ("Long" or "Short")||p.Quantity<=0
                    ||p.MarkPrice<=0||p.ObservedAt<p.FundingAt||p.SignedCost!=expected||p.RateType is not ("Regular" or "Special"))throw new InvalidDataException("Funding audit conservation or identity invalid");
                if(all.TryGetValue(Key(p),out var prior))
                {
                    if(prior.Direction!=p.Direction||prior.Quantity!=p.Quantity||prior.Rate!=p.Rate||prior.MarkPrice!=p.MarkPrice||prior.SignedCost!=p.SignedCost)
                        throw new InvalidDataException("Funding audit conflict; immutable earlier evidence retained");
                }
                else all.Add(Key(p),p);
            }
            if(all.Count>50000)throw new InvalidDataException("Funding audit size limit reached; archive before adding");
            if(all.Count!=old.Length)Write("funding-audit.json",all.Values.OrderBy(p=>p.FundingAt).ThenBy(p=>p.PositionId).ToArray());
            return all.Count-old.Length;
        }
    }
    public int FundingPaymentCount(){lock(gate)return (Read<FundingAuditPayment[]>("funding-audit.json")??[]).Length;}
    public void SaveQuote(ExecutionQuote quote)
    {
        lock(gate)
        {
            // A diagnostic snapshot, never a fill or account mutation. One immutable file per symbol/side/15-min bucket.
            var name="quote-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(quote.Symbol+":"+quote.Side)))+"-"+quote.AsOf.ToUnixTimeSeconds()/900+".json";
            var directory=Path.Combine(Root,"Quotes");Directory.CreateDirectory(directory);
            if(!File.Exists(Path.Combine(directory,name)))Write(Path.Combine("Quotes",name),quote);
        }
    }
}
