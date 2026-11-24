namespace PulletCryptoRadar.Core;

// Research evidence only. Receipt time is never promoted to an on-chain observation time.
public sealed record HolderAddress(string Address, decimal Balance, decimal SupplyPercent,
    string ProviderTag, bool? IsContract, bool? IsLocked, IReadOnlyList<string> TokenAccounts);
public sealed record HolderSnapshot(Guid EvidenceId, Guid AssetId, ChainId Chain, string Contract,
    string Provider, DateTimeOffset ReceivedAt, decimal TotalSupply, long HolderCount,
    string Scope, IReadOnlyList<HolderAddress> Addresses)
{
    public decimal ListedSupplyPercent => Addresses.Sum(row => row.SupplyPercent);
    public DateTimeOffset? SourceTime => null;
    public TimeQuality TimeQuality => TimeQuality.ReceivedOnly;
}
public sealed record HolderRead(ProviderRead Read, HolderSnapshot? Snapshot);
public sealed record HolderAddressChange(string Address, decimal? PreviousBalance, decimal? CurrentBalance,
    decimal? BalanceChange, string Status);
public sealed record HolderComparison(DateTimeOffset? BaselineReceivedAt, Guid? BaselineEvidenceId,
    decimal? ListedShareChangePoints, long? HolderCountChange, bool SupplyChanged,
    string Status, IReadOnlyList<HolderAddressChange> Addresses);

public static class HolderResearch
{
    public static HolderComparison Compare(HolderSnapshot current, HolderSnapshot? previous)
    {
        if (previous is null) return new(null,null,null,null,false,"WaitingForBaseline",[]);
        if (previous.AssetId!=current.AssetId || previous.Chain!=current.Chain ||
            previous.Provider!=current.Provider || previous.Scope!=current.Scope ||
            !ResearchNetworks.SameAddress(current.Chain,previous.Contract,current.Contract) ||
            current.ReceivedAt-previous.ReceivedAt<TimeSpan.FromHours(5) ||
            current.ReceivedAt-previous.ReceivedAt>TimeSpan.FromDays(7))
            return new(null,null,null,null,false,"Incomparable",[]);
        var comparer=current.Chain==ChainId.Solana?StringComparer.Ordinal:StringComparer.OrdinalIgnoreCase;
        var before=previous.Addresses.ToDictionary(row=>row.Address,comparer);
        var after=current.Addresses.ToDictionary(row=>row.Address,comparer);
        var changes=before.Keys.Union(after.Keys,comparer).Select(address =>
        {
            var had=before.TryGetValue(address,out var a);var has=after.TryGetValue(address,out var b);
            return new HolderAddressChange(address,a?.Balance,b?.Balance,had&&has?b!.Balance-a!.Balance:null,
                !had?"EnteredList":!has?"LeftList":"Comparable");
        }).ToArray();
        return new(previous.ReceivedAt,previous.EvidenceId,current.ListedSupplyPercent-previous.ListedSupplyPercent,
            current.HolderCount-previous.HolderCount,current.TotalSupply!=previous.TotalSupply,
            "ReceiptSnapshotsOnly",changes);
    }
}
