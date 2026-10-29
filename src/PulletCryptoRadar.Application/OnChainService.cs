using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Application;

public sealed class OnChainOptions
{
    public bool Enabled {get;set;}=true;
    public int WatchMinutes {get;set;}=30;
    public int CandidateHours {get;set;}=6;
    public int MaximumTargets {get;set;}=5;
    public int HourRequestBudget {get;set;}=50;
    public int DayRequestBudget {get;set;}=200;
    public int WeekRequestBudget {get;set;}=1000;
    public bool SeedAriaReference {get;set;}=true;
    public bool IsValid()=>WatchMinutes is >=30 and <=60&&CandidateHours is >=6 and <=48&&MaximumTargets is >=1 and <=25
        &&HourRequestBudget is >=3 and <=100&&DayRequestBudget is >=3 and <=500&&WeekRequestBudget is >=3 and <=2000;
}
public sealed class OnChainService(IOnChainRepository repository,IOnChainPersistence persistence,IMarketRuntimeStore market,
    ICapitalRuntimeStore capital,OnChainFeatureEngine engine,TimeProvider time):IOnChainFeatureSource
{
    private readonly SemaphoreSlim _gate=new(1,1);private readonly object _cacheGate=new();
    private OnChainState _state=new([],[],[],[]);private bool _restored;
    private static string Platform(string chain)=>chain switch{"bsc"=>"binance-smart-chain","ethereum"=>"ethereum","base"=>"base","arbitrum"=>"arbitrum-one","polygon"=>"polygon-pos",_=>throw new ArgumentException("不支持的链")};
    private static bool Address(string value)=>System.Text.RegularExpressions.Regex.IsMatch(value??"",@"\A0x[0-9a-fA-F]{40}\z");
    public async Task RestoreAsync(CancellationToken ct)
    {await _gate.WaitAsync(ct);try{if(_restored)return;var state=repository.Restore();lock(_cacheGate)_state=state;_restored=true;}finally{_gate.Release();}}
    public IReadOnlyList<ChainBinding> Bindings(){lock(_cacheGate)return _state.Bindings;}
    private bool Identity(ChainBinding b)
    {
        var coin=capital.Catalog?.Coins.FirstOrDefault(c=>c.Id==b.CoinGeckoId);
        return b.Verified&&b.Enabled&&coin?.Platforms.TryGetValue(Platform(b.Chain),out var address)==true&&address.Equals(b.ContractAddress,StringComparison.OrdinalIgnoreCase);
    }
    public OnChainFeatures Features(int assetId,DateTimeOffset now)
    {
        lock(_cacheGate)
        {
            var b=_state.Bindings.FirstOrDefault(b=>b.AssetId==assetId&&b.Enabled);if(b is null)return new();
            var snapshots=_state.Samples.Where(s=>s.Chain==b.Chain&&s.ContractAddress.Equals(b.ContractAddress,StringComparison.OrdinalIgnoreCase)).OrderByDescending(s=>s.SourceTime).ThenByDescending(s=>s.ReceivedTime).ToArray();
            var latest=snapshots.FirstOrDefault();var prior=latest is null?null:snapshots.Where(s=>s.Provider==latest.Provider&&s.SourceTime<=latest.SourceTime.AddHours(-23)
                &&s.SourceTime>=latest.SourceTime.AddHours(-25.5)).MinBy(s=>Math.Abs((s.SourceTime-latest.SourceTime.AddHours(-24)).TotalSeconds));
            return engine.Calculate(b,latest,_state.Labels,prior,Identity(b),now);
        }
    }
    public IReadOnlyList<OnChainView> Overview()
    {
        lock(_cacheGate)return _state.Bindings.Select(b=>new OnChainView(b,Features(b.AssetId,time.GetUtcNow()),_state.Samples.Where(s=>s.Chain==b.Chain&&s.ContractAddress.Equals(b.ContractAddress,StringComparison.OrdinalIgnoreCase)).MaxBy(s=>s.SourceTime),
            _state.Attempts.FirstOrDefault(a=>a.Chain==b.Chain&&a.ContractAddress==b.ContractAddress))).ToArray();
    }
    public IReadOnlyList<OnChainSample> History(int assetId,int limit)
    {var b=Bindings().FirstOrDefault(b=>b.AssetId==assetId);return b is null?[]:repository.History(b.Chain,b.ContractAddress,Math.Clamp(limit,1,200));}
    public async Task<ChainBinding> SaveBindingAsync(int assetId,ChainBindingRequest request,CancellationToken ct)
    {
        await RestoreAsync(ct);await _gate.WaitAsync(ct);
        try
        {
            if(market.Get(assetId) is null)throw new KeyNotFoundException("资产不存在");
            if(!request.ConfirmIdentity||!Address(request.ContractAddress))throw new ArgumentException("须核对完整 EVM 合约身份；本功能不按符号猜测地址");
            var platform=Platform(request.Chain);var coin=capital.Catalog?.Coins.FirstOrDefault(c=>c.Id==request.CoinGeckoId);
            if(coin is null||!coin.Platforms.TryGetValue(platform,out var address)||!address.Equals(request.ContractAddress,StringComparison.OrdinalIgnoreCase))throw new ArgumentException("链与地址必须匹配所选 CoinGecko ID 的当前目录");
            var b=new ChainBinding(assetId,request.Chain,request.ContractAddress.ToLowerInvariant(),request.CoinGeckoId,request.Enabled,true,request.ExpectedRevision+1,time.GetUtcNow(),"Manual confirmation",
                [$"https://www.coingecko.com/en/coins/{Uri.EscapeDataString(request.CoinGeckoId)}"]);
            await persistence.SaveBindingAsync(b,request.ExpectedRevision,ct);lock(_cacheGate)_state=_state with{Bindings=_state.Bindings.Where(x=>x.AssetId!=assetId).Append(b).ToArray()};return b;
        }
        finally{_gate.Release();}
    }
    public async Task<AddressLabel> SaveLabelAsync(AddressLabelRequest request,CancellationToken ct)
    {
        await RestoreAsync(ct);await _gate.WaitAsync(ct);
        try
        {
            _=Platform(request.Chain);if(!Address(request.Address)||!Enum.IsDefined(request.Type))throw new ArgumentException("地址或标签类型无效");
            if(request.Type!=WalletLabelType.Unknown&&(!request.ConfirmEvidence||!Uri.TryCreate(request.EvidenceUrl,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.UserInfo.Length>0||request.EvidenceUrl.Length>1000))
                throw new ArgumentException("非 Unknown 标签须确认依据并提供 HTTPS 证据链接");
            var label=new AddressLabel(request.Chain,request.Address.ToLowerInvariant(),request.Type,request.Type!=WalletLabelType.Unknown,request.EvidenceUrl??"",request.ExpectedRevision+1,time.GetUtcNow());
            await persistence.SaveLabelAsync(label,request.ExpectedRevision,ct);lock(_cacheGate)_state=_state with{Labels=_state.Labels.Where(l=>l.Chain!=label.Chain||l.Address!=label.Address).Append(label).ToArray()};return label;
        }
        finally{_gate.Release();}
    }
    public async Task SeedAsync(CancellationToken ct)
    {
        await RestoreAsync(ct);await _gate.WaitAsync(ct);
        try
        {
            var asset=market.GetAll().FirstOrDefault(m=>m.Instrument.Symbol=="ARIAUSDT");
            const string address="0x5d3a12c42e5372b2cc3264ab3cdcf660a1555238";
            if(asset is null||Bindings().Any(b=>b.AssetId==asset.Instrument.AssetId))return;
            var coin=capital.Catalog?.Coins.FirstOrDefault(c=>c.Id=="aria-ai");
            if(coin?.Platforms.TryGetValue("binance-smart-chain",out var found)!=true||!address.Equals(found,StringComparison.OrdinalIgnoreCase))return;
            var binding=new ChainBinding(asset.Instrument.AssetId,"bsc",address,"aria-ai",true,true,1,time.GetUtcNow(),"Reference cross-check: CoinGecko catalog + Binance Wallet token reference; project documentation confirms BNB Chain",
                ["https://www.coingecko.com/en/coins/aria-ai","https://web3.binance.com/en/token/bsc/"+address,"https://docs.playariagame.com/tokenomics/token-details"]);
            await persistence.SaveBindingAsync(binding,0,ct);lock(_cacheGate)_state=_state with{Bindings=_state.Bindings.Append(binding).ToArray()};
        }
        finally{_gate.Release();}
    }
    public bool Due(ChainBinding b,DateTimeOffset now){lock(_cacheGate)return _state.Attempts.FirstOrDefault(a=>a.Chain==b.Chain&&a.ContractAddress==b.ContractAddress)?.NextAttempt<=now||!_state.Attempts.Any(a=>a.Chain==b.Chain&&a.ContractAddress==b.ContractAddress);}
    public async Task<bool> ReserveAsync(ChainAttempt attempt,OnChainOptions options,CancellationToken ct)
    {
        var reserved=await persistence.ReserveAsync(new(attempt,3,options.HourRequestBudget,options.DayRequestBudget,options.WeekRequestBudget),ct);
        if(reserved)lock(_cacheGate)_state=_state with{Attempts=_state.Attempts.Where(a=>a.Chain!=attempt.Chain||a.ContractAddress!=attempt.ContractAddress).Append(attempt).ToArray()};return reserved;
    }
    public async Task SaveSampleAsync(OnChainWrite write,CancellationToken ct)
    {
        await persistence.SaveSampleAsync(write,ct);lock(_cacheGate)
        {
            var samples=write.Sample is {} s?_state.Samples.Where(x=>x.Chain!=s.Chain||x.ContractAddress!=s.ContractAddress||x.Provider!=s.Provider||x.SourceTime!=s.SourceTime).Append(s).ToArray():_state.Samples;
            _state=_state with{Samples=samples.GroupBy(s=>(s.Chain,s.ContractAddress,s.Provider)).SelectMany(g=>g.OrderByDescending(s=>s.SourceTime).Take(256)).ToArray(),Attempts=_state.Attempts.Where(a=>a.Chain!=write.Attempt.Chain||a.ContractAddress!=write.Attempt.ContractAddress).Append(write.Attempt).ToArray()};
        }
    }
}
