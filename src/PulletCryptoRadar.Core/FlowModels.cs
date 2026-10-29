namespace PulletCryptoRadar.Core;

public sealed class FlowOptions
{
    public bool Enabled {get;set;}=true;
    public int PollSeconds {get;set;}=120;
    public int Confirmations {get;set;}=64;
    public int PageBlocks {get;set;}=500;
    public int InitialBlocks {get;set;}=2000;
    public int HourRequestBudget {get;set;}=6000;
    public int DayRequestBudget {get;set;}=130000;
    public bool IsValid()=>PollSeconds is >=120 and <=3600&&Confirmations is >=64 and <=1024&&PageBlocks is >=1 and <=500
        &&InitialBlocks>=PageBlocks&&InitialBlocks<=10000&&HourRequestBudget is >=160 and <=10000&&DayRequestBudget is >=160 and <=150000;
}

public sealed record TokenTransfer(string Chain,string ContractAddress,long BlockNumber,string BlockHash,string TransactionHash,
    int LogIndex,DateTimeOffset BlockTime,string From,string To,string RawAmount,string Provider,DateTimeOffset ReceivedTime);
public sealed record FlowCursor(string Chain,string ContractAddress,long StartBlock,long NextBlock,string LastBlockHash,
    DateTimeOffset LastBlockTime,long ObservedHead,DateTimeOffset UpdatedAt)
{public DateTimeOffset? StartTime {get;init;} public int ConfirmationDepth {get;init;}=64;}
public sealed record FlowAttempt(string Chain,string ContractAddress,DateTimeOffset NextAttempt,string? Error,bool Halted=false);
public sealed record AddressInspection(string Chain,string Address,long BlockNumber,string BlockHash,bool HasCode,string CodeSha256,
    DateTimeOffset CheckedAt,string Source,string Conclusion);
public sealed record FlowBatch(FlowCursor Cursor,long? ExpectedNextBlock,IReadOnlyList<TokenTransfer> Transfers,IReadOnlyList<AddressInspection> Inspections);
public sealed record TransferEvidence(TokenTransfer Transfer,WalletLabelType FromType,WalletLabelType ToType,long FromLabelRevision,long ToLabelRevision,
    string FromEvidence,string ToEvidence,decimal? SupplyPercent,string Kind);
public sealed record FlowSummary
{
    public bool TrustedIdentity {get;init;}
    public bool Fresh {get;init;}
    public bool WindowComplete {get;init;}
    public DateTimeOffset? ThroughTime {get;init;}
    public int ObservedTransfers {get;init;}
    public int ProjectOutflows {get;init;}
    public int ExchangeInflows {get;init;}
    public int ExchangeOutflows {get;init;}
    public decimal? ProjectOutflowSupplyPercent {get;init;}
    public decimal? ProjectToExchangeSupplyPercent {get;init;}
    public IReadOnlyList<TransferEvidence> Evidence {get;init;}=[];
    public string Caveat {get;init;}="尚未建立可靠转账观测；Unknown 不代表没有资金流";
}
public sealed record FlowView(int AssetId,string Symbol,FlowCursor? Cursor,FlowAttempt? Attempt,FlowSummary Summary,
    IReadOnlyList<TransferEvidence> Transfers,IReadOnlyList<AddressInspection> Inspections);
public interface IFlowRepository
{
    FlowCursor? Cursor(string chain,string contract);
    FlowAttempt? Attempt(string chain,string contract);
    IReadOnlyList<TokenTransfer> Transfers(string chain,string contract,DateTimeOffset since,int limit);
    IReadOnlyList<AddressInspection> Inspections(string chain);
}
public interface IFlowPersistence
{
    Task<bool> ReserveAsync(FlowAttempt attempt,DateTimeOffset now,int requests,int hourLimit,int dayLimit,CancellationToken ct);
    Task SaveAsync(FlowBatch batch,FlowAttempt attempt,CancellationToken ct);
    Task FailAsync(FlowAttempt attempt,CancellationToken ct);
    Task<bool> RetryTransportAsync(string chain,string contract,DateTimeOffset now,CancellationToken ct);
}
public interface IFlowProvider
{
    Task<FlowBatch> FetchAsync(ChainBinding binding,FlowCursor? cursor,IReadOnlyList<string> inspect,CancellationToken ct);
}
public interface IFlowFeatureSource {FlowSummary Features(int assetId,DateTimeOffset now);}
public sealed class FlowReorgException():Exception("已保存区块锚点发生变化，采集已暂停；需人工检查分叉后再恢复，未跳过区块");
