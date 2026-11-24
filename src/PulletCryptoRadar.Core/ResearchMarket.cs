namespace PulletCryptoRadar.Core;

public sealed record ResearchMarketWork(Guid LeaseId,Guid AssetId,bool OpenInterest,bool Candles);
public sealed record ResearchMarketSchedule(Guid AssetId,DateTimeOffset? LastAttemptAt,
    DateTimeOffset? NextOiAt,DateTimeOffset? NextCandlesAt,DateTimeOffset? LeaseUntil,
    DateTimeOffset? LastOiSuccessAt,DateTimeOffset? LastCandlesSuccessAt);
