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
    private static string Platform(string chain)=>ChainIdentity.Platform(chain);
    public async Task RestoreAsync(CancellationToken ct)
    {await _gate.WaitAsync(ct);try{if(_restored)return;var state=repository.Restore();lock(_cacheGate)_state=state;_restored=true;}finally{_gate.Release();}}
    public IReadOnlyList<ChainBinding> Bindings(){lock(_cacheGate)return _state.Bindings;}
    public IReadOnlyList<AddressLabel> Labels(){lock(_cacheGate)return _state.Labels;}
    public static IReadOnlyList<ChainBinding> DueBatch(IReadOnlyList<ChainBinding> bindings,IReadOnlyList<ChainAttempt> attempts,
        IReadOnlySet<int> watchIds,DateTimeOffset now,int limit)
    {
        var schedule=attempts.ToDictionary(a=>(a.Chain,ChainIdentity.Normalize(a.Chain,a.ContractAddress)));
        DateTimeOffset DueAt(ChainBinding b)=>schedule.TryGetValue((b.Chain,ChainIdentity.Normalize(b.Chain,b.ContractAddress)),out var a)?a.NextAttempt:DateTimeOffset.MinValue;
        return bindings.Where(b=>b.Enabled&&DueAt(b)<=now).OrderBy(DueAt).ThenByDescending(b=>watchIds.Contains(b.AssetId))
            .ThenBy(b=>b.AssetId).Take(Math.Max(0,limit)).ToArray();
    }
    public IReadOnlyList<ChainBinding> DueBindings(IReadOnlySet<int> watchIds,DateTimeOffset now,int limit)
    {lock(_cacheGate)return DueBatch(_state.Bindings,_state.Attempts,watchIds,now,limit);}
    private bool Identity(ChainBinding b)
    {
        var coin=capital.Catalog?.Coins.FirstOrDefault(c=>c.Id==b.CoinGeckoId);
        return b.Verified&&b.Enabled&&coin?.Platforms.TryGetValue(Platform(b.Chain),out var address)==true&&ChainIdentity.Same(b.Chain,address,b.ContractAddress);
    }
    public OnChainFeatures Features(int assetId,DateTimeOffset now)
    {
        lock(_cacheGate)
        {
            var b=_state.Bindings.FirstOrDefault(b=>b.AssetId==assetId&&b.Enabled);if(b is null)return new();
            var snapshots=_state.Samples.Where(s=>s.Chain==b.Chain&&ChainIdentity.Same(b.Chain,s.ContractAddress,b.ContractAddress)).OrderByDescending(s=>s.SourceTime).ThenByDescending(s=>s.ReceivedTime).ToArray();
            var latest=snapshots.FirstOrDefault();var prior=latest is null?null:snapshots.Where(s=>s.Provider==latest.Provider&&s.SourceTime<=latest.SourceTime.AddHours(-23)
                &&s.SourceTime>=latest.SourceTime.AddHours(-25.5)).MinBy(s=>Math.Abs((s.SourceTime-latest.SourceTime.AddHours(-24)).TotalSeconds));
            return engine.Calculate(b,latest,_state.Labels,prior,Identity(b),now);
        }
    }
    public IReadOnlyList<OnChainView> Overview()
    {
        lock(_cacheGate)return _state.Bindings.Select(b=>new OnChainView(b,Features(b.AssetId,time.GetUtcNow()),_state.Samples.Where(s=>s.Chain==b.Chain&&ChainIdentity.Same(b.Chain,s.ContractAddress,b.ContractAddress)).OrderByDescending(s=>s.SourceTime).ThenByDescending(s=>s.ReceivedTime).FirstOrDefault(),
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
            if(!request.ConfirmIdentity||!ChainIdentity.Address(request.Chain,request.ContractAddress))throw new ArgumentException("须核对完整链与合约 / Mint 身份；本功能不按符号猜测地址");
            var platform=Platform(request.Chain);var coin=capital.Catalog?.Coins.FirstOrDefault(c=>c.Id==request.CoinGeckoId);
            if(coin is null||!coin.Platforms.TryGetValue(platform,out var address)||!ChainIdentity.Same(request.Chain,address,request.ContractAddress))throw new ArgumentException("链与地址必须匹配所选 CoinGecko ID 的当前目录");
            ChainBinding? previous;lock(_cacheGate)previous=_state.Bindings.FirstOrDefault(b=>b.AssetId==assetId&&b.Chain==request.Chain
                &&b.CoinGeckoId==request.CoinGeckoId&&ChainIdentity.Same(request.Chain,b.ContractAddress,request.ContractAddress));
            var evidence=request.EvidenceUrls??previous?.EvidenceUrls??[];
            if(evidence.Count>5||evidence.Any(url=>url is null||url.Length>1000||!Uri.TryCreate(url,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.UserInfo.Length>0))
                throw new ArgumentException("身份依据最多五条，须为无凭证的 HTTPS 链接");
            var b=new ChainBinding(assetId,request.Chain,ChainIdentity.Normalize(request.Chain,request.ContractAddress),request.CoinGeckoId,request.Enabled,true,request.ExpectedRevision+1,time.GetUtcNow(),"Manual confirmation",
                evidence.Prepend($"https://www.coingecko.com/en/coins/{Uri.EscapeDataString(request.CoinGeckoId)}").Distinct(StringComparer.Ordinal).ToArray());
            await persistence.SaveBindingAsync(b,request.ExpectedRevision,ct);lock(_cacheGate)_state=_state with{Bindings=_state.Bindings.Where(x=>x.AssetId!=assetId).Append(b).ToArray()};return b;
        }
        finally{_gate.Release();}
    }
    public async Task<AddressLabel> SaveLabelAsync(AddressLabelRequest request,CancellationToken ct)
    {
        await RestoreAsync(ct);await _gate.WaitAsync(ct);
        try
        {
            _=Platform(request.Chain);if(!ChainIdentity.Address(request.Chain,request.Address)||!Enum.IsDefined(request.Type))throw new ArgumentException("地址或标签类型无效");
            if(request.Type!=WalletLabelType.Unknown&&(!request.ConfirmEvidence||!Uri.TryCreate(request.EvidenceUrl,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.UserInfo.Length>0||request.EvidenceUrl.Length>1000))
                throw new ArgumentException("非 Unknown 标签须确认依据并提供 HTTPS 证据链接");
            var label=new AddressLabel(request.Chain,ChainIdentity.Normalize(request.Chain,request.Address),request.Type,request.Type!=WalletLabelType.Unknown,request.EvidenceUrl??"",request.ExpectedRevision+1,time.GetUtcNow());
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
    public static ChainAttempt PromoteWatchAttempt(ChainAttempt attempt,int minutes)=>attempt.Error is null&&attempt.NextAttempt>attempt.LastAttempt.AddMinutes(minutes)
        ?attempt with{NextAttempt=attempt.LastAttempt.AddMinutes(minutes)}:attempt;
    public async Task PromoteWatchAsync(ChainBinding binding,int minutes,CancellationToken ct)
    {
        ChainAttempt? old;lock(_cacheGate)old=_state.Attempts.FirstOrDefault(a=>a.Chain==binding.Chain&&a.ContractAddress==binding.ContractAddress);
        if(old is null)return;var next=PromoteWatchAttempt(old,minutes);
        if(!ReferenceEquals(old,next))await SaveSampleAsync(new(null,next),ct);
    }
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
