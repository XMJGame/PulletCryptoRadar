namespace PulletCryptoRadar.Host;

public sealed record MarketCapitalResolution([property: System.Text.Json.Serialization.JsonPropertyName("status")] string Status, [property: System.Text.Json.Serialization.JsonPropertyName("difference")] decimal? Difference, [property: System.Text.Json.Serialization.JsonPropertyName("canonicalUsd")] decimal? CanonicalUsd,
    [property: System.Text.Json.Serialization.JsonPropertyName("provider")] string? Provider, [property: System.Text.Json.Serialization.JsonPropertyName("confidence")] string Confidence);

public static class MarketCapitalResolver
{
    public static MarketCapitalResolution Resolve(DataObservation<MarketCapital> gecko,
        DataObservation<MarketCapital> cmc, DateTimeOffset now)
    {
        var first = IsUsable(gecko, now) ? gecko.Value?.MarketCap : null;
        var second = IsUsable(cmc, now) ? cmc.Value?.MarketCap : null;
        decimal? difference = first is > 0 && second is > 0 ? Math.Abs(second.Value - first.Value) / first.Value : null;
        var status = difference is not null ? (difference > .15m ? "Conflict" : "Confirmed") :
            first is not null || second is not null ? "SingleSource" :
            gecko.Value is not null || cmc.Value is not null ? "Stale" : "Missing";
        var confidence = status switch
        {
            "Confirmed" => "High",
            "SingleSource" => "Limited",
            "Conflict" => "Low",
            _ => "Unknown"
        };
        return new(status, difference, first ?? second,
            first is not null ? gecko.Provider : second is not null ? cmc.Provider : null, confidence);
    }

    public static bool IsUsable(DataObservation<MarketCapital> value, DateTimeOffset now) =>
        value.Error == ProviderError.None && value.TimeQuality == TimeQuality.ProviderTimestamp &&
            value.SourceTime is { } at && at <= now && now - at <= TimeSpan.FromMinutes(30) &&
            value.Value?.MarketCap is > 0;
}
