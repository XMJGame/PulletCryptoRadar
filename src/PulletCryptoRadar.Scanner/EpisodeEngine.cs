using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Scanner;

public sealed record EpisodePlan(IReadOnlyList<MoveEpisode> Active, IReadOnlyList<MoveEpisode> Changed,
    IReadOnlyList<RadarEvent> Events, IReadOnlyList<RadarObservation> Observations, IReadOnlyList<MoveEpisode> Tracking);

/// <summary>Plans transitions; the caller commits the plan only after durable persistence succeeds.</summary>
public sealed class EpisodeEngine(VolatilityOptions options)
{
    private long _lastId;
    public void RestoreId(long lastId) => _lastId = Math.Max(_lastId, lastId);

    public EpisodePlan Advance(IReadOnlyList<MoveEpisode> active, IReadOnlyList<RadarObservation> observations,
        Guid scanId, DateTimeOffset now)
    {
        var remaining = new List<MoveEpisode>(); var changed = new List<MoveEpisode>(); var events = new List<RadarEvent>();
        var coolingGuards = new List<MoveEpisode>();
        var linked = new List<RadarObservation>();
        var previous = active.ToDictionary(x => x.AssetId);
        foreach (var observation in observations)
        {
            var m = observation.Input;
            previous.Remove(m.Instrument.AssetId, out var episode);
            var selected = observation.Down.IsCandidate ? observation.Down : observation.Up.IsCandidate ? observation.Up : null;
            var currentAssessment = episode?.Direction == MoveDirection.Down ? observation.Down : observation.Up;
            if (episode is { State: EpisodeState.Closed })
            {
                if (episode.EndReason != "NoNewExtreme") episode = null;
            }
            if (episode is { State: EpisodeState.Closed })
            {
                var newExtreme = m.Price is not null && (episode.Direction == MoveDirection.Up ? m.Price > episode.PeakPrice : m.Price < episode.LowestPrice);
                if (currentAssessment.Status != DataStatus.Valid && (selected is null || selected.Direction == episode.Direction)
                    || selected?.Direction == episode.Direction && !newExtreme)
                {
                    coolingGuards.Add(episode); linked.Add(observation with { EpisodeId = episode.Id }); continue;
                }
                if (selected is null) changed.Add(episode with { EndReason = "QuietPeriodReset" });
                episode = null;
            }
            if (episode is not null)
            {
                if (currentAssessment.Status != DataStatus.Valid && (selected is null || selected.Direction == episode.Direction))
                {
                    // Missing evidence cannot imply cooling, reversal, new extremes or low scores.
                    var paused = episode with { DataStatus = currentAssessment.Status };
                    changed.Add(paused); remaining.Add(paused); linked.Add(observation with { EpisodeId = paused.Id }); continue;
                }
                var price = m.Price!.Value;
                var measuredScore = currentAssessment.Status == DataStatus.Valid ? currentAssessment.Score!.Value : episode.CurrentScore;
                var fresh = episode with { CurrentPrice = price, PeakPrice = Math.Max(episode.PeakPrice, price),
                    LowestPrice = Math.Min(episode.LowestPrice, price), CurrentScore = measuredScore,
                    MaxScore = Math.Max(episode.MaxScore, measuredScore), LastObservedTime = now, DataStatus = DataStatus.Valid };
                var gap = now - episode.LastObservedTime >= TimeSpan.FromMinutes(options.EpisodeQuietMinutes);
                var reversal = selected is not null && selected.Direction != episode.Direction;
                var newExtreme = episode.Direction == MoveDirection.Up ? price > episode.PeakPrice : price < episode.LowestPrice;
                var timedOut = !newExtreme && now - episode.LastExtremeTime >= TimeSpan.FromMinutes(options.EpisodeQuietMinutes);
                var endReason = currentAssessment.StrategyVersion != episode.StrategyVersion && !reversal ? "StrategyChanged"
                    : gap ? "DataGap" : reversal ? "DirectionReversed" : timedOut ? "NoNewExtreme"
                    : currentAssessment.Score < options.EpisodeCloseScore ? "ScoreBelowThreshold" : null;
                if (endReason is not null)
                {
                    var closed = fresh with { State = EpisodeState.Closed, EndTime = now, EndReason = endReason };
                    changed.Add(closed); Record(closed, "Closed", m, currentAssessment); episode = null;
                    if (endReason == "NoNewExtreme") coolingGuards.Add(closed);
                    // An expired move must not be recreated on every scan of the same unchanged candidate.
                    if (endReason is "NoNewExtreme" or "ScoreBelowThreshold") selected = null;
                }
                else
                {
                    if (newExtreme) fresh = fresh with { LastExtremeTime = now };
                    var cooling = !currentAssessment.IsCandidate;
                    var stage = cooling ? "Cooling" : currentAssessment.Stage;
                    var escalation = StageRank(stage) > StageRank(episode.Stage) || currentAssessment.Score >= episode.MaxScore + options.EventScoreStep;
                    var state = cooling ? EpisodeState.Cooling : stage == "Overheated" ? EpisodeState.Overheated
                        : escalation ? EpisodeState.Escalating : EpisodeState.Active;
                    fresh = fresh with { Stage = stage, State = state };
                    if (stage != episode.Stage || Math.Abs(currentAssessment.Score!.Value - episode.LastRecordedScore) >= options.EventScoreStep
                        || episode.DataStatus != DataStatus.Valid)
                    {
                        fresh = fresh with { LastRecordedScore = currentAssessment.Score!.Value };
                        Record(fresh, stage != episode.Stage ? "StageChanged" : "ScoreChanged", m, currentAssessment);
                    }
                    changed.Add(fresh); remaining.Add(fresh); linked.Add(observation with { EpisodeId = fresh.Id }); continue;
                }
            }
            if (selected is not null)
            {
                _lastId = Math.Max(_lastId, now.ToUnixTimeMilliseconds() * 1000) + 1;
                var created = new MoveEpisode { Id = _lastId, AssetId = m.Instrument.AssetId, Symbol = m.Instrument.Symbol,
                    Direction = selected.Direction, StrategyVersion = selected.StrategyVersion,
                    StartTime = now, LastObservedTime = now, LastExtremeTime = now, StartPrice = m.Price!.Value,
                    CurrentPrice = m.Price.Value, PeakPrice = m.Price.Value, LowestPrice = m.Price.Value,
                    StartScore = selected.Score!.Value, CurrentScore = selected.Score.Value, MaxScore = selected.Score.Value,
                    LastRecordedScore = selected.Score.Value, Stage = selected.Stage,
                    State = selected.Stage == "Overheated" ? EpisodeState.Overheated : EpisodeState.Created, DataStatus = DataStatus.Valid };
                changed.Add(created); remaining.Add(created); Record(created, "Created", m, selected);
                linked.Add(observation with { EpisodeId = created.Id });
            }
            else linked.Add(observation);
        }
        foreach (var orphan in previous.Values)
        {
            if (orphan.State == EpisodeState.Closed) continue;
            var closed = orphan with { State = EpisodeState.Closed, EndTime = now, EndReason = "AssetInactive" };
            changed.Add(closed); Record(closed, "Closed", null, null);
        }
        return new(remaining, changed, events, linked, remaining.Concat(coolingGuards).ToArray());

        void Record(MoveEpisode e, string kind, MarketView? input, MoveAssessment? assessment)
            => events.Add(new(Guid.NewGuid(), scanId, e.Id, e.AssetId, e.Symbol, e.Direction, kind, now, e.StrategyVersion, input, assessment, e));
    }
    private static int StageRank(string stage) => stage switch
    {
        "Preheat" or "Weakening" => 1, "Ignition" => 2, "Breakout" or "Breakdown" => 3,
        "Acceleration" or "Selloff" => 4, "Overheated" or "Crash" => 5, _ => 0
    };
}
