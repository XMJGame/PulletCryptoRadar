namespace PulletCryptoRadar.Core;

public enum AddressRole { Unknown,Exchange,DexAuthority,LiquidityPool,TeamTreasury,Vesting,Burn }
public enum AddressSourceKind { ExplorerLabel,ProtocolReference,HistoricalIssuerDisclosure }
public sealed record AddressRoleEvidence(string Id,ChainId Chain,string Address,string? TokenContract,
    AddressRole Role,string Entity,string SourceUrl,AddressSourceKind SourceKind,string Statement,
    DateTimeOffset ReviewedAt,DateTimeOffset ExpiresAt,DateTimeOffset? SourcePublishedAt=null);
public sealed record HolderRoleRow(HolderAddress Holder,AddressRole Role,string Status,string? Entity,
    IReadOnlyList<AddressRoleEvidence> Evidence);
public sealed record HolderRoleBreakdown(DateTimeOffset LabelAsOf,decimal ListedSupplyPercent,
    decimal SourceAttributedPercent,decimal UnknownListedPercent,decimal OutsideListPercent,
    decimal UnknownContractPercent,IReadOnlyList<RoleShare> Categories,IReadOnlyList<HolderRoleRow> Addresses);
public sealed record RoleShare(AddressRole Role,int Addresses,decimal SupplyPercent);
public sealed record HolderRoleChange(AddressRole Role,int ComparableAddresses,decimal? BalanceChange,
    decimal? SupplyPercentChangePoints,int EnteredList,int LeftList);

public static class AddressRoles
{
    public static HolderRoleBreakdown Analyze(HolderSnapshot snapshot,IReadOnlyList<AddressRoleEvidence> evidence,DateTimeOffset now)
    {
        var rows=snapshot.Addresses.Select(holder=>
        {
            var matches=evidence.Where(proof=>proof.Chain==snapshot.Chain &&
                ResearchNetworks.SameAddress(snapshot.Chain,proof.Address,holder.Address) &&
                (proof.TokenContract is null || ResearchNetworks.SameAddress(snapshot.Chain,proof.TokenContract,snapshot.Contract)) &&
                proof.ReviewedAt<=now).ToArray();
            // A historical issuer disclosure is supporting evidence, not a current classification by itself.
            var active=matches.Where(proof=>proof.ExpiresAt>now && proof.SourceKind!=AddressSourceKind.HistoricalIssuerDisclosure).ToArray();
            var roles=active.Select(proof=>(proof.Role,proof.Entity)).Distinct().ToArray();
            var status=roles.Length>1?"ConflictingSources":roles.Length==1?"SourceAttributed":matches.Length==0?"Unknown":
                matches.All(proof=>proof.ExpiresAt<=now)?"ReviewExpired":"HistoricalOnly";
            return new HolderRoleRow(holder,roles.Length==1?roles[0].Role:AddressRole.Unknown,status,
                roles.Length==1?roles[0].Entity:null,matches);
        }).ToArray();
        var unknown=rows.Where(row=>row.Role==AddressRole.Unknown).ToArray();
        return new(now,snapshot.ListedSupplyPercent,rows.Where(row=>row.Role!=AddressRole.Unknown).Sum(row=>row.Holder.SupplyPercent),
            unknown.Sum(row=>row.Holder.SupplyPercent),100-snapshot.ListedSupplyPercent,
            unknown.Where(row=>row.Holder.IsContract==true).Sum(row=>row.Holder.SupplyPercent),
            Enum.GetValues<AddressRole>().Select(role=>new RoleShare(role,rows.Count(row=>row.Role==role),
                rows.Where(row=>row.Role==role).Sum(row=>row.Holder.SupplyPercent))).ToArray(),rows);
    }

    public static IReadOnlyList<HolderRoleChange> Compare(HolderSnapshot current,HolderSnapshot? baseline,
        IReadOnlyList<AddressRoleEvidence> evidence,DateTimeOffset now)
    {
        if(HolderResearch.Compare(current,baseline).Status!="ReceiptSnapshotsOnly")return [];
        var after=Analyze(current,evidence,now);var before=Analyze(baseline!,evidence,now);
        var comparer=current.Chain==ChainId.Solana?StringComparer.Ordinal:StringComparer.OrdinalIgnoreCase;
        var previous=before.Addresses.ToDictionary(row=>row.Holder.Address,comparer);
        var latest=after.Addresses.ToDictionary(row=>row.Holder.Address,comparer);
        return Enum.GetValues<AddressRole>().Select(role=>
        {
            var common=after.Addresses.Where(row=>row.Role==role && previous.TryGetValue(row.Holder.Address,out var old) && old.Role==role).ToArray();
            return new HolderRoleChange(role,common.Length,common.Length==0?null:common.Sum(row=>row.Holder.Balance-previous[row.Holder.Address].Holder.Balance),
                common.Length==0?null:common.Sum(row=>row.Holder.SupplyPercent-previous[row.Holder.Address].Holder.SupplyPercent),
                after.Addresses.Count(row=>row.Role==role && !previous.ContainsKey(row.Holder.Address)),
                before.Addresses.Count(row=>row.Role==role && !latest.ContainsKey(row.Holder.Address)));
        }).ToArray();
    }
}
