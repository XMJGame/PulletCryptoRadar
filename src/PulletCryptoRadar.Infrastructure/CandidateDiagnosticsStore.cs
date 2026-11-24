using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Infrastructure;

public sealed partial class FoundationStore
{
    public CandidateDiagnostics CandidateDiagnostics(DateTimeOffset now)
    {
        var universe = Universe();
        var fresh = universe is not null && universe.Evidence.ReceivedTime <= now &&
            now-universe.Evidence.ReceivedTime <= TimeSpan.FromHours(8);
        var assets = Assets().ToDictionary(item => item.BinanceSymbol);
        var entries = LatestEntries().ToLookup(item => item.AssetId);
        var priority = Priority(now).Select(item => item.Symbol).ToHashSet();
        var focus = CollectionFocus(now,6).Select(item => item.AssetId).ToHashSet();
        var held = Portfolio().Positions.Where(item => item.RemainingQuantity > 0).Select(item => item.AssetId).ToHashSet();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.AssetId,a.Status,a.At FROM V2PaperAdmission a WHERE a.At<=$now AND NOT EXISTS
                (SELECT 1 FROM V2PaperAdmission b WHERE b.AssetId=a.AssetId AND b.At<=$now AND
                    (b.At>a.At OR (b.At=a.At AND b.rowid>a.rowid)))
            """;
        command.Parameters.AddWithValue("$now",now.ToUnixTimeMilliseconds());
        var admissions = new Dictionary<Guid,(string Status,DateTimeOffset At)>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) admissions[Guid.Parse(reader.GetString(0))] =
                (reader.GetString(1),DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)));
        var rows = new List<CandidateDiagnostic>();
        foreach (var instrument in universe?.Universe ?? [])
        {
            assets.TryGetValue(instrument.Symbol,out var asset);
            var patterns = (asset is null ? [] : entries[asset.AssetId]).Select(item => new CandidatePattern(
                item.Strategy,item.Side,item.AsOf > now || now-item.AsOf > TimeSpan.FromMinutes(3) ? "Stale" :
                item.Setup is { } setup && setup.ExpiresAt <= now ? "Expired" :
                item.Status == "PatternConfirmed" && (item.Setup?.ConfirmedAt is not { } confirmed || confirmed > now ||
                    now-confirmed > TimeSpan.FromMinutes(1)) ? "Expired" : item.Status,item.AsOf,item.Reasons)).ToArray();
            var state = asset is not null && held.Contains(asset.AssetId) ? "Held" :
                patterns.Length == 0 ? "NotEvaluated" : patterns.All(item => item.Status == "Stale") ? "Stale" :
                patterns.Any(item => item.Status == "PatternConfirmed") ? "PatternConfirmed" :
                patterns.Any(item => item.Status == "Waiting") ? "Waiting" : "ConditionsNotMet";
            var admission = asset is not null && admissions.TryGetValue(asset.AssetId,out var found) ? found :
                ((string Status,DateTimeOffset At)?)null;
            rows.Add(new(instrument.Symbol,asset?.AssetId,state,priority.Contains(instrument.Symbol),
                asset is not null && focus.Contains(asset.AssetId),patterns.Length == 0 ? null : patterns.Max(item => item.AsOf),
                patterns,admission?.Status,admission?.At));
        }
        return new(now,fresh,rows.Count,BackgroundRequestHeadroom(now),rows.OrderByDescending(item => item.Focus)
            .ThenByDescending(item => item.Priority).ThenByDescending(item => item.LastEvaluatedAt)
            .ThenBy(item => item.Symbol,StringComparer.Ordinal).ToArray());
    }
}
