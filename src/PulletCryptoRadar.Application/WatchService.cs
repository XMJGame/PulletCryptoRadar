using System.Text.Json;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Scanner;

namespace PulletCryptoRadar.Application;

public sealed class WatchService(IMarketRuntimeStore market,IWatchRepository repository,
    IWatchPersistence persistence,WatchStrategyEngine engine,WatchOptions options,UpMoveDetector up,DownMoveDetector down,IRadarSnapshotSource radar,TimeProvider time,WatchOnChainStrategyEngine? onChain=null,IFlowFeatureSource? flows=null,WatchFlowStrategyEngine? flowEngine=null)
{
    private readonly SemaphoreSlim _gate=new(1,1);
    public IReadOnlyList<WatchView> Overview()
    {var latest=repository.Checkpoints().ToDictionary(c=>c.Result.AssetId);return repository.Configs().Select(c=>new WatchView(c,latest.TryGetValue(c.AssetId,out var p)&&p.Input.Config.Revision==c.Revision?p.Result:null)).ToArray();}
    public IReadOnlyList<WatchCheckpoint> History(int id,int limit)=>repository.History(id,Math.Clamp(limit,1,100));
    public async Task<WatchAsset> SaveAsync(int id,WatchRequest request,CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if(market.Get(id) is null)throw new KeyNotFoundException("交易所资产不存在");
            var old=repository.Configs().FirstOrDefault(c=>c.AssetId==id);
            if((old?.Revision??0)!=request.ExpectedRevision)throw new MappingConflictException("自选配置已更新，请刷新后重试");
            foreach(var value in new[]{request.CostPrice,request.TargetPrice,request.AttentionPrice,request.SupportPrice,request.ResistancePrice})
                if(value is <=0 or >1_000_000_000_000m)throw new ArgumentException("价格必须大于 0 且不超过 1 万亿美元；未配置请留空");
            if(request.SupportPrice>=request.ResistancePrice&&request.SupportPrice is not null&&request.ResistancePrice is not null)throw new ArgumentException("支撑价必须低于压力价");
            var wallets=request.Wallets??[];if(wallets.Count>20)throw new ArgumentException("每个标的最多保存 20 个钱包观察地址");
            foreach(var w in wallets)
                if(w.Role is not("project" or "team" or "investor")||w.Chain is not("bsc" or "ethereum" or "base" or "arbitrum" or "polygon")
                    ||!System.Text.RegularExpressions.Regex.IsMatch(w.Address??"",@"\A0x[0-9a-fA-F]{40}\z"))throw new ArgumentException("钱包须为支持的 EVM 链、完整地址及 project/team/investor 标签；当前只保存配置，不进行链上采集");
            if(wallets.Select(w=>(w.Chain,w.Address.ToLowerInvariant())).Distinct().Count()!=wallets.Count)throw new ArgumentException("不能重复配置相同链的钱包");
            var now=time.GetUtcNow();var config=new WatchAsset(id,request.Enabled,request.CostPrice,request.TargetPrice,request.AttentionPrice,request.SupportPrice,
                request.ResistancePrice,request.EnableAlerts,wallets.ToArray(),request.ExpectedRevision+1,old?.CreatedAt??now,now);
            await persistence.SaveConfigAsync(config,request.ExpectedRevision,ct);return config;
        }
        finally{_gate.Release();}
    }
    public async Task TickAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var configs=repository.Configs();var now=time.GetUtcNow();var assets=market.GetAll();
            if(options.SeedAriaExample&&configs.Count==0&&assets.FirstOrDefault(m=>m.Instrument.Symbol=="ARIAUSDT") is {} aria)
            {
                await persistence.SaveConfigAsync(new(aria.Instrument.AssetId,true,.40m,null,null,null,null,false,[],1,now,now,true),0,ct);
                configs=repository.Configs();
            }
            var old=repository.Checkpoints().ToDictionary(c=>c.Result.AssetId);
            var checkpoints=new List<WatchCheckpoint>();var notifications=new List<NotificationRecord>();var candles=new List<WatchCandle>();
            foreach(var config in configs)
            {
                var m=market.Get(config.AssetId);if(m is null)continue;
                var input=SpeculativeService.Binance(m,now);
                var data=new WatchInput(config,input,up.Detect(m,now),down.Detect(m,now),radar.GetSnapshot()?.Market){Flow=flows?.Features(config.AssetId,now)??new()};
                var result=flowEngine?.Evaluate(data,now)??onChain?.Evaluate(data,now)??engine.Evaluate(data,now);var next=engine.Advance(data,result,old.GetValueOrDefault(config.AssetId),now);
                checkpoints.Add(next.Checkpoint with{ParametersJson=JsonSerializer.Serialize(options)});if(next.Notification is {} n)notifications.Add(n);
                if(config.Enabled) candles.AddRange(market.GetCandles(config.AssetId,1).Where(c=>c.EndTime<=now&&c.SourceTime<=now&&c.ReceivedTime<=now).TakeLast(10).Select(c=>new WatchCandle(config.AssetId,c)));
            }
            if(checkpoints.Count>0)await persistence.SaveRunAsync(new(Guid.NewGuid(),now,checkpoints,notifications,candles,JsonSerializer.Serialize(options)),ct);
        }
        finally{_gate.Release();}
    }
    public IReadOnlyList<WatchAudit> Audit(int id,int limit)=>repository.History(id,Math.Clamp(limit,1,100)).Select(c=>
    {
        if(c.Result.Version is not(WatchStrategyEngine.Version or WatchOnChainStrategyEngine.Version or WatchFlowStrategyEngine.Version))return new WatchAudit(c.Result.AsOf,id,"UnsupportedVersion","拒绝套用其他策略版本");
        if(string.IsNullOrWhiteSpace(c.ParametersJson))return new WatchAudit(c.Result.AsOf,id,"MissingParameters","未保存历史参数");
        var settings=JsonSerializer.Deserialize<WatchOptions>(c.ParametersJson);
        if(settings is null||!settings.IsValid())return new WatchAudit(c.Result.AsOf,id,"InvalidParameters","缺少有效的历史参数");
        var original=new WatchStrategyEngine(settings);var chain=new WatchOnChainStrategyEngine(original);var result=c.Result.Version switch{WatchFlowStrategyEngine.Version=>new WatchFlowStrategyEngine(chain).Evaluate(c.Input,c.Result.AsOf),WatchOnChainStrategyEngine.Version=>chain.Evaluate(c.Input,c.Result.AsOf),_=>original.Evaluate(c.Input,c.Result.AsOf)};
        return new WatchAudit(c.Result.AsOf,id,JsonSerializer.Serialize(result)==JsonSerializer.Serialize(c.Result)?"Match":"Mismatch","使用冻结输入与参数；不读取现在的行情");
    }).ToArray();
}
