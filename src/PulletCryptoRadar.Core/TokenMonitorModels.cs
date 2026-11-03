namespace PulletCryptoRadar.Core;

public sealed record TokenMonitorTarget(string Id,string CoinGeckoId,string Symbol,string Platform,string ContractAddress,
    string? VestingContract=null,string? VestingRuntimeSha256=null,string? SourceUrl=null,string? PermissionReview=null);
public sealed record MonitorIdentity(DateTimeOffset SourceTime,DateTimeOffset CheckedAt,DateTimeOffset ReviewAfter,string EvidenceUrl,string Basis);
public sealed record MonitorBalance(string Address,string RawBalance);
public sealed record VestingAllocation(int Index,string Name,string RawCap,string RawReleased,string Recipient,long PeriodSeconds,
    DateTimeOffset? NextRelease,string RawReleaseAmount);
public sealed record VestingSnapshot(string Address,string RuntimeSha256,string Token,string Admin,bool TokenSet,bool TgeReleased,string RawBalance,
    IReadOnlyList<VestingAllocation> Allocations,string SourceUrl,string PermissionReview);
public sealed record MonitorTransferWindow(long StartBlock,long NextBlock,string EndBlockHash,DateTimeOffset StartTime,DateTimeOffset ThroughTime,string AddressSetHash);
public sealed record TokenMonitorSnapshot(Guid Id,string TargetId,string ContractAddress,long BlockNumber,string BlockHash,DateTimeOffset SourceTime,
    DateTimeOffset ReceivedTime,int Decimals,string RawTotalSupply,IReadOnlyList<MonitorBalance> Balances,VestingSnapshot? Vesting,
    MonitorTransferWindow Window,IReadOnlyList<TokenTransfer> Transfers,int RpcCalls)
{
    public string? FindingVersion {get;init;}
    public string? Finding {get;init;}
    public DateTimeOffset? AnalyzedAt {get;init;}
}
public sealed record TokenMonitorState(string TargetId,MonitorIdentity? Identity,bool IdentityRevoked,DateTimeOffset NextIdentityAttempt,
    DateTimeOffset NextRpcAttempt,DateTimeOffset NextHolderAttempt,string? IdentityError,string? RpcError,bool Halted,TokenMonitorSnapshot? Latest);
public sealed record MonitorBudgetEntry(DateTimeOffset At,int Requests);
public sealed record MonitorBudgetView(int ReservedHour,int ReservedDay,int HourLimit=256,int DayLimit=4096);
public sealed record MonitorBalanceView(string Address,string Amount,decimal? SupplyPercent,string? Change24hAmount,decimal? Change24hPercentagePoints,string Role,string EvidenceStatus);
public sealed record VestingReleaseView(string Name,string Recipient,string Cap,string Released,string Remaining,string PerRelease,long PeriodSeconds,
    DateTimeOffset? NextRelease,string Status,string EarliestPermitted7Days);
public sealed record TokenMonitorView(TokenMonitorTarget Target,string IdentityStatus,MonitorIdentity? Identity,string? IdentityError,string? RpcError,
    DateTimeOffset NextAttempt,bool Halted,bool Fresh,long? BlockNumber,string? BlockHash,DateTimeOffset? SourceTime,DateTimeOffset? BaselineTime,
    decimal? TrackedSupplyPercent,IReadOnlyList<MonitorBalanceView> Balances,VestingSnapshot? Vesting,IReadOnlyList<VestingReleaseView> Releases,
    string? EarliestPermitted7Days,decimal? EarliestPermitted7DaysSupplyPercent,string TransferCoverage,DateTimeOffset? FlowStartedAt,DateTimeOffset? FlowThroughTime,int? ObservedTransfers,
    int? ConfirmedExchangeInflows,int? CandidateExchangeInflows,IReadOnlyList<TokenTransfer> TransferEvidence,IReadOnlyList<string> Caveats)
{
    public string? FindingVersion {get;init;}
    public string? Finding {get;init;}
    public DateTimeOffset? AnalyzedAt {get;init;}
}
public interface ITokenMonitorProvider
{
    Task<TokenMonitorSnapshot> FetchAsync(TokenMonitorTarget target,IReadOnlyList<string> addresses,TokenMonitorSnapshot? previous,CancellationToken ct);
}
public interface ITokenMonitorRepository
{
    IReadOnlyList<TokenMonitorTarget> Targets();
    TokenMonitorState? State(string id);
    IReadOnlyList<TokenMonitorSnapshot> History(string id,int limit);
    void Save(TokenMonitorState state,TokenMonitorSnapshot? snapshot=null);
    bool Reserve(DateTimeOffset now,int requests);
    MonitorBudgetView Budget(DateTimeOffset now);
}
