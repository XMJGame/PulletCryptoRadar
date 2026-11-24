using PulletCryptoRadar.Core;
using PulletCryptoRadar.Features;

namespace PulletCryptoRadar.DataFoundation;

public sealed class QuickCheckService(IQuickCheckStore store, IFoundationEvidenceStore evidence,
    CollectionPipeline pipeline, TimeProvider clock)
{
    private (AssetIdentity? Asset,bool Active) Resolve(Guid id)
    {
        var now = clock.GetUtcNow();
        var catalog = store.Universe();
        var fresh = catalog?.Universe is not null && catalog.Evidence.ReceivedTime <= now &&
            now-catalog.Evidence.ReceivedTime <= TimeSpan.FromHours(8);
        var known = store.Assets().SingleOrDefault(asset => asset.AssetId == id);
        var instrument = fresh ? catalog!.Universe!.SingleOrDefault(item =>
            known is not null ? item.Symbol == known.BinanceSymbol : BinanceIdentity.AssetId(item.Symbol) == id) : null;
        return (known ?? (instrument is null ? null : new(id,instrument.BaseAsset,instrument.BaseAsset,
            instrument.Symbol,null,null,[],now)),instrument is not null);
    }

    public QuickCheckReport? Cached(Guid id)
    {
        var (asset,active) = Resolve(id);
        return asset is null ? null : Report(asset,active,"Cached");
    }

    public async Task<QuickCheckReport?> AnalyzeAsync(Guid id, CancellationToken token)
    {
        var (asset,active) = Resolve(id);
        if (asset is null) return null;
        if (store.Assets().All(item => item.AssetId != id))
        {
            // Serializes registration across quick checks and the background collector.
            asset = store.EnsureRegistered(asset);
        }
        store.TouchQuickCheck(id,clock.GetUtcNow());
        if (!active) return Report(asset,false,"ContractUnverifiedOrInactive");
        var completed = store.QuickCheckCompleted(id);
        if (completed <= clock.GetUtcNow() && clock.GetUtcNow()-completed < TimeSpan.FromMinutes(1))
            return Report(asset,true,"Cached");
        var started = clock.GetUtcNow();
        if (!store.TryBeginQuickCheck(id,started)) return Report(asset,true,"RefreshBusyOrCoolingDown");
        var state = "RefreshAttempted";
        try
        {
            await pipeline.CollectAsync(asset,token);
            await pipeline.CollectCandlesAsync(asset,token);
            await pipeline.CollectOpenInterestHistoryAsync(asset,token);
            if ((asset.CoinGeckoId is not null || asset.CoinMarketCapId is not null || ResearchNetworks.Single(asset) is not null) &&
                store.ReserveJob("Metadata:"+asset.AssetId,clock.GetUtcNow(),TimeSpan.FromHours(6)))
                await pipeline.CollectSupplementaryAsync(asset,token,
                    store.ReserveJob("Ownership:"+asset.AssetId,clock.GetUtcNow(),TimeSpan.FromHours(12)));
            store.FinishQuickCheck(id,started,clock.GetUtcNow());
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or ArgumentException)
        {
            state = "RefreshFailed";
        }
        // Quick checks never create radar episodes or alter the research priority queue.
        return Report(asset,true,state);
    }

    private QuickCheckReport Report(AssetIdentity asset,bool active,string state)
    {
        var now = clock.GetUtcNow();
        var snapshot = evidence.Latest(asset.AssetId);
        var research = new RadarEngine().Calculate(asset,snapshot,now);
        var findings = new List<string>();
        if (state == "RefreshBusyOrCoolingDown")
            findings.Add("正在刷新、处于一分钟冷却或本地请求预算不足；本次返回已有资料，请稍后重试。");
        if (snapshot is null) findings.Add("尚无该代币的已保存行情快照。");
        else if (snapshot.AsOf > now || now-snapshot.AsOf > TimeSpan.FromSeconds(90))
            findings.Add("保存的行情快照已失鲜；原始指标仅用于历史参考，当前研究判断已重新检查时效。");
        if (state == "RefreshAttempted" && research.Stage == RadarStage.InsufficientData) state = "Partial";
        if (!active) findings.Add("有效合约目录未确认该交易对；本次不发起行情采集。");
        if (research.MissingEvidence.Count > 0) findings.Add("缺失或失鲜："+string.Join("、",research.MissingEvidence));
        if (research.Stage == RadarStage.Overheated) findings.Add("价格与成交量快速扩张，需警惕追涨和波动风险。");
        if (research.Stage == RadarStage.Expansion) findings.Add("价格、成交量与持仓量同时扩张；尚未完成交易准入判断。");
        if (research.Stage == RadarStage.Participation) findings.Add("出现参与度增强特征；尚不能归因为庄家吸筹。");
        if (research.Stage == RadarStage.Observing) findings.Add("当前未满足组合参与度条件，继续观察。");
        if (!research.LowCapResearchQualified) findings.Add("低市值、所有权或流动性证据不足，不能认定为埋伏机会。");
        findings.Add("代币快查独立于自动模拟准入，本报告仅展示证据，不生成买卖指令或收益概率。");
        return new(asset,now,snapshot?.AsOf,state,"NotAssessed",active,research,findings,snapshot?.Metrics ?? []);
    }
}
