using System.Text.Json;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Scanner;

namespace PulletCryptoRadar.Application;

public sealed class SpeculativeService(IMarketRuntimeStore market,IAlternativeMarketStore alternative,ISpeculativeRepository repository,
    ISpeculativePersistence persistence,SpeculativeOptions options,SpeculativeDetector detector,CapitalFeatureEngine capitalEngine,
    ICapitalRuntimeStore capital,UpMoveDetector up,DownMoveDetector down,TimeProvider time,SpeculativeOnChainDetector? onChain=null)
{
    private readonly SemaphoreSlim _gate=new(1,1);
    private SpeculativeSnapshot? _snapshot;
    public SpeculativeSnapshot? Latest()=>Volatile.Read(ref _snapshot);
    public IReadOnlyList<VenueBinding> Bindings()=>repository.Bindings();
    public async Task SetVenueBindingAsync(int assetId,VenueBindingRequest request,CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if(request.Revoke)
            {
                var old=repository.Bindings().FirstOrDefault(b=>b.AssetId==assetId&&b.Exchange==request.Exchange)
                    ??throw new KeyNotFoundException("未保存该交易所身份");
                await persistence.BindAsync(old with{Symbol=null,Revision=request.ExpectedRevision+1,UpdatedAt=time.GetUtcNow()},request.ExpectedRevision,ct);
                return;
            }
            if(!request.ConfirmIdentity)throw new ArgumentException("必须核对交易所原生合约与本地资产身份");
            var asset=market.Get(assetId)??throw new KeyNotFoundException("资产不存在");
            var q=alternative.GetAll().FirstOrDefault(q=>q.Exchange==request.Exchange&&q.Symbol==request.Symbol&&q.BaseAsset==asset.Instrument.BaseAsset)
                ??throw new ArgumentException("只能确认当前列表中的精确基础符号候选；不自动推断倍数或别名");
            if(q.Kind!="Perpetual")throw new ArgumentException("现货仅作参考，不能替代永续合约");
            await persistence.BindAsync(new(assetId,q.Exchange,q.Symbol,request.ExpectedRevision+1,time.GetUtcNow()),request.ExpectedRevision,ct);
        }
        finally{_gate.Release();}
    }
    public async Task<SpeculativeSnapshot> ScanAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var now=time.GetUtcNow();var quotes=alternative.GetAll();var bindings=repository.Bindings();var rows=new List<SpeculativeRow>();
            foreach(var original in market.GetAll())
            {
                var m=original with{Features=original.Features with{Capital=capitalEngine.Calculate(original,capital.Get(original.Instrument.AssetId),now)}};
                var binance = Binance(m, now);
                rows.Add(new(binance,onChain?.Detect(binance,now)??detector.Detect(binance,now),true){BinanceUp=up.Detect(m,now),BinanceDown=down.Detect(m,now)});
                if (HasBinancePrice(m, now)) continue;
                foreach(var q in quotes.Where(q=>q.BaseAsset==m.Instrument.BaseAsset))
                {
                    var verified=bindings.Any(b=>b.AssetId==m.Instrument.AssetId&&b.Exchange==q.Exchange&&b.Symbol==q.Symbol);
                    var input=External(m,q,verified,now);
                    rows.Add(new(input,onChain?.Detect(input,now)??detector.Detect(input,now),false));
                }
            }
            var snapshot=new SpeculativeSnapshot(Guid.NewGuid(),now,onChain is null?SpeculativeDetector.Version:SpeculativeOnChainDetector.Version,JsonSerializer.Serialize(options),rows);
            if(rows.Count>0)await persistence.SaveAsync(snapshot,ct);
            Volatile.Write(ref _snapshot,snapshot);return snapshot;
        }
        finally{_gate.Release();}
    }
    public static bool HasBinancePrice(MarketView market, DateTimeOffset now) =>
        market.Price is > 0 && market.PriceSourceTime is { } source && market.PriceReceivedTime is { } received &&
        source <= received && received <= now && now - source <= TimeSpan.FromSeconds(30);

    public static SpeculativeInput Binance(MarketView m,DateTimeOffset now)
    {
        FeatureValue<decimal> Field(decimal? v,DateTimeOffset? source)=>new(v,v is null?DataStatus.Missing:source>now||m.PriceReceivedTime>now?DataStatus.Invalid:
            source is null||now-source>TimeSpan.FromSeconds(30)?DataStatus.Stale:DataStatus.Valid,source,"Binance USDT perpetual (USD proxy)");
        var d=m.Features.Derivatives;
        return new(m,"Binance",m.Instrument.Symbol,true,Field(m.Price,m.PriceSourceTime),Field(m.Volume24h,m.PriceSourceTime),
            m.Features.Returns.GetValueOrDefault(60)??FeatureValue<decimal>.Missing(),Field(m.ExchangeReturn24h,m.PriceSourceTime),
            // Binance contract funding intervals vary. Without verified interval metadata do not assume every market settles in 8h.
            FeatureValue<decimal>.Missing("Binance funding interval metadata unavailable; no assumed 8h normalization"),
            d.OpenInterestUsd,d.OiChanges.GetValueOrDefault(15)??FeatureValue<decimal>.Missing(),d.OiRobustZ,d.GlobalLongShare,d.TopPositionChange15m,
            m.Features.PriceRobustZ5m,m.Features.VolumeRobustZ,"Binance 原生序列；与 V2 并行，不修改原检测器或通知");
    }
    public static SpeculativeInput External(MarketView m,VenueQuote q,bool verified,DateTimeOffset now)
    {
        FeatureValue<decimal> Field(decimal? v)=>new(v,v is null?DataStatus.Missing:q.SourceTime>now||q.ReceivedTime>now?DataStatus.Invalid:
            now-q.SourceTime>TimeSpan.FromSeconds(180)?DataStatus.Stale:DataStatus.Valid,q.SourceTime,q.Exchange+" "+q.Kind+"; "+q.TimeBasis);
        return new(m,q.Exchange,q.Symbol,verified&&q.Kind=="Perpetual",Field(q.Price),Field(q.Volume24hQuote),FeatureValue<decimal>.Missing(q.Exchange+" 1h history unavailable"),
            Field(q.Return24h),Field(q.FundingRate8h),q.OpenInterestUsd,q.OiChange15m,q.OiRobustZ,q.LongShare,q.TopPositionChange15m,
            FeatureValue<double>.Missing("No cross-venue price baseline"),FeatureValue<double>.Missing("No cross-venue volume baseline"),
            q.Exchange=="Coinbase"?"Coinbase 现货参考；原生基币成交量不是美元成交额，无永续 OI，不参与永续评分":verified?"已人工核对跨交易所合约；Gate 独立序列，不与 Binance 相加":"仅基础符号精确匹配候选，跨交易所身份待确认，不使用可信市值评分");
    }
    public IReadOnlyList<SpeculativeAudit> Audit(int limit)
    {
        return repository.History(Math.Clamp(limit,1,12)).Select(s=>
        {
            if(s.StrategyVersion is not(SpeculativeDetector.Version or SpeculativeOnChainDetector.Version))return new SpeculativeAudit(s.Id,s.AsOf,"UnsupportedVersion",s.Rows.Count,0,"保留历史版本，拒绝静默套用新模型");
            var settings=JsonSerializer.Deserialize<SpeculativeOptions>(s.ParametersJson);
            if(settings is null||!settings.IsValid())return new SpeculativeAudit(s.Id,s.AsOf,"InvalidParameters",s.Rows.Count,0,"历史参数无效");
            var engine=new SpeculativeDetector(settings);var upgraded=new SpeculativeOnChainDetector(settings,engine);var mismatch=s.Rows.Count(r=>JsonSerializer.Serialize(s.StrategyVersion==SpeculativeOnChainDetector.Version?upgraded.Detect(r.Input,s.AsOf):engine.Detect(r.Input,s.AsOf))!=JsonSerializer.Serialize(r.Assessment));
            return new SpeculativeAudit(s.Id,s.AsOf,mismatch==0?"Match":"Mismatch",s.Rows.Count,mismatch,"使用保存的独立交易所输入、映射状态、时间和参数复算；不读取当前行情");
        }).ToArray();
    }
}
