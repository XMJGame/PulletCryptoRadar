using PulletCryptoRadar.Core;
namespace PulletCryptoRadar.Application;
public sealed class QuantDiagnosticService(IQuantStudyRepository repository)
{
    private readonly object gate=new();
    private Guid? cachedId;
    private readonly Dictionary<string,QuantDiagnosticReport> cached=new(StringComparer.Ordinal);
    public QuantDiagnosticReport? Read(Guid id,string window,string strategy,string policy,int cost)
    {
        lock(gate)
        {
            var key=$"{window}:{strategy}:{policy}:{cost}";
            if(cachedId==id&&cached.TryGetValue(key,out var previous))return previous;
            var report=repository.Report(id);if(report is null)return null;
            var result=QuantDiagnosticEngine.Build(report,repository.ReadTrades(id),repository.Series,window,strategy,policy,cost);
            if(cachedId!=id){cached.Clear();cachedId=id;}
            cached[key]=result;return result;
        }
    }
}
