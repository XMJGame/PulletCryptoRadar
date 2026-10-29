namespace PulletCryptoRadar.Core;

public enum WalletLabelType { Unknown,Project,Team,Investor,Treasury,Exchange,LP,Bridge,Burn,Whale,Contract }
public sealed record ChainBinding(int AssetId,string Chain,string ContractAddress,string CoinGeckoId,bool Enabled,bool Verified,
    long Revision,DateTimeOffset UpdatedAt,string Verification,IReadOnlyList<string> EvidenceUrls);
public sealed record ChainBindingRequest(string Chain,string ContractAddress,string CoinGeckoId,bool Enabled,long ExpectedRevision,bool ConfirmIdentity);
public sealed record AddressLabel(string Chain,string Address,WalletLabelType Type,bool Verified,string EvidenceUrl,long Revision,DateTimeOffset UpdatedAt);
public sealed record AddressLabelRequest(string Chain,string Address,WalletLabelType Type,string EvidenceUrl,long ExpectedRevision,bool ConfirmEvidence);
public sealed record TokenHolder(string Address,string RawBalance,decimal Percent,string ReportedTag="");
public sealed record OnChainSample(Guid Id,string Chain,string ContractAddress,string Provider,DateTimeOffset SourceTime,DateTimeOffset ReceivedTime,
    string TimeBasis,long? HolderCount,string RawTotalSupply,int? Decimals,IReadOnlyList<TokenHolder> Holders,int RequestedCount,bool OrderedTopHolders);
public sealed record OnChainFeatures
{
    public bool TrustedIdentity {get;init;}
    public string? Chain {get;init;}
    public string? ContractAddress {get;init;}
    public string? Provider {get;init;}
    public long BindingRevision {get;init;}
    public int ReturnedHolders {get;init;}
    public FeatureValue<long> HolderCount {get;init;}=FeatureValue<long>.Missing();
    public FeatureValue<decimal> HolderChange24h {get;init;}=FeatureValue<decimal>.Missing();
    public FeatureValue<decimal> HolderChange24hPercent {get;init;}=FeatureValue<decimal>.Missing();
    public Dictionary<int,FeatureValue<decimal>> RawTopPercent {get;init;}=[];
    public FeatureValue<decimal> AdjustedTop10Percent {get;init;}=FeatureValue<decimal>.Missing();
    public FeatureValue<decimal> ObservedExchangePercent {get;init;}=FeatureValue<decimal>.Missing();
    public FeatureValue<decimal> ObservedProjectPercent {get;init;}=FeatureValue<decimal>.Missing();
    public decimal ClassificationCoveragePercent {get;init;}
    public bool ConcentrationUsable {get;init;}
    public IReadOnlyList<AddressLabel> Labels {get;init;}=[];
    public IReadOnlyList<string> Caveats {get;init;}=[];
}
public sealed record ChainAttempt(string Chain,string ContractAddress,DateTimeOffset LastAttempt,DateTimeOffset NextAttempt,string? Error);
public sealed record OnChainView(ChainBinding Binding,OnChainFeatures Features,OnChainSample? Sample,ChainAttempt? Attempt);
public sealed record OnChainState(IReadOnlyList<ChainBinding> Bindings,IReadOnlyList<AddressLabel> Labels,IReadOnlyList<OnChainSample> Samples,IReadOnlyList<ChainAttempt> Attempts);
public sealed record OnChainWrite(OnChainSample? Sample,ChainAttempt Attempt);
public sealed record ChainReservation(ChainAttempt Attempt,int Requests,int HourLimit,int DayLimit,int WeekLimit);
public interface IOnChainProvider { Task<OnChainSample> FetchAsync(ChainBinding binding,CancellationToken ct); }
public interface IOnChainRepository
{
    OnChainState Restore();
    IReadOnlyList<OnChainSample> History(string chain,string contractAddress,int limit);
}
public interface IOnChainPersistence
{
    Task SaveBindingAsync(ChainBinding binding,long expectedRevision,CancellationToken ct);
    Task SaveLabelAsync(AddressLabel label,long expectedRevision,CancellationToken ct);
    Task<bool> ReserveAsync(ChainReservation reservation,CancellationToken ct);
    Task SaveSampleAsync(OnChainWrite write,CancellationToken ct);
}
public interface IOnChainFeatureSource { OnChainFeatures Features(int assetId,DateTimeOffset now); }
public sealed class OnChainProviderException(string message,DateTimeOffset retryAt):Exception(message)
{public DateTimeOffset RetryAt {get;}=retryAt;}
