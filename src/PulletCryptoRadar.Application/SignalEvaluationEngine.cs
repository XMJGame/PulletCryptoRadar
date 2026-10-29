using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Application;

public sealed class SignalEvaluationEngine
{
    public const string Version = "Evaluation-1.0";
    public const string Methodology = "24h 窗口从信号之后的首个 5m 边界开盘价开始（延迟 0～5m），不是成交回测。仅完整 288 根闭合 K 线确定标签；缺口和未到期均不计失败。MFE/MAE 为方向性有利/不利幅度（非负百分数）。Lead 以首次信号冻结的 24h 参考价计算百分点，参考价缺失则 Unknown。Precision@K 按上海日期、方向取通知预留记录的每日首个标的并按信号分数选 K；不等同 Windows 已送达。Recall 仅在扫描连续覆盖整日且有完整价格历史的当日合约集合中计算；不涵盖未保存的历史市场。";

    public SignalEvaluation Evaluate(EvaluationSignal signal, IReadOnlyList<Candle> history, DateTimeOffset asOf)
    {
        var boundary = DateTimeOffset.FromUnixTimeMilliseconds((signal.Time.ToUnixTimeMilliseconds() + 299999) / 300000 * 300000);
        var end = boundary.AddHours(24);
        var bars = history.Where(c => c.IntervalMinutes == 5 && c.Symbol == signal.Symbol && c.IsValid
                && c.OpenTime >= boundary && c.EndTime <= end && c.EndTime <= asOf && c.SourceTime <= asOf && c.ReceivedTime <= asOf)
            .GroupBy(c => c.OpenTime).Select(g => g.MaxBy(c => c.ReceivedTime)!).OrderBy(c => c.OpenTime).ToArray();
        if (signal.Time > asOf || signal.Price <= 0 || bars.FirstOrDefault()?.OpenTime != boundary)
            return new(signal, EvaluationStatus.AwaitingEntry, null, null, end, bars.Length, 288,
                null, null, null, null, null, null, null, null, "首个 5m 开盘价格尚不可用或存在入口缺口");
        var entry = bars[0].Open; var high = Math.Max(entry, bars.Max(c => c.High)); var low = Math.Min(entry, bars.Min(c => c.Low));
        var rise = (high / entry - 1) * 100; var drop = (1 - low / entry) * 100;
        var complete = asOf >= end && bars.Length == 288 && bars[^1].EndTime == end && FeatureEngine.Continuous(bars);
        var status = complete ? EvaluationStatus.Complete : asOf < end ? EvaluationStatus.Pending : EvaluationStatus.Incomplete;
        return new(signal, status, boundary, entry, end, bars.Length, 288,
            signal.Direction == MoveDirection.Up ? rise : drop, signal.Direction == MoveDirection.Up ? drop : rise,
            complete ? rise >= 20 : null, complete ? rise >= 50 : null, complete ? drop >= 20 : null, complete ? drop >= 30 : null,
            complete && signal.ReferencePrice > 0 ? Math.Max(0, (high - signal.Price) / signal.ReferencePrice.Value * 100) : null,
            complete && signal.ReferencePrice > 0 ? Math.Max(0, (signal.Price - low) / signal.ReferencePrice.Value * 100) : null,
            complete ? "完整 24h；使用 5m OHLC 极值，未模拟费用、滑点或成交" : status == EvaluationStatus.Pending
                ? "24h 尚未结束；MFE/MAE 仅为已观测部分，标签未定" : "24h 已结束但存在 K 线缺口；标签保持 Unknown");
    }

    public EvaluationReport Build(EvaluationData data, DateTimeOffset from, DateTimeOffset asOf, int k)
    {
        var evaluations = data.Signals.Select(s => Evaluate(s, data.Candles.GetValueOrDefault(s.AssetId) ?? [], asOf)).ToArray();
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");
        string Date(DateTimeOffset t) => TimeZoneInfo.ConvertTime(t, zone).ToString("yyyy-MM-dd");
        DateTimeOffset Start(DateOnly d) => new(d.ToDateTime(TimeOnly.MinValue), zone.GetUtcOffset(d.ToDateTime(TimeOnly.MinValue)));
        var precision = evaluations.Where(e => e.Signal.IsNotification).GroupBy(e => (Day: Date(e.Signal.Time), e.Signal.Direction)).Select(g =>
        {
            var selected = g.GroupBy(e => e.Signal.AssetId).Select(a => a.OrderBy(e => e.Signal.Time).ThenBy(e => e.Signal.Key, StringComparer.Ordinal).First())
                .OrderByDescending(e => e.Signal.Score).ThenBy(e => e.Signal.Symbol, StringComparer.Ordinal).Take(k).ToArray();
            var done = selected.Count(e => e.Status == EvaluationStatus.Complete);
            var hits = selected.Count(e => e.Signal.Direction == MoveDirection.Up ? e.Hit20 == true : e.Drop20 == true);
            var dayStart = Start(DateOnly.Parse(g.Key.Day));
            var dayClosed = dayStart >= from && Start(DateOnly.Parse(g.Key.Day).AddDays(1)) <= asOf;
            return new DailyPrecision(g.Key.Day, g.Key.Direction, k, selected.Length, done, hits,
                !data.Truncated && dayClosed && done == selected.Length ? 100m * hits / selected.Length : null);
        }).OrderBy(x => x.Date).ToArray();
        var recall = new List<ExtremeRecall>();
        // Only full calendar days inside the requested observation period can define a daily universe.
        for (var day = DateOnly.Parse(Date(from)); Start(day.AddDays(1)) <= asOf; day = day.AddDays(1))
        {
            var start = Start(day); var end = Start(day.AddDays(1)); if (start < from) continue;
            var times = data.ScanTimes.Where(t => t >= start && t < end).Order().ToArray();
            var scanCoverage = times.Length >= 720 && times.FirstOrDefault() <= start.AddMinutes(2)
                && times.LastOrDefault() >= end.AddMinutes(-2) && times.Zip(times.Skip(1)).All(p => p.Second - p.First <= TimeSpan.FromMinutes(2));
            var universe = data.Instruments.Where(i => i.OnboardTime <= start).ToArray();
            var covered = new List<(Instrument Instrument, Candle[] Bars)>();
            foreach (var instrument in universe)
            {
                var bars = (data.Candles.GetValueOrDefault(instrument.AssetId) ?? []).Where(c => c.IntervalMinutes == 5 && c.IsValid
                        && c.Symbol == instrument.Symbol && c.OpenTime >= start && c.EndTime <= end && c.EndTime <= asOf && c.SourceTime <= asOf && c.ReceivedTime <= asOf)
                    .GroupBy(c => c.OpenTime).Select(g => g.Last()).OrderBy(c => c.OpenTime).ToArray();
                if (bars.Length == 288 && bars[0].OpenTime == start && bars[^1].EndTime == end && FeatureEngine.Continuous(bars)) covered.Add((instrument, bars));
            }
            foreach (var direction in new[] { MoveDirection.Up, MoveDirection.Down })
            {
                var extreme = 0; var captured = 0;
                foreach (var (instrument, bars) in covered)
                {
                    var price = bars[0].Open;
                    var threshold = direction == MoveDirection.Up ? price * 1.5m : price * .7m;
                    var crossing = bars.FirstOrDefault(c => direction == MoveDirection.Up ? c.High >= threshold : c.Low <= threshold);
                    if (crossing is null) continue; extreme++;
                    // Requiring a signal before the crossing bar OPEN avoids assuming intrabar ordering.
                    if (data.Signals.Any(s => !s.IsNotification && s.AssetId == instrument.AssetId && s.Direction == direction
                        && s.Time >= start && s.Time < crossing.OpenTime && (direction == MoveDirection.Up ? s.Price < price * 1.2m : s.Price > price * .8m))) captured++;
                }
                recall.Add(new(day.ToString("yyyy-MM-dd"), direction, universe.Length, covered.Count, extreme, captured, scanCoverage,
                    !data.Truncated && scanCoverage && extreme > 0 ? captured * 100m / extreme : null));
            }
        }
        return new(Guid.NewGuid(), asOf, from, Version, evaluations, precision, recall, data.Truncated, Methodology);
    }
    public static decimal? ReferencePrice(MarketView input)
    {
        var value = input.Features.Returns.GetValueOrDefault(1440);
        var change = value is { Status: DataStatus.Valid, Value: not null } && value.SourceTime <= input.PriceSourceTime
            ? value.Value : input.ExchangeReturn24h;
        return input.Price > 0 && change > -100 ? input.Price / (1 + change / 100) : null;
    }
}
