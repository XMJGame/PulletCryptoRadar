namespace PulletCryptoRadar.Core;

public sealed record OpportunityUniverseRow(int AssetId,string Symbol,decimal? Price,decimal? Return24h,decimal? Volume24h,
    DateTimeOffset? PriceSourceTime,DateTimeOffset? PriceReceivedTime,string Status);
public sealed record OpportunityRow(int AssetId,string Symbol,IReadOnlyList<string> Tracks,decimal? Price,decimal? Return24h,
    decimal? Volume24h,DateTimeOffset? PriceSourceTime,DateTimeOffset? PriceReceivedTime,int Hours,decimal? Range72hPercent,
    decimal? HourVolumeRatio,decimal? Oi24hPercent,DateTimeOffset? OiSourceTime,DateTimeOffset? OiReceivedTime,
    string State,IReadOnlyList<string> Supporting,IReadOnlyList<string> Risks,IReadOnlyList<string> Missing,
    IReadOnlyList<ForwardDecision> Plans)
{public TenfoldProfile? Tenfold {get;init;}}
public sealed record TenfoldProfile(string Status,bool VerifiedSmallCap,string? CoinGeckoId,string? Chain,string? ContractAddress,string? Provider,
    decimal? MarketCapUsd,decimal? ReferenceMarketCapUsd,DateTimeOffset? MarketCapSourceTime,
    decimal? AdjustedTop10Percent,DateTimeOffset? ConcentrationSourceTime,decimal? Top10Change24hPoints,
    long? HolderCount,decimal? HolderChange24hPercent,decimal? Past30DayPriceMultiple,
    decimal? TargetPrice10x,decimal? ImpliedMarketCap10x,decimal? Probability30d10x,string Prediction,
    IReadOnlyList<string> Supporting,IReadOnlyList<string> Risks,IReadOnlyList<string> Missing)
{public string? ClassificationFingerprint {get;init;}}
public sealed record OpportunitySnapshot(Guid Id,DateTimeOffset AsOf,string Version,string RulesHash,int DeepLimit,
    IReadOnlyList<OpportunityUniverseRow> Universe,IReadOnlyList<OpportunityRow> Rows);
public interface IOpportunityRepository
{
    OpportunitySnapshot? Latest();
    IReadOnlyList<OpportunitySnapshot> History(int limit);
    void Save(OpportunitySnapshot snapshot);
    OpportunitySnapshot? AtOrBefore(DateTimeOffset time)=>null;
}
