using System.Text.Json;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;
using PulletCryptoRadar.Trading;

namespace PulletCryptoRadar.Host;

public sealed record LifecycleScenario(string Name,TradeSide Side,bool Passed,decimal NetCashChange,decimal Fees,
    decimal Funding,int Reductions,bool CashReconciled,bool RestartPreserved,bool DuplicateIgnored,string? Error);
public sealed record LifecycleReport(DateTimeOffset At,string Scope,IReadOnlyList<LifecycleScenario> Scenarios,PortfolioLifecycleReport? Portfolio=null);
public static class PaperLifecycleVerification
{
    public static LifecycleScenario Run(string folder,TradeSide side,bool gap)
    {
        var at=new DateTimeOffset(2026,1,1,0,0,0,TimeSpan.Zero);var store=new FoundationStore(folder);store.Initialize(at);
        if(store.Portfolio().Revision!=0||store.Assets().Count!=0)throw new InvalidOperationException("Use a new isolated validation database.");
        var asset=new AssetIdentity(BinanceIdentity.AssetId("FIXTUREUSDT"),"FIXTURE","Synthetic lifecycle fixture","FIXTUREUSDT",null,null,[],at);
        store.Register(asset);var limits=new TradingLimits();var engine=new PaperPortfolioEngine();
        var rules=new ContractRules(asset.BinanceSymbol,.01m,1000,.01m,1,.01m);
        ProviderRead Evidence(string path)=>new(new(Guid.NewGuid(),"Binance",asset.BinanceSymbol,path,at,200,CollectionFailure.None,"synthetic-fixture"),[]);
        var book=new OrderBook(asset.AssetId,1,at,at,at,[new(99.99m,1000)],[new(100,1000)]);
        var execution=new ExecutionEvidence(asset.AssetId,at,Evidence("fixture-book") with {OrderBook=book},Evidence("fixture-funding") with {
            FundingSettlements=[new(asset.AssetId,at.AddHours(-2),at,.0001m,100,"Regular")]});
        var setup=new EntrySetup(Guid.NewGuid(),asset.AssetId,EntryStrategy.TrendPullback,side,SetupPhase.PatternConfirmed,
            at.AddHours(-1),at.AddHours(-1),at.AddHours(3),100,101,side==TradeSide.Long?99:100.99m,1,at.AddSeconds(-1),at.AddHours(-1));
        var sizing=new SizingEngine().Evaluate(asset,setup,execution,rules,1000,1000,limits,new("synthetic-lifecycle-costs"),at);
        var openId=Guid.NewGuid();var opening=store.CommitPaper(openId,portfolio=>engine.OpenReviewed(portfolio,sizing,"Fixture",limits,at));
        if(opening.Status!="Opened")throw new InvalidDataException("Lifecycle did not open: "+opening.Status);
        var duplicate=store.CommitPaper(openId,_=>throw new InvalidOperationException("duplicate command callback ran"));
        var duplicateIgnored=store.PaperEvents().Count(row=>row.Type=="Open")==1&&duplicate.Portfolio.Revision==opening.Portfolio.Revision;
        var first=store.Portfolio().Positions.Single();var positionId=first.PositionId;var original=JsonSerializer.Serialize(store.Portfolio());
        store=new FoundationStore(folder);store.Initialize(at.AddSeconds(30));var restartPreserved=original==JsonSerializer.Serialize(store.Portfolio());
        var fundingAt=at.AddMinutes(1);var settled=new FundingSettlement(asset.AssetId,fundingAt,fundingAt.AddSeconds(20),.0001m,100,"Regular");
        var funding=store.CommitPaper(Guid.NewGuid(),portfolio=>engine.Funding(portfolio,positionId,settled,store.PaperEvents(positionId),Guid.NewGuid(),fundingAt.AddSeconds(20)));
        if(funding.Status!="FundingApplied")throw new InvalidDataException("Lifecycle funding failed: "+funding.Status);
        var steps=gap?new[]{-3m}:new[]{1.6m,2.6m,5m,2m};var step=0;
        foreach(var r in steps)
        {
            var time=at.AddMinutes(2+step++);var sign=side==TradeSide.Long?1:-1;
            var price=first.EntryPrice+sign*r*Math.Abs(first.EntryPrice-first.InitialStop);
            var exit=new OrderBook(asset.AssetId,time.ToUnixTimeMilliseconds(),time,time,time,
                [new(side==TradeSide.Long?price:price-.01m,1000)],[new(side==TradeSide.Long?price+.01m:price,1000)]);
            store.CommitPaper(Guid.NewGuid(),portfolio=>engine.Observe(portfolio,positionId,exit,Guid.NewGuid(),time));
        }
        var audit=store.AuditPaper();var position=audit.Portfolio.Positions.Single();
        var reductions=audit.Events.Count(row=>row.Type=="Reduce");
        var passed=position.RemainingQuantity==0&&audit.CashReconciled&&restartPreserved&&duplicateIgnored&&
            (gap?position.MaeR<=-3:position.FirstTargetDone&&position.SecondTargetDone&&reductions==3)&&position.FeesPaid>0;
        return new(gap?"GapStop":"ScaleOutAndRunner",side,passed,audit.Portfolio.Balance-1000,position.FeesPaid,position.FundingPnl,
            reductions,audit.CashReconciled,restartPreserved,duplicateIgnored,null);
    }
    public static async Task<int> RunAsync(string directory)
    {
        var root=Path.Combine(Path.GetFullPath(directory),"Research","Validation",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);var scenarios=new List<LifecycleScenario>();
        foreach(var side in Enum.GetValues<TradeSide>())foreach(var gap in new[]{false,true})
        {
            try{scenarios.Add(Run(Path.Combine(root,$"{side}-{gap}"),side,gap));}
            catch(Exception error) when(error is InvalidDataException or InvalidOperationException or ArgumentException)
            {scenarios.Add(new(gap?"GapStop":"ScaleOutAndRunner",side,false,0,0,0,0,false,false,false,error.Message));}
        }
        PortfolioLifecycleReport portfolio;
        try{portfolio=PortfolioLifecycleVerification.Run(Path.Combine(root,"SharedPortfolio"));}
        catch(Exception error) when(error is InvalidDataException or InvalidOperationException or ArgumentException)
        {portfolio=new(false,0,"Failed",0,0,0,false,false,false,false,false,0,0,0,"Synthetic isolated portfolio",error.Message);}
        var report=new LifecycleReport(DateTimeOffset.UtcNow,
            "Synthetic isolated sizing/ledger lifecycle; not natural signals, historical execution, live streams, or strategy profitability",scenarios,portfolio);
        var output=Path.Combine(directory,"Research","lifecycle-report.json");
        await File.WriteAllTextAsync(output,JsonSerializer.Serialize(report,HistoricalResearchCommand.Json));
        Console.WriteLine($"{output}: {scenarios.Count(row=>row.Passed)}/{scenarios.Count} passed; fixture databases: {root}");
        Console.WriteLine($"Shared six-slot portfolio: {(portfolio.Passed?"passed":"FAILED")}, {portfolio.ClosedPositions} closed, reconciled={portfolio.CashReconciled}; {portfolio.Error}");
        return scenarios.All(row=>row.Passed)&&portfolio.Passed?0:1;
    }
}
