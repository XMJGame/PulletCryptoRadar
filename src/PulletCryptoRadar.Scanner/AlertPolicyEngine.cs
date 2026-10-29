using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Scanner;

public sealed class AlertPolicyEngine(AlertOptions options)
{
    public const string Version = "AlertPolicy-1.0";
    public string BudgetDate(DateTimeOffset now) => TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(options.BudgetTimeZone)).ToString("yyyy-MM-dd");
    public int DailyUsed(IReadOnlyList<NotificationRecord> records, DateTimeOffset now)
        => records.Count(n => n.Kind == NotificationKind.Normal && BudgetDate(n.CreatedAt) == BudgetDate(now));
    public int WindowAssetsUsed(IReadOnlyList<NotificationRecord> records, DateTimeOffset now)
        => WindowRecords(records, now).Select(n => n.AssetId).Distinct().Count();

    public AlertPolicyPlan Evaluate(RadarSnapshot snapshot, AlertPolicyState state,
        IReadOnlyList<NotificationRecord> history, DateTimeOffset now)
    {
        var notifications = new List<NotificationRecord>(); var decisions = new List<AlertDecision>();
        if (snapshot.ScanId == Guid.Empty || now < snapshot.AsOf || now - snapshot.AsOf > TimeSpan.FromSeconds(options.MaxSnapshotAgeSeconds))
            return new(state, [], [new("Snapshot", "全市场", MoveDirection.Down, SignalGrade.Unknown, null, 0, "Unknown", false, "快照缺失或过期", null)]);
        var marketFresh = snapshot.Market.Status == DataStatus.Valid && snapshot.Market.AsOf <= now
            && now - snapshot.Market.AsOf <= TimeSpan.FromSeconds(options.MaxSnapshotAgeSeconds);
        var marketRegime = marketFresh ? snapshot.Market.Regime : null;
        var systemic = marketRegime is MarketRegime.SystemicPump or MarketRegime.SystemicCrash;
        var gap = state.LastMarketObservation is { } last && now - last > TimeSpan.FromMinutes(options.SystemicResetMinutes);
        var systemicKey = state.SystemicKey;
        var previousRegime = state.SystemicRegime;
        if (marketFresh && (!systemic || gap || previousRegime != marketRegime)) { systemicKey = null; previousRegime = null; }
        if (systemic && marketFresh)
        {
            if (systemicKey is null)
            {
                // A brief normal/unknown interval must not turn the same broad move into a new toast storm.
                var recentSameRegime = history.Where(n => n.Kind == NotificationKind.Systemic && n.Stage == marketRegime.ToString()
                    && n.CreatedAt <= now && now - n.CreatedAt < TimeSpan.FromMinutes(options.SystemicResetMinutes))
                    .OrderByDescending(n => n.CreatedAt).FirstOrDefault();
                systemicKey = recentSameRegime?.DedupKey["Market:".Length..] ?? Guid.NewGuid().ToString("N");
                previousRegime = marketRegime;
            }
            var dedup = $"Market:{systemicKey}";
            if (!history.Any(n => n.DedupKey == dedup))
            {
                var crash = marketRegime == MarketRegime.SystemicCrash;
                notifications.Add(new(Guid.NewGuid(), dedup, NotificationKind.Systemic, null, null, "Binance USDT 永续", crash ? MoveDirection.Down : MoveDirection.Up,
                    SignalGrade.Unknown, null, marketRegime.ToString()!, now, now.AddSeconds(options.NotificationLifetimeSeconds), NotificationStatus.Pending,
                    crash ? "Binance 合约市场出现系统性下跌" : "Binance 合约市场出现系统性上涨",
                    $"1h 中位收益 {snapshot.Market.MedianReturn1h:0.00}%，有效覆盖 {snapshot.Market.ValidAssets}/{snapshot.Market.TotalAssets}。研究预警，请查看市场状态。", StrategyVersion: Version));
            }
        }
        var oldCandidates = state.Candidates.Where(c => now >= c.FirstSeen && now - c.FirstSeen <= TimeSpan.FromMinutes(options.WindowMinutes))
            .ToDictionary(c => c.Signal.Key, StringComparer.Ordinal);
        var nextCandidates = new List<AlertCandidate>(); var eligible = new List<AlertCandidate>();
        var active = snapshot.ActiveEpisodes.ToDictionary(e => e.Id);
        foreach (var o in snapshot.Observations)
        foreach (var a in new[] { o.Up, o.Down })
        {
            var key = $"{o.EpisodeId}:{a.Direction}"; var grade = SignalGrader.Grade(a);
            var signal = new AlertSignal(key, o.EpisodeId ?? 0, o.Input.Instrument.AssetId, o.Input.Instrument.Symbol, a.Direction, snapshot.AsOf, a, grade);
            var reason = Check(signal, o.Input, active.GetValueOrDefault(signal.EpisodeId), marketRegime, now);
            var sent = history.Where(n => n.Kind is NotificationKind.Normal or NotificationKind.WatchListCritical && n.EpisodeId == o.EpisodeId).OrderBy(n => n.CreatedAt).ToArray();
            if (reason is null && sent.Length >= 2) reason = "同一 Episode 已达两次通知上限";
            if (reason is null && sent.Length == 1)
            {
                var prior = sent[0];
                var stageUpgrade = a.Direction == MoveDirection.Up && prior.Stage is "Ignition" or "Breakout" && a.Stage == "Acceleration"
                    || a.Direction == MoveDirection.Down && prior.Stage != "Crash" && a.Stage == "Crash";
                if (!(a.Score >= prior.Score + options.EscalationScoreIncrease || stageUpgrade)) reason = "Episode 尚未明显升级";
            }
            if (reason is null)
            {
                var firstSeen = oldCandidates.GetValueOrDefault(key)?.FirstSeen ?? now;
                var candidate = new AlertCandidate(signal, firstSeen); nextCandidates.Add(candidate); eligible.Add(candidate);
                reason = "等待本窗口 Top-N 竞争";
            }
            decisions.Add(new(key, signal.Symbol, a.Direction, grade, a.Score, a.DataConfidence, a.Stage, false, reason, o.EpisodeId));
        }
        var used = DailyUsed(history, now); var window = WindowRecords(history, now).Select(n => n.AssetId).ToHashSet();
        foreach (var candidate in eligible.OrderByDescending(c => c.Signal.Grade).ThenByDescending(c => c.Signal.Assessment.Score).ThenBy(c => c.Signal.Symbol))
        {
            var signal = candidate.Signal; var a = signal.Assessment;
            var personalCritical = IsPersonalCritical(a);
            string reason;
            if (!options.ToastEnabled) reason = "Windows 通知已关闭";
            else if (!personalCritical && now - candidate.FirstSeen < TimeSpan.FromSeconds(options.CompetitionDelaySeconds)) reason = "等待 Top-N 竞争缓冲";
            else if (!personalCritical && used >= options.DailyNormalLimit) reason = "普通通知每日预算已用完";
            else if (!personalCritical && !window.Contains(signal.AssetId) && window.Count >= options.MaxNewAssetsPerWindow) reason = $"{options.WindowMinutes} 分钟新标的名额已用完";
            else
            {
                var count = history.Count(n => n.Kind is NotificationKind.Normal or NotificationKind.WatchListCritical && n.EpisodeId == signal.EpisodeId);
                notifications.Add(new(Guid.NewGuid(), $"Episode:{signal.EpisodeId}:{count + 1}", personalCritical ? NotificationKind.WatchListCritical : NotificationKind.Normal, signal.EpisodeId, signal.AssetId,
                    signal.Symbol, signal.Direction, signal.Grade, a.Score, a.Stage, now, now.AddSeconds(options.NotificationLifetimeSeconds), NotificationStatus.Pending,
                    $"{signal.Symbol} · {signal.Grade} 级{(signal.Direction == MoveDirection.Up ? "上涨异动" : "下跌风险")}",
                    $"{a.Stage} · 分数 {a.Score:0.0} · 置信度 {a.DataConfidence} · {string.Join(" / ", a.ConfirmedFamilies)}。仅作研究观察。", StrategyVersion: a.StrategyVersion));
                if (!personalCritical) { used++; window.Add(signal.AssetId); }
                reason = "已预留通知额度";
            }
            var index = decisions.FindIndex(d => d.Key == signal.Key);
            decisions[index] = decisions[index] with { Eligible = reason == "已预留通知额度", Reason = reason };
        }
        return new(new(nextCandidates, systemicKey, previousRegime, marketFresh ? now : state.LastMarketObservation), notifications, decisions);
    }

    private string? Check(AlertSignal s, MarketView input, MoveEpisode? episode, MarketRegime? regime, DateTimeOffset now)
    {
        var a = s.Assessment;
        if (a.Status != DataStatus.Valid || input.Price is null || input.PriceSourceTime is null || input.PriceSourceTime > now
            || now - input.PriceSourceTime > TimeSpan.FromSeconds(options.MaxPriceAgeSeconds)) return "行情缺失、过期或无效";
        if (a.DataConfidence < 70) return "数据置信度不足 70；仅保留雷达观察";
        if (s.Grade is not (SignalGrade.A or SignalGrade.S)) return "评分或独立特征确认未达到 A/S 级";
        if (!a.IsCandidate || episode is null || episode.Direction != s.Direction || episode.State == EpisodeState.Closed
            || episode.DataStatus != DataStatus.Valid) return "没有对应的有效活跃 Episode";
        if (a.Direction == MoveDirection.Up && a.Stage is not ("Ignition" or "Breakout" or "Acceleration")) return "预热、过热、派发或冷却阶段不作新机会通知";
        if (a.Direction == MoveDirection.Down && a.Stage is not ("Breakdown" or "Selloff" or "Crash")) return "下跌风险尚未确认结构";
        if (a.Direction == MoveDirection.Up && a.Risk is SignalRisk.High or SignalRisk.Critical) return "RiskGate：高风险否决上涨机会通知";
        if (!IsPersonalCritical(a) && (regime == MarketRegime.SystemicCrash || regime == MarketRegime.SystemicPump && a.Direction == MoveDirection.Up)) return "全市场事件合并通知，抑制同向单币刷屏";
        return null;
    }
    private NotificationRecord[] WindowRecords(IReadOnlyList<NotificationRecord> records, DateTimeOffset now)
        => records.Where(n => n.Kind == NotificationKind.Normal && n.CreatedAt <= now && now - n.CreatedAt < TimeSpan.FromMinutes(options.WindowMinutes)).ToArray();
    private static bool IsPersonalCritical(MoveAssessment assessment)
        => assessment.IsWatchListCritical && assessment.Risk == SignalRisk.Critical && assessment.Direction == MoveDirection.Down;
}
