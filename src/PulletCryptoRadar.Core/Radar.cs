namespace PulletCryptoRadar.Core;

public enum RadarStage { InsufficientData, Observing, Participation, Expansion, Overheated, Excluded }
public sealed record RadarAssessment(string Version, Guid AssetId, DateTimeOffset AsOf, RadarStage Stage,
    bool LowCapResearchQualified, IReadOnlyList<string> Reasons, IReadOnlyList<string> MissingEvidence,
    IReadOnlyList<ResolvedMetric> Evidence);
public sealed record RadarEpisode(Guid EpisodeId, Guid AssetId, DateTimeOffset StartedAt, DateTimeOffset UpdatedAt,
    RadarAssessment Assessment);
public sealed record PriorityCandidate(string Symbol, decimal Return24h, decimal QuoteVolume, DateTimeOffset SourceTime,
    string Reason);
