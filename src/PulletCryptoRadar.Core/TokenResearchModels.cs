namespace PulletCryptoRadar.Core;

public sealed record ResearchLink(string Name,string Url);
public sealed record TokenResearchClaim(string CoinGeckoId,string Platform,string ContractAddress,string Category,string Title,string Summary,
    string SourceUrl,string SourceKind,DateTimeOffset? PublishedAt,DateTimeOffset CheckedAt,DateTimeOffset ReviewAfter,
    DateTimeOffset? EventAt=null,decimal? TokenAmount=null,string? Address=null,string? RoleHint=null);
public interface ITokenResearchRepository { IReadOnlyList<TokenResearchClaim> Claims(); }
public sealed record TokenResearchItem(TokenResearchClaim Claim,string Status,string Meaning);
public sealed record TokenResearchAnalysis(IReadOnlyList<TokenResearchItem> Items,decimal? SupplyCirculatingPercent,decimal? FdvToMarketCap,
    decimal? DocumentedUnlockNext7Days,decimal? UnlockToCirculatingPercent,bool UnlockCalendarComplete,string EventAttribution,
    IReadOnlyList<string> Missing);
public sealed record HolderResearchRow(int? Rank,string Address,decimal Percent,string Role,string Confidence,string ReportedTag,
    bool? IsContract,bool? IsLocked,string? TokenAccount,string ExplorerUrl,string? EvidenceUrl,DateTimeOffset? LabelCheckedAt,
    decimal? Change24hPercentagePoints);
public sealed record HolderRelationship(string Kind,IReadOnlyList<string> Addresses,string Counterparty,int TransferCount,
    DateTimeOffset FirstAt,DateTimeOffset LastAt,IReadOnlyList<string> TransactionUrls,string Meaning);
public sealed record TokenHolderAnalysis(string Chain,string ContractAddress,DateTimeOffset? SourceTime,bool Fresh,bool OrderedTop10,
    decimal? RawTop10Percent,decimal? KnownCustodyOrInfrastructurePercent,decimal? RemainingObservedPercent,decimal? ClassifiedBalancePercent,
    IReadOnlyList<HolderResearchRow> Holders,IReadOnlyList<HolderRelationship> Relationships,IReadOnlyList<ResearchLink> Tools,
    string DistributionMeaning,string RelationshipCoverage,IReadOnlyList<string> Caveats);
public sealed record TokenFollowupPoint(int Hours,DateTimeOffset DueAt,string Status,DateTimeOffset? PriceAt,decimal? Price,
    decimal? ReturnPercent,bool? BelowInvalidation,string Note);
public sealed record TokenFollowup(Guid ReportId,DateTimeOffset EvaluatedAt,string Benchmark,IReadOnlyList<TokenFollowupPoint> Points);
public interface ITokenFollowupRepository
{
    TokenFollowup? Get(Guid reportId);
    void Save(TokenFollowup followup);
}
