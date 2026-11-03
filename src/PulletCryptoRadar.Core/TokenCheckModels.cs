namespace PulletCryptoRadar.Core;

public sealed record TokenCheckCandidate(MarketCoin Coin,IReadOnlyList<Instrument> Instruments,bool ExactMatch);
public sealed record TokenCheckRequest(string CoinGeckoId,string Platform,string ContractAddress,int? AssetId=null);
public sealed record TokenProjectSnapshot(string CoinGeckoId,DateTimeOffset SourceTime,DateTimeOffset ReceivedTime,
    decimal? PriceUsd,decimal? Return24h,decimal? MarketCapUsd,decimal? FdvUsd,decimal? SpotVolume24hUsd,
    decimal? CirculatingSupply,decimal? TotalSupply,IReadOnlyDictionary<string,string> Platforms,IReadOnlyList<string> Websites);
public sealed record TokenVenueIdentity(string Symbol,string Name,DateTimeOffset SourceTime,DateTimeOffset ReceivedTime,
    bool Delisted,IReadOnlyDictionary<string,string> Contracts,string EvidenceUrl);
public interface ITokenCheckProvider
{
    Task<TokenProjectSnapshot> ProjectAsync(string coinId,CancellationToken ct);
    Task<TokenVenueIdentity> IdentityAsync(string symbol,CancellationToken ct);
}
public sealed record TokenCheckSource(string Name,string Status,DateTimeOffset? SourceTime,DateTimeOffset? ReceivedTime,string Url,string Note);
public sealed record TokenTechnical(int Hours,bool Continuous,decimal? LastClosedPrice,decimal? Return24h,decimal? Ema20,decimal? Ema60,
    decimal? Atr14,decimal? Support,DateTimeOffset? SupportTime,decimal? Resistance,DateTimeOffset? ResistanceTime,
    decimal? Invalidation,decimal? RewardRiskToResistance,string Trend,string Confirmation);
public sealed record TokenCheckReport(Guid Id,string Version,DateTimeOffset AsOf,DateTimeOffset ExpiresAt,MarketCoin Coin,
    string Platform,string ContractAddress,int? AssetId,string? Instrument,bool IdentityVerified,string IdentityReason,
    string Verdict,string Summary,string Potential,int EvidenceCoverage,string EvidenceMeaning,
    TokenProjectSnapshot? Project,decimal? Price,decimal? PerpetualVolume24h,decimal? FundingRate,
    decimal? Oi24hPercent,DateTimeOffset? OiSourceTime,TokenTechnical Technical,OnChainSample? ChainSample,
    IReadOnlyList<string> Supporting,IReadOnlyList<string> Risks,IReadOnlyList<string> Missing,IReadOnlyList<string> NextChecks,
    IReadOnlyList<TokenCheckSource> Sources,string Isolation)
{
    public TokenHolderAnalysis? HolderAnalysis {get;init;}
    public TokenResearchAnalysis? Research {get;init;}
}
public interface ITokenCheckRepository
{
    void Save(TokenCheckReport report);
    TokenCheckReport? Get(Guid id);
    IReadOnlyList<TokenCheckReport> Recent(int limit);
    IReadOnlyList<TokenCheckReport> ForFollowup(DateTimeOffset since)=>Recent(100).Where(r=>r.AsOf>=since).ToArray();
}
