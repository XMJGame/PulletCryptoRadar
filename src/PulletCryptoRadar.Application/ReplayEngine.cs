using System.Text.Json;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Scanner;

namespace PulletCryptoRadar.Application;

public sealed class ReplayEngine
{
    public const string Version = "Replay-1.0";
    public const string CaptureVersion = "FeatureSnapshot-1.0/UpMove-2.0/DownMove-2.0/AlertPolicy-1.0";

    public ReplayResult Run(IEnumerable<ReplayFrame> frames, ReplayRequest request, CancellationToken ct)
    {
        var trace = new List<ReplayPoint>(); var signals = new List<EvaluationSignal>(); var notifications = new List<NotificationRecord>();
        var coverage = new List<ReplayFrameCoverage>();
        IReadOnlyList<MoveEpisode> tracking = []; var state = new AlertPolicyState([]);
        DateTimeOffset? previous = null; var gaps = 0; var count = 0; var observations = 0; long lastId = 0;
        foreach (var frame in frames)
        {
            ct.ThrowIfCancellationRequested();
            if (frame.AsOf < request.From || frame.AsOf > request.To) continue;
            if (++count > 360) throw new ArgumentException("每次回放最多 360 个扫描快照（默认约 6 小时）");
            if (frame.EngineVersion != CaptureVersion) throw new InvalidDataException("快照算法版本不受当前引擎支持，拒绝静默套用新模型");
            if (previous >= frame.AsOf) throw new InvalidDataException("回放快照必须严格按历史顺序且时间唯一");
            var options = JsonSerializer.Deserialize<VolatilityOptions>(frame.ParametersJson) ?? throw new InvalidDataException("缺少扫描参数");
            var alertOptions = JsonSerializer.Deserialize<AlertOptions>(frame.AlertParametersJson) ?? throw new InvalidDataException("缺少通知参数");
            if (!options.IsValid() || !alertOptions.IsValid()) throw new InvalidDataException("历史参数无效");
            if (previous is { } prior && frame.AsOf - prior > TimeSpan.FromSeconds(options.IntervalSeconds * 2)) gaps++;
            previous = frame.AsOf;
            var up = new UpMoveDetector(options); var down = new DownMoveDetector(options);
            foreach (var m in frame.Inputs) Validate(m, frame.AsOf);
            var input = frame.Inputs.OrderBy(m => m.Instrument.AssetId).Select(m => new RadarObservation(m, up.Detect(m, frame.AsOf), down.Detect(m, frame.AsOf))).ToArray();
            coverage.Add(new(frame.AsOf, input.Length, frame.Inputs.Count(m => m.Price > 0 && m.PriceSourceTime <= frame.AsOf
                && frame.AsOf - m.PriceSourceTime <= TimeSpan.FromSeconds(options.PriceFreshnessSeconds))));
            var episodes = new EpisodeEngine(options); episodes.RestoreId(lastId);
            var plan = episodes.Advance(tracking, input, frame.ScanId, frame.AsOf); tracking = plan.Tracking;
            lastId = Math.Max(lastId, plan.Changed.Select(e => e.Id).DefaultIfEmpty().Max());
            var snapshot = new RadarSnapshot(frame.ScanId, frame.AsOf, new MarketRegimeDetector(options).Detect(frame.Inputs, frame.AsOf),
                plan.Observations, plan.Active, plan.Events, frame.ParametersJson);
            var policy = new AlertPolicyEngine(alertOptions).Evaluate(snapshot, state, notifications, frame.AsOf); state = policy.State;
            notifications.AddRange(policy.Notifications);
            foreach (var e in plan.Events.Where(e => e.Kind == "Created" && e.Input is not null))
                signals.Add(new($"ReplayEpisode:{e.EpisodeId}", e.AssetId, e.Symbol, e.Direction, e.Time, e.Input!.Price!.Value,
                    e.Assessment?.Score, e.StrategyVersion, e.EpisodeId, SignalEvaluationEngine.ReferencePrice(e.Input)));
            foreach (var n in policy.Notifications.Where(n => n.Kind == NotificationKind.Normal))
            {
                var m = frame.Inputs.First(m => m.Instrument.AssetId == n.AssetId);
                signals.Add(new($"ReplayNotification:{n.Id}", m.Instrument.AssetId, n.Symbol, n.Direction!.Value,
                    frame.AsOf, m.Price!.Value, n.Score, n.StrategyVersion ?? "Unknown", n.EpisodeId!.Value, SignalEvaluationEngine.ReferencePrice(m), true));
            }
            trace.AddRange(plan.Observations.Where(o => o.Input.Instrument.Symbol.Equals(request.Symbol, StringComparison.OrdinalIgnoreCase))
                .Select(o => new ReplayPoint(frame.AsOf, o.Input.Instrument.AssetId, o.Input.Instrument.Symbol, o.Input.Price, o.Up, o.Down, o.EpisodeId)));
            observations += input.Length;
        }
        return new(Guid.NewGuid(), request.From, request.To, count, observations, gaps, trace, signals, notifications,
            "RecordedFeatureSnapshots", "复用生产 Detector、EpisodeEngine、MarketRegime 和 AlertPolicy；输入为生产 FeatureEngine 当时的快照，不是原始 tick 重建。通知预算从空状态开始，按每次扫描评估；模拟通知不会调用 Windows，也不写入生产 Episode/通知预算。缺口计数、市场清单覆盖变化和冷启动会影响结果，不能声称逐条复原历史通知。") { Coverage = coverage };
    }

    public ReplayAuditResult Audit(ReplayAuditInput input)
    {
        var e = input.Event;
        if (e.Input is null || e.Assessment is null) return Result("MissingInput", null, "事件未保存足够输入");
        var supported = e.Assessment.Direction == MoveDirection.Up ? new[] { UpMoveDetector.Version, DerivativeMoveDetector.UpVersion }
            : new[] { DownMoveDetector.Version, DerivativeMoveDetector.DownVersion };
        if (!supported.Contains(e.StrategyVersion)) return Result("UnsupportedVersion", null, "历史策略版本未被当前引擎保留");
        var options = JsonSerializer.Deserialize<VolatilityOptions>(input.ParametersJson) ?? throw new InvalidDataException("缺少历史参数");
        Validate(e.Input, e.Time);
        var replayed = e.Assessment.Direction == MoveDirection.Up ? new UpMoveDetector(options).Detect(e.Input, e.Time) : new DownMoveDetector(options).Detect(e.Input, e.Time);
        var match = e.Assessment.StrategyVersion == replayed.StrategyVersion && e.Assessment.Score == replayed.Score
            && e.Assessment.Stage == replayed.Stage && e.Assessment.DataConfidence == replayed.DataConfidence
            && e.Assessment.Status == replayed.Status && e.Assessment.IsCandidate == replayed.IsCandidate && e.Assessment.Risk == replayed.Risk
            && e.Assessment.ConfirmedFamilies.SequenceEqual(replayed.ConfirmedFamilies)
            && e.Assessment.Components.SequenceEqual(replayed.Components);
        return Result(match ? "Match" : "Mismatch", replayed.Score, match ? "历史输入 + 历史参数复算一致" : "检测结果有差异；请检查版本、参数或算法漂移");
        ReplayAuditResult Result(string status, double? score, string reason) => new(e.Id, e.Symbol, e.Time, e.StrategyVersion, status, e.Assessment?.Score, score, reason);
    }

    // Raw-input replay callers use the same feature engines as live collection and enforce received-time causality.
    public static MarketView Recalculate(MarketView market, IEnumerable<Candle> candles, DerivativeRuntimeSnapshot derivatives, DateTimeOffset asOf)
    {
        if (market.PriceReceivedTime > asOf || market.PriceSourceTime > asOf || market.FundingSourceTime > asOf)
            throw new InvalidDataException("原始回放行情含未来时间");
        var bars = candles.Where(c => c.IsValid && c.Symbol == market.Instrument.Symbol && c.EndTime <= asOf
                && c.SourceTime <= asOf && c.ReceivedTime <= asOf).GroupBy(c => (c.IntervalMinutes, c.OpenTime))
            .Select(g => g.MaxBy(c => c.ReceivedTime)!).OrderBy(c => c.OpenTime).ToArray();
        var features = new FeatureEngine().Calculate(market, bars.Where(c => c.IntervalMinutes == 1).TakeLast(2880).ToArray(),
            bars.Where(c => c.IntervalMinutes == 5).TakeLast(2016).ToArray(), bars.Where(c => c.IntervalMinutes == 15).TakeLast(2880).ToArray(), asOf);
        var safe = derivatives with { Series = derivatives.Series.ToDictionary(x => x.Key,
                x => (IReadOnlyList<DerivativeSample>)x.Value.Where(p => p.SourceTime <= asOf && p.ReceivedTime <= asOf).OrderBy(p => p.SourceTime).ToArray()),
            Liquidations = derivatives.Liquidations.Where(p => p.SourceTime <= asOf && p.ReceivedTime <= asOf).ToArray(),
            LastStreamActivity = derivatives.LastStreamActivity <= asOf ? derivatives.LastStreamActivity : null,
            CoverageStart = derivatives.CoverageStart <= asOf ? derivatives.CoverageStart : null };
        return market with { Features = features with { Derivatives = new DerivativeFeatureEngine().Calculate(market with { Features = features }, safe, asOf) } };
    }
    private static void Validate(MarketView market, DateTimeOffset asOf)
    {
        if (market.PriceSourceTime > asOf || market.PriceReceivedTime > asOf || market.FundingSourceTime > asOf || market.Instrument.OnboardTime > asOf)
            throw new InvalidDataException("快照含未来行情或上市时间");
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(market.Features));
        Check(json.RootElement);
        void Check(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
                foreach (var p in element.EnumerateObject())
                {
                    if (p.Name == "SourceTime" && p.Value.ValueKind == JsonValueKind.String && p.Value.GetDateTimeOffset() > asOf)
                        throw new InvalidDataException("特征源时间晚于回放时刻");
                    Check(p.Value);
                }
            else if (element.ValueKind == JsonValueKind.Array) foreach (var value in element.EnumerateArray()) Check(value);
        }
    }
}
