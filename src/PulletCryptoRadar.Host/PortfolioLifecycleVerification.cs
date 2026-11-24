using System.Text.Json;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;
using PulletCryptoRadar.Trading;

namespace PulletCryptoRadar.Host;

public sealed record PortfolioLifecycleReport(bool Passed,int PeakPositions,string SeventhAdmission,int ClosedPositions,
    int FundingEvents,int Reductions,bool CashReconciled,bool QuantitiesReconciled,bool RestartPreserved,bool DuplicateIgnored,
    bool RiskWithinLimits,decimal NetCashChange,decimal Fees,decimal Funding,string Scope,string? Error=null);

public static class PortfolioLifecycleVerification
{
    public static PortfolioLifecycleReport Run(string folder)
    {
        var at=new DateTimeOffset(2026,1,1,0,0,0,TimeSpan.Zero);
        var store=new FoundationStore(folder);store.Initialize(at);
        if(store.Portfolio().Revision!=0||store.Assets().Count!=0)throw new InvalidOperationException("Use a fresh isolated portfolio fixture.");
        var engine=new PaperPortfolioEngine();
        // A deliberately smaller fixture risk exercises all six slots without overriding the
        // production correlation/directional/total limits. It is never saved as production policy.
        var limits=new TradingLimits{RiskPerTradeFraction=.0008m};
        var assets=Enumerable.Range(0,7).Select(i=>new AssetIdentity(BinanceIdentity.AssetId($"FIXTURE{i}USDT"),
            "FIXTURE"+i,"Synthetic portfolio fixture "+i,$"FIXTURE{i}USDT",null,null,[],at)).ToArray();
        foreach(var asset in assets)store.Register(asset);
        var peak=0;var riskWithinLimits=true;
        void CheckRisk()
        {
            var p=store.Portfolio();var active=p.Positions.Where(row=>row.RemainingQuantity>0).ToArray();peak=Math.Max(peak,active.Length);
            var equity=p.ConservativeEquity;
            decimal Risk(PaperPosition row)=>row.InitialPlannedRisk*row.RemainingQuantity/row.InitialQuantity;
            riskWithinLimits &= p.AvailableBalance>=0&&active.Length<=6&&active.All(row=>row.Leverage==10&&row.InitialMargin<=100)&&
                p.OpenRisk<=equity*limits.MaximumOpenRiskFraction&&active.GroupBy(row=>row.Side).All(g=>g.Sum(Risk)<=equity*limits.MaximumDirectionalRiskFraction)&&
                active.GroupBy(row=>row.CorrelationGroup).All(g=>g.Sum(Risk)<=equity*limits.MaximumCorrelationRiskFraction);
        }
        PaperTransition Open(int i,Guid command)=>store.CommitPaper(command,p=>
        {
            var asset=assets[i];var side=i%2==0?TradeSide.Long:TradeSide.Short;
            ProviderRead Read(string capability)=>new(new(Guid.NewGuid(),"Binance",asset.BinanceSymbol,capability,at,200,
                CollectionFailure.None,"SYNTHETIC PORTFOLIO FIXTURE"),[]);
            var evidence=new ExecutionEvidence(asset.AssetId,at,Read("fixture-book") with {OrderBook=
                new(asset.AssetId,1,at,at,at,[new(99.99m,1000)],[new(100,1000)])},Read("fixture-funding") with
                {FundingSettlements=[new(asset.AssetId,at.AddHours(-2),at,.0001m,100,"Regular")]});
            var setup=new EntrySetup(Guid.NewGuid(),asset.AssetId,EntryStrategy.TrendPullback,side,SetupPhase.PatternConfirmed,
                at.AddHours(-1),at.AddHours(-1),at.AddHours(3),100,101,side==TradeSide.Long?99:100.99m,1,at.AddSeconds(-1),at.AddHours(-1));
            var proposal=new SizingEngine().Evaluate(asset,setup,evidence,new(asset.BinanceSymbol,.01m,1000,.01m,1,.01m),
                p.ConservativeEquity,p.AvailableBalance,limits,new("SyntheticPortfolioCosts-1.0"),at);
            return engine.OpenReviewed(p,proposal,"Unclassified",limits,at);
        });
        Guid lastCommand=Guid.Empty;
        for(var i=0;i<6;i++)
        {
            lastCommand=Guid.NewGuid();var opened=Open(i,lastCommand);
            if(opened.Status!="Opened")throw new InvalidDataException("Six-slot fixture failed to open: "+opened.Status);
            CheckRisk();
        }
        var seventh=Open(6,Guid.NewGuid());
        var serialized=JsonSerializer.Serialize(store.Portfolio());
        store=new FoundationStore(folder);store.Initialize(at.AddSeconds(1));
        var restartPreserved=serialized==JsonSerializer.Serialize(store.Portfolio());
        var beforeDuplicate=store.Portfolio().Revision;
        store.CommitPaper(lastCommand,_=>throw new InvalidOperationException("Duplicate open callback executed."));
        var duplicateIgnored=store.Portfolio().Revision==beforeDuplicate&&store.PaperEvents().Count(row=>row.Type=="Open")==6;

        void Observe(Guid id,decimal r,DateTimeOffset now,decimal depth=1000)
        {
            store.CommitPaper(Guid.NewGuid(),p=>
            {
                var position=p.Positions.Single(row=>row.PositionId==id);var sign=position.Side==TradeSide.Long?1:-1;
                var price=position.EntryPrice+sign*r*Math.Abs(position.EntryPrice-position.InitialStop);
                var quote=new OrderBook(position.AssetId,now.ToUnixTimeMilliseconds(),now,now,now,
                    [new(position.Side==TradeSide.Long?price:price-.01m,depth)],
                    [new(position.Side==TradeSide.Long?price+.01m:price,depth)]);
                return engine.Observe(p,id,quote,Guid.NewGuid(),now);
            });
            CheckRisk();
        }
        var ids=store.Portfolio().Positions.Select(row=>row.PositionId).ToArray();
        foreach(var id in ids)Observe(id,1.6m,at.AddMinutes(1));
        for(var i=0;i<ids.Length;i++)
        {
            var position=store.Portfolio().Positions.Single(row=>row.PositionId==ids[i]);
            var settledAt=at.AddMinutes(2);var received=settledAt.AddSeconds(20);
            var settlement=new FundingSettlement(position.AssetId,settledAt,received,.0001m*(i+1),100,"Regular");
            var funding=store.CommitPaper(Guid.NewGuid(),p=>engine.Funding(p,position.PositionId,settlement,
                store.PaperEvents(position.PositionId),Guid.NewGuid(),received));
            if(funding.Status!="FundingApplied")throw new InvalidDataException("Portfolio funding failed: "+funding.Status);
            var duplicate=store.CommitPaper(Guid.NewGuid(),p=>engine.Funding(p,position.PositionId,settlement,
                store.PaperEvents(position.PositionId),Guid.NewGuid(),received));
            duplicateIgnored &= duplicate.Status=="FundingAlreadyApplied";CheckRisk();
        }
        serialized=JsonSerializer.Serialize(store.Portfolio());
        store=new FoundationStore(folder);store.Initialize(at.AddMinutes(3));restartPreserved &= serialized==JsonSerializer.Serialize(store.Portfolio());
        for(var i=0;i<ids.Length;i++)
            if(i<3)Observe(ids[i],2.6m,at.AddMinutes(4));else Observe(ids[i],-3m,at.AddMinutes(4),.1m);
        for(var i=0;i<ids.Length;i++)
            if(i<3)Observe(ids[i],5m,at.AddMinutes(5));else Observe(ids[i],-3.1m,at.AddMinutes(5));
        foreach(var id in ids.Take(3))Observe(id,2m,at.AddMinutes(6));
        var audit=store.AuditPaper();var pFinal=audit.Portfolio;
        var quantities=pFinal.Positions.All(position=>position.InitialQuantity==position.RemainingQuantity+
            audit.Events.Where(row=>row.PositionId==position.PositionId&&row.Type=="Reduce").Sum(row=>row.Quantity));
        var closed=pFinal.Positions.Count(row=>row.RemainingQuantity==0);var fundingCount=audit.Events.Count(row=>row.Type=="Funding");
        var passed=peak==6&&seventh.Status=="MaximumPositionsReached"&&closed==6&&fundingCount==6&&audit.CashReconciled&&quantities&&
            restartPreserved&&duplicateIgnored&&riskWithinLimits;
        return new(passed,peak,seventh.Status,closed,fundingCount,audit.Events.Count(row=>row.Type=="Reduce"),audit.CashReconciled,
            quantities,restartPreserved,duplicateIgnored,riskWithinLimits,pFinal.Balance-1000,audit.Events.Sum(row=>row.Fee),audit.Events.Sum(row=>row.Funding),
            "Synthetic six-asset shared account; fixture trade risk 0.08% (production default 0.25% unchanged), all Unclassified, 6 max / 10x / 100U margin; not historical strategy or live-stream validation");
    }
}
