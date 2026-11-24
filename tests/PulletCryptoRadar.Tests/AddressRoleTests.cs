using System.Text.Json;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;

namespace PulletCryptoRadar.Tests;

public sealed class AddressRoleTests:IDisposable
{
    private static readonly DateTimeOffset Now=new(2026,11,4,8,0,0,TimeSpan.Zero);
    private const string A="0x1111111111111111111111111111111111111111";
    private const string B="0x2222222222222222222222222222222222222222";
    private const string C="0x3333333333333333333333333333333333333333";
    private const string Token="0x9999999999999999999999999999999999999999";
    private readonly string folder=Path.Combine(Path.GetTempPath(),"address-roles-"+Guid.NewGuid());
    private static HolderSnapshot Snapshot(DateTimeOffset? at=null)=>new(Guid.NewGuid(),Guid.Parse("d32327c0-5195-4335-9553-21c018200ce4"),
        ChainId.Ethereum,Token,"GoPlus",at??Now,100,100,"ProviderTop10Addresses",
        [new(A,40,40,"untrusted whale tag",false,false,[]),new(B,20,20,"team",true,true,[]),new(C,10,10,"",true,false,[])]);
    private static AddressRoleEvidence Proof(string address=A,AddressRole role=AddressRole.Exchange,string entity="Exchange A",string id="proof")=>
        new(id,ChainId.Ethereum,address,null,role,entity,"https://etherscan.io/address/"+address,AddressSourceKind.ExplorerLabel,
            "Reviewed public address label",Now.AddMinutes(-1),Now.AddDays(29));

    [Fact]
    public void DecompositionKeepsTotalSupplyDenominatorAndUnknownAndOutsideListSeparate()
    {
        var result=AddressRoles.Analyze(Snapshot(),[Proof(),Proof(C,AddressRole.DexAuthority,"Dex",id:"dex")],Now);
        Assert.Equal(70,result.ListedSupplyPercent);Assert.Equal(50,result.SourceAttributedPercent);
        Assert.Equal(20,result.UnknownListedPercent);Assert.Equal(30,result.OutsideListPercent);Assert.Equal(20,result.UnknownContractPercent);
        Assert.Equal(100,result.SourceAttributedPercent+result.UnknownListedPercent+result.OutsideListPercent);
        Assert.Equal(20,Assert.Single(result.Categories,row=>row.Role==AddressRole.Unknown).SupplyPercent);
        var unknown=Assert.Single(result.Addresses,row=>row.Holder.Address==B);
        Assert.Equal(AddressRole.Unknown,unknown.Role);Assert.Equal("Unknown",unknown.Status); // contract, lock and tag cannot establish team identity
    }
    [Fact]
    public void DuplicatedCorroboratingSourcesDoNotDoubleCountAndConflictsStayUnknown()
    {
        var corroborating=Proof(id:"second");
        Assert.Equal(40,AddressRoles.Analyze(Snapshot(),[Proof(),corroborating],Now).SourceAttributedPercent);
        var conflict=AddressRoles.Analyze(Snapshot(),[Proof(),Proof(role:AddressRole.TeamTreasury,entity:"Team",id:"conflict")],Now);
        Assert.Equal(0,conflict.SourceAttributedPercent);
        Assert.Equal("ConflictingSources",Assert.Single(conflict.Addresses,row=>row.Holder.Address==A).Status);
    }
    [Fact]
    public void ChainTokenScopeTimeAndExpiryAreCheckedBeforeAttribution()
    {
        var candidates=new[]{Proof() with {Chain=ChainId.Bsc},Proof(id:"wrong-token") with {TokenContract=B},
            Proof(id:"future") with {ReviewedAt=Now.AddMinutes(1)},Proof(id:"expired") with {ExpiresAt=Now}};
        var result=AddressRoles.Analyze(Snapshot(),candidates,Now);
        Assert.Equal(0,result.SourceAttributedPercent);Assert.Equal("ReviewExpired",result.Addresses[0].Status);
        Assert.Equal(40,AddressRoles.Analyze(Snapshot(),[Proof() with {TokenContract=Token}],Now).SourceAttributedPercent);
    }
    [Fact]
    public void HistoricalIssuerDisclosureRequiresCurrentCorroboration()
    {
        var historic=Proof() with {SourceKind=AddressSourceKind.HistoricalIssuerDisclosure,SourcePublishedAt=Now.AddYears(-4)};
        Assert.Equal("HistoricalOnly",AddressRoles.Analyze(Snapshot(),[historic],Now).Addresses[0].Status);
        var current=Proof(id:"current");
        Assert.Equal(40,AddressRoles.Analyze(Snapshot(),[historic,current],Now).SourceAttributedPercent);
    }
    [Fact]
    public void SolanaIsCaseSensitiveAndDoesNotApplyAccountLabelToTokenAccount()
    {
        const string sol="9WzDXwBbmkg8ZTbNMqUxvQRAyrZzDsGYdLVL9zYtAWWM";
        const string tokenAccount="7XX64f8UKE1nxNCSwncCUj3c2FJYdXHmDV5xd5DRdFky";
        var snapshot=Snapshot() with {Chain=ChainId.Solana,Addresses=[new(sol,40,40,"",null,false,[tokenAccount])]};
        var proof=Proof() with {Chain=ChainId.Solana,Address=sol};
        Assert.Equal(40,AddressRoles.Analyze(snapshot,[proof],Now).SourceAttributedPercent);
        Assert.Equal(0,AddressRoles.Analyze(snapshot,[proof with {Address=sol.ToLowerInvariant()}],Now).SourceAttributedPercent);
        Assert.Equal(0,AddressRoles.Analyze(snapshot,[proof with {Address=tokenAccount}],Now).SourceAttributedPercent);
    }
    [Fact]
    public void ChangesUseSameCurrentRolesAndOnlyCommonAddressesAreSummed()
    {
        var baseline=Snapshot(Now.AddHours(-6));var current=Snapshot() with {Addresses=
            [new(A,45,45,"",false,false,[]),new(B,15,15,"",true,true,[]),new(Token,10,10,"",true,false,[])]};
        var proof=Proof();var changes=AddressRoles.Compare(current,baseline,[proof],Now);
        var exchange=Assert.Single(changes,row=>row.Role==AddressRole.Exchange);
        Assert.Equal(1,exchange.ComparableAddresses);Assert.Equal(5,exchange.BalanceChange);
        var unknown=Assert.Single(changes,row=>row.Role==AddressRole.Unknown);
        Assert.Equal(-5,unknown.BalanceChange);Assert.Equal(1,unknown.EnteredList);Assert.Equal(1,unknown.LeftList);
        Assert.Empty(AddressRoles.Compare(current,baseline with {ReceivedAt=Now.AddHours(-1)},[proof],Now));
    }
    [Fact]
    public void EvidencePersistsWithoutOverwritingAndReimportDoesNotDuplicate()
    {
        var store=new FoundationStore(folder);store.Initialize(Now);var account=JsonSerializer.Serialize(store.Portfolio());
        store.ImportReviewedAddressEvidence([Proof()]);store.ImportReviewedAddressEvidence([Proof()]);
        var restarted=new FoundationStore(folder);restarted.Initialize(Now.AddHours(1));
        Assert.Single(restarted.AddressRoleProofs());
        Assert.Throws<InvalidOperationException>(()=>restarted.ImportReviewedAddressEvidence([Proof() with {Role=AddressRole.TeamTreasury}]));
        Assert.Equal(AddressRole.Exchange,Assert.Single(restarted.AddressRoleProofs()).Role);
        Assert.Equal(account,JsonSerializer.Serialize(restarted.Portfolio()));
    }
    [Fact]
    public void CatalogHasOnlyReviewedExactChainAddressesAndExpiresWithoutRenewingOnRestart()
    {
        var store=new FoundationStore(folder);store.Initialize(Now);store.ImportReviewedAddressEvidence(ReviewedAddressCatalog.Items);
        var proofs=store.AddressRoleProofs();Assert.Equal(7,proofs.Count);
        var expired=proofs.Max(proof=>proof.ExpiresAt).AddSeconds(1);
        var snapshot=Snapshot() with {Addresses=[new("0xf977814e90da44bfa03b6295a0616a897441acec",40,40,"",false,false,[])]};
        Assert.Equal(40,AddressRoles.Analyze(snapshot,proofs,Now).SourceAttributedPercent);
        Assert.Equal(0,AddressRoles.Analyze(snapshot,proofs,expired).SourceAttributedPercent);
    }
    [Theory]
    [InlineData("http://etherscan.io/address/x")]
    [InlineData("javascript:alert(1)")]
    public void InvalidSourcesCannotEnterRegistry(string url)
    {
        var store=new FoundationStore(folder);store.Initialize(Now);
        Assert.Throws<InvalidDataException>(()=>store.ImportReviewedAddressEvidence([Proof() with {SourceUrl=url}]));
        Assert.Empty(store.AddressRoleProofs());
    }
    public void Dispose(){if(Directory.Exists(folder))Directory.Delete(folder,true);}
}
