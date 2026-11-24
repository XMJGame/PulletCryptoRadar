using System.Text.Json;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;
using PulletCryptoRadar.Trading;

namespace PulletCryptoRadar.Tests;

public sealed class PaperPortfolioTests
{
    private static readonly DateTimeOffset Now = new(2026,11,3,10,0,0,TimeSpan.Zero);
    private static readonly PaperPortfolioEngine Engine = new();
    private static readonly TradingLimits Limits = new() { RiskPerTradeFraction = .001m };
    private static PaperPortfolio Empty() => new(Guid.NewGuid(),1000,1000,0,Now,[]);
    private static SizingProposal Proposal(PaperPortfolio portfolio,Guid assetId,TradeSide side,DateTimeOffset at,
        TradingLimits? limits = null,Guid? setupId = null)
    {
        var asset = new AssetIdentity(assetId,"TEST","Test","TESTUSDT",null,null,[],at);
        var rules = new ContractRules("TESTUSDT",.01m,1000,.01m,1,.01m);
        var book = new OrderBook(assetId,1,at,at,at,[new(99.99m,1000)],[new(100,1000)]);
        ProviderRead Read(string endpoint) => new(new(Guid.NewGuid(),"Binance","TESTUSDT",endpoint,at,200,CollectionFailure.None,"{}"),[]);
        var evidence = new ExecutionEvidence(assetId,at,Read("/depth") with { OrderBook = book },Read("/funding") with {
            FundingSettlements = [new(assetId,at.AddHours(-2),at,.0001m,100,"Regular")] });
        var setup = new EntrySetup(setupId ?? Guid.NewGuid(),assetId,EntryStrategy.TrendPullback,side,SetupPhase.PatternConfirmed,
            at.AddHours(-1),at.AddHours(-1),at.AddHours(3),100,101,side == TradeSide.Long ? 99 : 100.99m,1,at.AddSeconds(-1),at.AddHours(-1));
        return new SizingEngine().Evaluate(asset,setup,evidence,rules,portfolio.ConservativeEquity,portfolio.AvailableBalance,
            limits ?? Limits,new("paper-test-costs"),at);
    }
    private static PaperTransition Open(PaperPortfolio portfolio,Guid? asset = null,TradeSide side = TradeSide.Long,
        string group = "group",TradingLimits? limits = null) => Engine.OpenReviewed(portfolio,
            Proposal(portfolio,asset ?? Guid.NewGuid(),side,Now,limits),group,limits ?? Limits,Now);
    private static OrderBook Quote(PaperPosition position,decimal r,DateTimeOffset at,decimal liquidity = 1000)
    {
        var price = position.EntryPrice+(position.Side == TradeSide.Long ? 1 : -1)*r*Math.Abs(position.EntryPrice-position.InitialStop);
        return new(position.AssetId,at.ToUnixTimeMilliseconds(),at,at,at,
            [new(position.Side == TradeSide.Long ? price : price-.01m,liquidity)],
            [new(position.Side == TradeSide.Long ? price+.01m : price,liquidity)]);
    }

    [Theory]
    [InlineData(TradeSide.Long)]
    [InlineData(TradeSide.Short)]
    public void OpensWithFeesAndScalesOriginalQuantityThenKeepsRunnerAndNeverLoosensStop(TradeSide side)
    {
        var opening = Open(Empty(),side:side);
        Assert.Equal("Opened",opening.Status);
        var portfolio = opening.Portfolio;
        var position = Assert.Single(portfolio.Positions);
        Assert.Equal(1000-opening.Events.Sum(item => item.Fee),portfolio.Balance);
        Assert.Equal(portfolio.Balance-position.InitialMargin,portfolio.AvailableBalance);
        var targetSlice = Math.Floor(position.InitialQuantity*.25m/position.Rules.StepSize)*position.Rules.StepSize;
        var history = new List<PaperLedgerEvent>(opening.Events);
        var first = Engine.Observe(portfolio,position.PositionId,Quote(position,1.5m,Now.AddMinutes(1)),Guid.NewGuid(),Now.AddMinutes(1));
        history.AddRange(first.Events); position = Assert.Single(first.Portfolio.Positions);
        Assert.Equal(targetSlice,Assert.Single(first.Events,item => item.Type == "Reduce").Quantity);
        Assert.Equal(position.EntryPrice,position.StopPrice);
        Assert.True(position.FirstTargetDone); Assert.False(position.SecondTargetDone);
        var second = Engine.Observe(first.Portfolio,position.PositionId,Quote(position,2.5m,Now.AddMinutes(2)),Guid.NewGuid(),Now.AddMinutes(2));
        history.AddRange(second.Events); position = Assert.Single(second.Portfolio.Positions);
        Assert.Equal(position.InitialQuantity-2*targetSlice,position.RemainingQuantity);
        Assert.True(position.SecondTargetDone);
        var runner = Engine.Observe(second.Portfolio,position.PositionId,Quote(position,6,Now.AddMinutes(3)),Guid.NewGuid(),Now.AddMinutes(3));
        position = Assert.Single(runner.Portfolio.Positions);
        var stop = position.StopPrice;
        var pullback = Engine.Observe(runner.Portfolio,position.PositionId,Quote(position,5,Now.AddMinutes(4)),Guid.NewGuid(),Now.AddMinutes(4));
        Assert.Equal(stop,Assert.Single(pullback.Portfolio.Positions).StopPrice);
        var closed = Engine.Observe(pullback.Portfolio,position.PositionId,Quote(position,3.9m,Now.AddMinutes(5)),Guid.NewGuid(),Now.AddMinutes(5));
        history.AddRange(closed.Events);
        position = Assert.Single(closed.Portfolio.Positions);
        Assert.Equal("Closed",closed.Status);
        Assert.Equal(0,position.RemainingQuantity);
        Assert.Equal(0,closed.Portfolio.UsedMargin);
        Assert.Equal(1000+history.Sum(item => item.CashDelta),closed.Portfolio.Balance);
        Assert.Equal(position.InitialQuantity,history.Where(item => item.Type == "Reduce").Sum(item => item.Quantity));
        Assert.Equal(6,position.MfeR);
    }

    [Fact]
    public void GapStopUsesActualQuoteAndThinStopClosesOnlyAvailableQuantity()
    {
        var opened = Open(Empty()); var position = opened.Portfolio.Positions[0];
        var quote = Quote(position,-3,Now.AddMinutes(1),.1m);
        var partial = Engine.Observe(opened.Portfolio,position.PositionId,quote,Guid.NewGuid(),Now.AddMinutes(1));
        Assert.Equal("StopPartialRetryRequired",partial.Status);
        var fill = Assert.Single(partial.Events,item => item.Type == "Reduce");
        Assert.Equal(.1m,fill.Quantity);
        Assert.Equal(quote.Bids[0].Price,fill.Price);
        Assert.NotEqual(position.StopPrice,fill.Price);
        var rest = Engine.Observe(partial.Portfolio,position.PositionId,Quote(position,-3.1m,Now.AddMinutes(2)),Guid.NewGuid(),Now.AddMinutes(2));
        Assert.Equal("Closed",rest.Status);
        Assert.True(rest.Portfolio.Positions[0].MaeR <= -3);
    }

    [Fact]
    public void MinimumSizeSlicesAreRecordedWithoutInventingFractionalTrades()
    {
        var tiny = new TradingLimits { RiskPerTradeFraction = .00002m };
        var opening = Open(Empty(),limits:tiny); var position = opening.Portfolio.Positions[0];
        var result = Engine.Observe(opening.Portfolio,position.PositionId,Quote(position,3,Now.AddMinutes(1)),Guid.NewGuid(),Now.AddMinutes(1));
        Assert.Single(result.Events,item => item.Type == "ScaleOutSkipped");
        Assert.Equal(position.InitialQuantity,result.Portfolio.Positions[0].RemainingQuantity);
        Assert.Equal(opening.Portfolio.Balance,result.Portfolio.Balance);
    }

    [Fact]
    public void JumpAcrossBothTargetsWalksDepthOnceAndThinTargetsDoNotFakeFills()
    {
        var opening = Open(Empty()); var position = opening.Portfolio.Positions[0];
        var slice = Math.Floor(position.InitialQuantity*.25m/.01m)*.01m;
        var at = Now.AddMinutes(1); var quote = Quote(position,3,at);
        quote = quote with { Bids = [new(quote.Bids[0].Price,slice),new(quote.Bids[0].Price-.01m,100)] };
        var result = Engine.Observe(opening.Portfolio,position.PositionId,quote,Guid.NewGuid(),at);
        var fill = Assert.Single(result.Events,item => item.Type == "Reduce");
        Assert.Equal(slice*2,fill.Quantity);
        Assert.Equal(quote.Bids[0].Price-.005m,fill.Price);
        var thin = Engine.Observe(opening.Portfolio,position.PositionId,Quote(position,1.5m,at,.01m),Guid.NewGuid(),at);
        Assert.Equal("TargetExitDepthInsufficient",thin.Status);
        Assert.DoesNotContain(thin.Events,item => item.Type == "Reduce");
        Assert.Single(thin.Events,item => item.Type == "Mark");
        Assert.False(thin.Portfolio.Positions[0].FirstTargetDone);
    }

    [Fact]
    public void FundingUsesQuantityHeldAtSettlementAfterPartialExitAndCannotBeChargedTwice()
    {
        var opened = Open(Empty()); var position = opened.Portfolio.Positions[0];
        var reduced = Engine.Observe(opened.Portfolio,position.PositionId,Quote(position,1.5m,Now.AddMinutes(1)),Guid.NewGuid(),Now.AddMinutes(1));
        var history = opened.Events.Concat(reduced.Events).ToArray();
        var settlement = new FundingSettlement(position.AssetId,Now.AddMinutes(2),Now.AddMinutes(3),.001m,100,"Regular");
        var applied = Engine.Funding(reduced.Portfolio,position.PositionId,settlement,history,Guid.NewGuid(),Now.AddMinutes(3));
        Assert.Equal("FundingApplied",applied.Status);
        var ledger = Assert.Single(applied.Events);
        Assert.Equal(reduced.Portfolio.Positions[0].RemainingQuantity,ledger.Quantity);
        Assert.Equal(-ledger.Quantity*.1m,ledger.CashDelta);
        Assert.Equal(settlement.SettledAt,ledger.SettlementAt);
        var duplicate = Engine.Funding(applied.Portfolio,position.PositionId,settlement,history.Concat(applied.Events).ToArray(),Guid.NewGuid(),Now.AddMinutes(4));
        Assert.Equal("FundingAlreadyApplied",duplicate.Status);
        Assert.Equal(applied.Portfolio.Balance,duplicate.Portfolio.Balance);
        Assert.Equal("FundingAlreadyApplied",Engine.Funding(applied.Portfolio,position.PositionId,
            settlement with { SettledAt = settlement.SettledAt.ToOffset(TimeSpan.FromHours(8)) },
            history.Concat(applied.Events).ToArray(),Guid.NewGuid(),Now.AddMinutes(4)).Status);
        Assert.Equal("PositionHistoryRequired",Engine.Funding(applied.Portfolio,position.PositionId,settlement,[],Guid.NewGuid(),Now.AddMinutes(4)).Status);
        Assert.Equal("CompletePositionHistoryRequired",Engine.Funding(reduced.Portfolio,position.PositionId,settlement,opened.Events,Guid.NewGuid(),Now.AddMinutes(4)).Status);
    }

    [Fact]
    public void RepeatedSetupDuplicateAssetStaleMarksAndInvalidQuoteAreRejected()
    {
        var portfolio = Empty(); var asset = Guid.NewGuid(); var proposal = Proposal(portfolio,asset,TradeSide.Long,Now);
        var opened = Engine.OpenReviewed(portfolio,proposal,"group",Limits,Now);
        Assert.Equal("AssetAlreadyHeld",Open(opened.Portfolio,asset).Status);
        var stale = opened.Portfolio with { Positions = opened.Portfolio.Positions.Select(item => item with { LastObservedAt = Now.AddSeconds(-11) }).ToArray() };
        Assert.Equal("OpenPositionMarksStale",Open(stale).Status);
        var position = opened.Portfolio.Positions[0];
        var invalid = Quote(position,2,Now.AddMinutes(1)) with { SourceTime = Now.AddMinutes(-1) };
        Assert.Equal("FreshCausalExitBookRequired",Engine.Observe(opened.Portfolio,position.PositionId,invalid,Guid.NewGuid(),Now.AddMinutes(1)).Status);
        Assert.Equal("SetupAlreadyConsumed",Engine.OpenReviewed(opened.Portfolio,proposal with { AssetId = Guid.NewGuid() },"other",Limits,Now).Status);
        Assert.Equal("ReplanInsidePortfolioTransaction",Engine.OpenReviewed(opened.Portfolio,proposal with { AssetId = Guid.NewGuid(),SetupId = Guid.NewGuid() },"other",Limits,Now).Status);
    }

    [Fact]
    public void CorrelationDirectionAndTotalRiskHaveIndependentLimits()
    {
        var correlatedLimits = new TradingLimits { RiskPerTradeFraction = .0024m,MaximumOpenRiskFraction = .02m };
        var p = Open(Empty(),limits:correlatedLimits).Portfolio;
        p = Open(p,limits:correlatedLimits).Portfolio;
        Assert.Equal("CorrelationRiskExceeded",Open(p,limits:correlatedLimits).Status);
        p = Open(Empty(),group:"a",limits:correlatedLimits).Portfolio;
        p = Open(p,group:"b",limits:correlatedLimits).Portfolio;
        p = Open(p,group:"c",limits:correlatedLimits).Portfolio;
        Assert.Equal("DirectionalRiskExceeded",Open(p,group:"d",limits:correlatedLimits).Status);
        var totalLimits = new TradingLimits { RiskPerTradeFraction = .0024m,MaximumOpenRiskFraction = .0048m };
        p = Open(Empty(),group:"a",limits:totalLimits).Portfolio;
        p = Open(p,side:TradeSide.Short,group:"b",limits:totalLimits).Portfolio;
        Assert.Equal("TotalOpenRiskExceeded",Open(p,group:"c",limits:totalLimits).Status);
    }

    [Fact]
    public async Task ConcurrentStoresEnforceSixSlotsAndTransactionsAreIdempotentAcrossRestart()
    {
        var folder = Path.Combine(Path.GetTempPath(),"v2-paper-"+Guid.NewGuid());
        try
        {
            var store = new FoundationStore(folder); store.Initialize(Now);
            var assets = Enumerable.Range(0,8).Select(i => new AssetIdentity(Guid.NewGuid(),"TEST"+i,"Test"+i,
                "TEST"+i+"USDT",null,null,[],Now)).ToArray();
            foreach (var asset in assets) store.Register(asset);
            var tasks = Enumerable.Range(0,8).Select(i => Task.Run(() => new FoundationStore(folder).CommitPaper(Guid.NewGuid(),portfolio =>
                Open(portfolio,asset:assets[i].AssetId,side:i%2 == 0 ? TradeSide.Long : TradeSide.Short,group:"group"+i)))).ToArray();
            var results = await Task.WhenAll(tasks);
            Assert.Equal(6,results.Count(item => item.Status == "Opened"));
            Assert.Equal(2,results.Count(item => item.Status == "MaximumPositionsReached"));
            store = new FoundationStore(folder);
            var portfolio = store.Portfolio();
            Assert.Equal(6,portfolio.Positions.Count);
            Assert.Equal(6,store.PaperEvents().Count);
            Assert.Equal(1000+store.PaperEvents().Sum(item => item.CashDelta),store.Account().Cash);
            var pos = portfolio.Positions[0]; var command = Guid.NewGuid(); var at = Now.AddMinutes(1);
            var first = store.CommitPaper(command,p => Engine.Observe(p,pos.PositionId,Quote(pos,1.5m,at),Guid.NewGuid(),at));
            store = new FoundationStore(folder);
            var duplicate = store.CommitPaper(command,_ => throw new InvalidOperationException("Must not rerun command"));
            Assert.Equal(JsonSerializer.Serialize(first),JsonSerializer.Serialize(duplicate));
            Assert.Equal(1000+store.PaperEvents().Sum(item => item.CashDelta),store.Portfolio().Balance);
            var balance = store.Account().Cash;
            Assert.Throws<InvalidDataException>(() => store.CommitPaper(Guid.NewGuid(),p => new(p with { Balance = p.Balance+100 },[],"Broken")));
            Assert.Equal(balance,store.Account().Cash);
            Assert.True(store.AuditPaper().CashReconciled);
            Assert.Throws<InvalidDataException>(() => store.CommitPaper(Guid.NewGuid(),p => new(p with {
                Positions = p.Positions.Select(item => item.PositionId == pos.PositionId ? item with { RemainingQuantity = 0,ClosedAt = at } : item).ToArray()
            },[],"BrokenQuantity")));
            Assert.Equal(balance,store.Account().Cash);
        }
        finally
        {
            foreach (var file in Directory.GetFiles(folder)) File.Delete(file);
            Directory.Delete(folder);
        }
    }
}
