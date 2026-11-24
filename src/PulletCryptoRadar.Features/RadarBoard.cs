using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Features;

public sealed record RadarMetricDetail(string Metric,string Status,decimal? Value,string? Provider,string Reason,
    IReadOnlyList<RadarSourceDetail> Sources);
public sealed record RadarSourceDetail(string Provider,decimal? Value,DateTimeOffset? SourceTime,
    DateTimeOffset ReceivedTime,TimeQuality TimeQuality);
public sealed record RadarResearchRow(Guid AssetId,string Symbol,RadarStage Stage,bool DataQualified,
    DateTimeOffset? LastCollectedAt,IReadOnlyList<ChainToken> Chains,IReadOnlyList<RadarMetricDetail> Metrics,
    LongHoldReview? LongHoldReview = null);
public sealed record RadarCoverage(int ScannedContracts,int VerifiedIdentities,int MarketCapReady,int DataQualified,
    int ActivityFresh,int ActivityStale,int UnmappedIdentities);
public sealed record RadarBoard(DateTimeOffset AsOf,RadarCoverage Coverage,IReadOnlyList<RadarResearchRow> Research,
    IReadOnlyList<RadarResearchRow> Activity,IReadOnlyList<RadarResearchRow> DataChecks,
    IReadOnlyList<string> Limitations, IReadOnlyList<RadarResearchRow> EarlyWatch, int EarlyWatchCount);

public sealed class RadarBoardBuilder
{
    public RadarResearchRow Row(AssetIdentity asset,FeatureSnapshot? snapshot,DateTimeOffset now,HolderSnapshot? holders=null)
    {
        var assessment = new RadarEngine().Calculate(asset,snapshot,now);
        var metrics = new List<RadarMetricDetail>();
        foreach (var metric in new[] { Metric.Price,Metric.MarketCap,Metric.DexLiquidity,Metric.Top10,Metric.TotalClusterPercent,
            Metric.VolumeRatioH1,Metric.Return24hClosed,Metric.TakerBuyShareH1,Metric.OiQuantityChange1h,Metric.OiQuantityChange4h })
        {
            var saved = snapshot?.Metrics.SingleOrDefault(item => item.Metric == metric);
            var chainMetric = metric is Metric.DexLiquidity or Metric.Top10 or Metric.TotalClusterPercent;
            var missingId = metric == Metric.MarketCap && asset.CoinGeckoId is null && asset.CoinMarketCapId is null ||
                chainMetric && asset.Chains.Count == 0;
            var noHolderCoverage = metric == Metric.Top10 && (saved?.Evidence.Any(item =>
                item.Methodology=="provider-distribution-no-holder-coverage")==true || saved?.Value==0 &&
                snapshot?.Metrics.SingleOrDefault(item=>item.Metric==Metric.Nakamoto)?.Value==0);
            if(metric==Metric.Top10 && saved?.Status!=ResolutionStatus.Conflict && (saved?.Value is null || noHolderCoverage) && holders is not null &&
                holders.Provider=="GoPlus" && holders.AssetId==asset.AssetId && holders.ReceivedAt<=now &&
                now-holders.ReceivedAt<=TimeSpan.FromHours(8) && ResearchNetworks.Single(asset) is {} identity &&
                holders.Chain==identity.Chain && ResearchNetworks.SameAddress(identity.Chain,holders.Contract,identity.Address))
            {
                metrics.Add(new(metric.ToString(),"UnverifiedTime",holders.ListedSupplyPercent,"GoPlus",
                    "GoPlus 已取得实际持币条目；缺原始区块时间，且尚未扣除交易所、团队和池地址；详见上方持续观察，不用于资格判定",
                    [new("GoPlus",holders.ListedSupplyPercent,null,holders.ReceivedAt,TimeQuality.ReceivedOnly)]));
                continue;
            }
            var usable = !assessment.MissingEvidence.Contains(metric.ToString()) && !noHolderCoverage;
            var status = missingId ? "IdentityMissing" : noHolderCoverage ? "NoHolderCoverage" :
                chainMetric && ResearchNetworks.Single(asset) is null ? "CollectorUnsupportedChain" :
                saved?.Status == ResolutionStatus.Conflict ? "Conflict" :
                saved?.Status == ResolutionStatus.UnverifiedTime ? "UnverifiedTime" :
                saved?.Value is null ? "NotCollectedOrMissing" : usable ? "Usable" : "StaleOrInvalid";
            var reason = status switch
            {
                "IdentityMissing" => metric == Metric.MarketCap ? "尚未核实市值平台 ID；不会按同名代码自动配对" : "尚未核实链及完整合约地址",
                "NoHolderCoverage" => "原响应的 Nakamoto 与前十占比均为零，不能当作真实分散持仓",
                "CollectorUnsupportedChain" => "当前仅适配 BSC、Ethereum、Solana 单链身份；多链身份需要先明确研究网络",
                "Conflict" => "来源数值冲突，保留各来源，不用于低市值判断",
                "UnverifiedTime" => "有数值，但缺原始数据时间；仅供查阅，不用于资格判定",
                "NotCollectedOrMissing" => "尚未采集或接口未返回可用值；查看来源能力与原始记录",
                "StaleOrInvalid" => "资料已失鲜或证据来源/身份未通过核对",
                _ => "当前证据通过对应指标的来源和时效检查"
            };
            metrics.Add(new(metric.ToString(),status,saved?.Value,saved?.Provider,reason,
                saved?.Evidence.Select(item => new RadarSourceDetail(item.Provider,item.Value,item.SourceTime,
                    item.ReceivedTime,item.TimeQuality)).ToArray() ?? []));
        }
        return new(asset.AssetId,asset.BinanceSymbol,assessment.Stage,assessment.LowCapResearchQualified &&
            assessment.Stage != RadarStage.InsufficientData,snapshot?.AsOf,asset.Chains,metrics);
    }

    public RadarBoard Build(IReadOnlyList<AssetIdentity> assets,IReadOnlyList<FeatureSnapshot> snapshots,
        IReadOnlyList<MarketInstrument> universe,DateTimeOffset now,IReadOnlyList<HolderSnapshot>? holderSnapshots=null)
    {
        var eligible = universe.Select(item => item.Symbol).ToHashSet();
        var saved = snapshots.ToDictionary(item => item.AssetId);
        var scoped = assets.Where(item => eligible.Contains(item.BinanceSymbol) && item.BinanceSymbol is not ("BTCUSDT" or "ETHUSDT"))
            .ToDictionary(item => item.AssetId);
        var holders=(holderSnapshots??[]).GroupBy(row=>row.AssetId).ToDictionary(group=>group.Key,group=>group.OrderByDescending(row=>row.ReceivedAt).First());
        var rows = scoped.Values.Select(asset => Row(asset,saved.GetValueOrDefault(asset.AssetId),now,holders.GetValueOrDefault(asset.AssetId))).ToArray();
        var research = rows.Where(item => item.DataQualified).OrderBy(item => item.Symbol).ToArray();
        // The watchlist is a discovery queue. It must also surface quiet assets with incomplete holder coverage.
        var earlyWatch = rows.Where(item => item.Chains.Count > 0)
            .Select(item => (Row: item, Cap: item.Metrics.Single(metric => metric.Metric == "MarketCap")))
            .Where(item => item.Cap.Status == "Usable" && item.Cap.Value is > 0 and <= RadarEngine.LowCapResearchLimit)
            .OrderBy(item => item.Cap.Value).ThenBy(item => item.Row.Symbol, StringComparer.Ordinal)
            .Select(item => item.Row with { LongHoldReview = LongHoldReviews.Find(scoped[item.Row.AssetId]) }).ToArray();
        var activity = rows.Where(item => !item.DataQualified && item.Stage is RadarStage.Participation or RadarStage.Expansion or RadarStage.Overheated)
            .OrderByDescending(item => item.Stage).ThenBy(item => item.Symbol).Take(12).ToArray();
        var checks = rows.Where(item => scoped[item.AssetId].CoinGeckoId is not null ||
            scoped[item.AssetId].CoinMarketCapId is not null || item.Chains.Count > 0).OrderBy(item => item.Symbol).Take(12).ToArray();
        var scanned = eligible.Count(symbol => symbol is not ("BTCUSDT" or "ETHUSDT"));
        var coverage = new RadarCoverage(scanned,rows.Count(item => item.Chains.Count > 0),
            rows.Count(item => item.Metrics.Any(metric => metric.Metric == "MarketCap" && metric.Status == "Usable")),
            research.Length,rows.Count(item => item.Stage != RadarStage.InsufficientData),
            rows.Count(item => item.Stage == RadarStage.InsufficientData),
            scanned-rows.Count(item => item.Chains.Count > 0));
        return new(now,coverage,research.Take(24).ToArray(),activity,checks,
            ["身份补全采用人工审阅样本及平台精确地址核对，未自动覆盖全部合约",
             "已适配 BSC、Solana、Ethereum 的单链采集；免费覆盖以各次请求实际结果为准",
             "已记录持币接收快照；原始区块时间、交易所/团队地址扣除、关联主体核验和历史样板校准尚未完成",
             "当前不输出十倍概率、庄家吸筹结论或自动买入指令"],
            earlyWatch, earlyWatch.Length);
    }
}
