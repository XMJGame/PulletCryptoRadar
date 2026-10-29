using System.Globalization;
using System.Numerics;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Application;

public static class FlowEvidenceEngine
{
    private static bool Project(WalletLabelType t)=>t is WalletLabelType.Project or WalletLabelType.Team or WalletLabelType.Investor or WalletLabelType.Treasury;
    public static (FlowSummary Summary,IReadOnlyList<TransferEvidence> Rows) Calculate(OnChainView chain,FlowCursor? cursor,FlowAttempt? attempt,
        IReadOnlyList<TokenTransfer> transfers,DateTimeOffset now)
    {
        AddressLabel? Label(string address)=>chain.Features.Labels.FirstOrDefault(l=>l.Chain==chain.Binding.Chain&&l.Address.Equals(address,StringComparison.OrdinalIgnoreCase)&&l.Verified&&l.UpdatedAt<=now);
        var supply=chain.Sample is {} s&&s.SourceTime<=now&&now-s.SourceTime<=TimeSpan.FromMinutes(45)&&BigInteger.TryParse(s.RawTotalSupply,out var amount)&&amount>0?amount:BigInteger.Zero;
        var rows=transfers.Where(t=>t.Chain==chain.Binding.Chain&&t.ContractAddress==chain.Binding.ContractAddress&&t.BlockTime<=now&&t.ReceivedTime<=now).Select(t=>
        {
            var f=Label(t.From);var d=Label(t.To);var ft=f?.Type??WalletLabelType.Unknown;var dt=d?.Type??WalletLabelType.Unknown;
            var kind=t.From==t.To?"SelfTransfer":t.From=="0x0000000000000000000000000000000000000000"?"Mint":t.To is "0x0000000000000000000000000000000000000000" or "0x000000000000000000000000000000000000dead"?"Burn":Project(ft)&&dt==WalletLabelType.Exchange?"ProjectToExchange":Project(ft)&&!Project(dt)?"ProjectOutflow":ft!=WalletLabelType.Exchange&&dt==WalletLabelType.Exchange?"ExchangeInflow":ft==WalletLabelType.Exchange&&dt!=WalletLabelType.Exchange?"ExchangeOutflow":"Transfer";
            var raw=BigInteger.Parse(t.RawAmount,CultureInfo.InvariantCulture);
            decimal? percent=supply>0&&raw<=supply?(decimal)(raw*BigInteger.Pow(10,20)/supply)/1_000_000_000_000_000_000m:null;
            return new TransferEvidence(t,ft,dt,f?.Revision??0,d?.Revision??0,f?.EvidenceUrl??"",d?.EvidenceUrl??"",percent,kind);
        }).OrderByDescending(t=>t.Transfer.BlockNumber).ThenByDescending(t=>t.Transfer.LogIndex).ToArray();
        var hour=rows.Where(t=>t.Transfer.BlockTime>=now.AddHours(-1)&&BigInteger.Parse(t.Transfer.RawAmount,CultureInfo.InvariantCulture)>0).ToArray();
        var project=hour.Where(t=>t.Kind is "ProjectOutflow" or "ProjectToExchange").ToArray();var exchange=hour.Where(t=>t.Kind=="ProjectToExchange").ToArray();
        decimal? Total(TransferEvidence[] list)=>supply>0&&list.All(t=>t.SupplyPercent is not null)?list.Sum(t=>t.SupplyPercent!.Value):null;
        var fresh=chain.Features.TrustedIdentity&&cursor is not null&&cursor.UpdatedAt<=now&&now-cursor.UpdatedAt<=TimeSpan.FromMinutes(10)&&cursor.LastBlockTime<=now&&now-cursor.LastBlockTime<=TimeSpan.FromMinutes(10)&&attempt?.Error is null;
        return(new(){TrustedIdentity=chain.Features.TrustedIdentity,Fresh=fresh,ThroughTime=cursor?.LastBlockTime,
            WindowComplete=fresh&&cursor?.StartTime<=now.AddHours(-1)&&cursor.LastBlockTime>=now.AddMinutes(-2)&&cursor.NextBlock>cursor.ObservedHead-cursor.ConfirmationDepth&&transfers.Count<10000,ObservedTransfers=hour.Length,ProjectOutflows=project.Length,ExchangeInflows=hour.Count(t=>t.Kind is "ExchangeInflow" or "ProjectToExchange"),ExchangeOutflows=hour.Count(t=>t.Kind=="ExchangeOutflow"),
            ProjectOutflowSupplyPercent=Total(project),ProjectToExchangeSupplyPercent=Total(exchange),Evidence=hour.Where(t=>t.Kind is "ProjectOutflow" or "ProjectToExchange").Take(20).ToArray(),
            Caveat="最近 1h 的本地已观测事件下界；非全链净流量。分类采用当前已核对标签，归属未知不强行归类。转入交易所不证明卖出；资金经过多跳后归属不作推断。"},rows);
    }
}
public sealed class FlowService(OnChainService chain,IFlowRepository repository,IMarketRuntimeStore market,TimeProvider time,FlowOptions options):IFlowFeatureSource
{
    private readonly object _gate=new();private IReadOnlyList<FlowView> _cache=[];
    public void Refresh()
    {
        var now=time.GetUtcNow();var views=chain.Overview().Where(c=>c.Binding.Chain=="bsc").Select(c=>
        {
            var b=c.Binding;var cursor=repository.Cursor(b.Chain,b.ContractAddress);var attempt=repository.Attempt(b.Chain,b.ContractAddress);
            var rows=repository.Transfers(b.Chain,b.ContractAddress,now.AddHours(-24),10000);var calculated=FlowEvidenceEngine.Calculate(c,cursor,attempt,rows,now);
            if(!options.Enabled)calculated=(calculated.Summary with{Fresh=false},calculated.Rows);
            var topAddresses=(c.Sample?.Holders??[]).Take(10).Select(h=>h.Address).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return new FlowView(b.AssetId,market.Get(b.AssetId)?.Instrument.Symbol??$"Asset {b.AssetId}",cursor,attempt,calculated.Summary,calculated.Rows.Take(200).ToArray(),repository.Inspections(b.Chain).Where(i=>topAddresses.Contains(i.Address)).ToArray());
        }).ToArray();lock(_gate)_cache=views;
    }
    public IReadOnlyList<FlowView> Overview(){lock(_gate)return _cache;}
    public FlowSummary Features(int assetId,DateTimeOffset now)
    {
        var identity=chain.Features(assetId,now);
        lock(_gate)
        {
            var v=_cache.FirstOrDefault(v=>v.AssetId==assetId);if(v is null)return new();
            // Never return a stale positive quality flag between worker ticks.
            var trusted=options.Enabled&&identity.TrustedIdentity&&identity.Chain==v.Cursor?.Chain&&identity.ContractAddress==v.Cursor?.ContractAddress;
            return v.Summary with{TrustedIdentity=trusted,Fresh=trusted&&v.Summary.Fresh&&v.Cursor?.UpdatedAt<=now&&now-v.Cursor?.UpdatedAt<=TimeSpan.FromMinutes(10)&&now-v.Summary.ThroughTime<=TimeSpan.FromMinutes(10)};
        }
    }
}
