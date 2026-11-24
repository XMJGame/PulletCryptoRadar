using System.Security.Cryptography;
using System.Text;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Trading;

public sealed class PaperPortfolioEngine
{
    public const string Version = "PaperPortfolio-2.0.0";
    private static Guid Id(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0,16));
    private static PaperTransition Reject(PaperPortfolio portfolio,string reason) => new(portfolio,[],reason);
    private static PaperTransition Apply(PaperPortfolio portfolio,PaperPosition position,IReadOnlyList<PaperLedgerEvent> events,
        DateTimeOffset now,string status) => new(portfolio with
        {
            Balance = portfolio.Balance+events.Sum(item => item.CashDelta), Revision = portfolio.Revision+1,UpdatedAt = now,
            Positions = portfolio.Positions.Where(item => item.PositionId != position.PositionId).Append(position).ToArray()
        },events,status);

    // This is the deterministic paper ledger kernel. It is not exposed as an order-admission API.
    public PaperTransition OpenReviewed(PaperPortfolio portfolio,SizingProposal proposal,string correlationGroup,
        TradingLimits limits,DateTimeOffset now)
    {
        limits.Validate();
        if (string.IsNullOrWhiteSpace(correlationGroup) || correlationGroup.Length > 64)
            throw new ArgumentException("Explicit correlation classification is required.");
        if (proposal.AsOf > now || now-proposal.AsOf > TimeSpan.FromSeconds(2) || proposal.BookEvidenceId is null ||
            proposal.Quantity is not > 0 || proposal.AverageEntry is not > 0 || proposal.PlannedLossIncludingCosts is not > 0 ||
            proposal.Margin is not > 0 || proposal.RulesUsed is not { } rules || rules.PriceTick <= 0 || rules.StepSize <= 0 ||
            proposal.Assumptions.FeePerSide is < 0 or > .01m || proposal.Assumptions.StopSlippageFraction is < 0 or > .05m ||
            proposal.Assumptions.FundingReserveFraction is < 0 or > .05m ||
            proposal.LimitsUsed.MaximumLeverage != limits.MaximumLeverage ||
            !proposal.Reasons.Contains("MarketAndSingleTradeSizingChecksPassed")) return Reject(portfolio,"ReviewedSizingRequired");
        var active = portfolio.Positions.Where(position => position.RemainingQuantity > 0).ToArray();
        if (active.Length >= limits.MaximumPositions) return Reject(portfolio,"MaximumPositionsReached");
        if (active.Any(position => position.AssetId == proposal.AssetId)) return Reject(portfolio,"AssetAlreadyHeld");
        if (portfolio.Positions.Any(position => position.SetupId == proposal.SetupId)) return Reject(portfolio,"SetupAlreadyConsumed");
        if (active.Any(position => position.LastObservedAt > now || now-position.LastObservedAt > TimeSpan.FromSeconds(10)))
            return Reject(portfolio,"OpenPositionMarksStale");
        var equity = portfolio.ConservativeEquity;
        if (equity <= 0 || portfolio.AvailableBalance <= 0) return Reject(portfolio,"InsufficientEquityOrCash");
        if (Math.Abs(proposal.EquityUsed-equity) > .00000001m ||
            Math.Abs(proposal.AvailableCashUsed-portfolio.AvailableBalance) > .00000001m)
            return Reject(portfolio,"ReplanInsidePortfolioTransaction");
        var quantity = proposal.Quantity.Value;
        var entry = proposal.AverageEntry.Value;
        var sign = proposal.Side == TradeSide.Long ? 1m : -1m;
        var stop = proposal.Side == TradeSide.Long ? Math.Floor(proposal.StructuralStop/rules.PriceTick)*rules.PriceTick :
            Math.Ceiling(proposal.StructuralStop/rules.PriceTick)*rules.PriceTick;
        var fee = quantity*entry*proposal.Assumptions.FeePerSide;
        var risk = proposal.PlannedLossIncludingCosts.Value;
        var adverseStop = stop*(1-sign*proposal.Assumptions.StopSlippageFraction);
        var modeledRisk = quantity*(sign*(entry-adverseStop)+(entry+adverseStop)*proposal.Assumptions.FeePerSide+
            entry*proposal.Assumptions.FundingReserveFraction);
        var margin = quantity*entry/limits.MaximumLeverage;
        if (stop <= 0 || sign*(entry-stop) <= 0 || quantity%rules.StepSize != 0 || quantity < rules.MinimumQuantity ||
            quantity > rules.MaximumQuantity || quantity*entry < rules.MinimumNotional || margin > limits.MaximumMarginPerPosition ||
            margin != proposal.Margin || margin+fee+quantity*entry*proposal.Assumptions.FundingReserveFraction > portfolio.AvailableBalance ||
            risk < modeledRisk || risk > equity*limits.RiskPerTradeFraction) return Reject(portfolio,"SingleTradeBudgetOrRulesExceeded");
        decimal RemainingRisk(PaperPosition position) => position.InitialPlannedRisk*position.RemainingQuantity/position.InitialQuantity;
        if (portfolio.OpenRisk+risk > equity*limits.MaximumOpenRiskFraction) return Reject(portfolio,"TotalOpenRiskExceeded");
        if (active.Where(position => position.Side == proposal.Side).Sum(RemainingRisk)+risk > equity*limits.MaximumDirectionalRiskFraction)
            return Reject(portfolio,"DirectionalRiskExceeded");
        if (active.Where(position => position.CorrelationGroup == correlationGroup).Sum(RemainingRisk)+risk > equity*limits.MaximumCorrelationRiskFraction)
            return Reject(portfolio,"CorrelationRiskExceeded");
        var id = Id($"{Version}:position:{portfolio.AccountId}:{proposal.SetupId}");
        var position = new PaperPosition(id,proposal.AssetId,proposal.SetupId,proposal.Side,proposal.Strategy,correlationGroup,
            quantity,quantity,entry,stop,stop,risk,margin,limits.MaximumLeverage,0,fee,0,0,0,false,false,entry,now,now,0,null,
            proposal.Assumptions,rules);
        var ledger = new PaperLedgerEvent(Id($"{id}:open"),id,"Open",now,quantity,entry,0,fee,0,-fee,
            "ReviewedSingleTradeSizing",proposal.BookEvidenceId);
        return Apply(portfolio,position,[ledger],now,"Opened");
    }

    public PaperTransition Observe(PaperPortfolio portfolio,Guid positionId,OrderBook book,Guid evidenceId,DateTimeOffset now)
    {
        var position = portfolio.Positions.SingleOrDefault(item => item.PositionId == positionId);
        if (position is null || position.RemainingQuantity == 0) return Reject(portfolio,"PositionNotOpen");
        if (book.AssetId != position.AssetId || book.SourceTime < position.OpenedAt || book.SourceTime > book.EventTime ||
            book.EventTime > book.ReceivedTime || book.ReceivedTime > now || now-book.SourceTime > TimeSpan.FromSeconds(2) ||
            book.SourceTime < position.LastObservedAt || book.UpdateId <= position.LastBookUpdateId ||
            book.Bids.Count == 0 || book.Asks.Count == 0 || book.Bids[0].Price >= book.Asks[0].Price ||
            book.Bids.Concat(book.Asks).Any(level => level.Price <= 0 || level.Quantity <= 0) ||
            book.Bids.Zip(book.Bids.Skip(1),(a,b) => a.Price > b.Price).Any(valid => !valid) ||
            book.Asks.Zip(book.Asks.Skip(1),(a,b) => a.Price < b.Price).Any(valid => !valid))
            return Reject(portfolio,"FreshCausalExitBookRequired");
        var sign = position.Side == TradeSide.Long ? 1m : -1m;
        var levels = position.Side == TradeSide.Long ? book.Bids : book.Asks;
        var price = levels[0].Price;
        var distance = Math.Abs(position.EntryPrice-position.InitialStop);
        var currentR = sign*(price-position.EntryPrice)/distance;
        position = position with { LastPrice = price,LastObservedAt = book.SourceTime,LastBookUpdateId = book.UpdateId,
            MfeR = Math.Max(position.MfeR,currentR),MaeR = Math.Min(position.MaeR,currentR) };
        var events = new List<PaperLedgerEvent> { new(Id($"{positionId}:mark:{evidenceId}"),positionId,"Mark",now,
            0,price,0,0,0,0,"PositionObservation",evidenceId) };
        var stopped = sign*(price-position.StopPrice) <= 0;
        var requested = stopped ? position.RemainingQuantity : 0m;
        var first = !position.FirstTargetDone && currentR >= 1.5m;
        var second = !position.SecondTargetDone && currentR >= 2.5m;
        if (!stopped && (first || second))
        {
            var slice = Math.Floor(position.InitialQuantity*.25m/position.Rules.StepSize)*position.Rules.StepSize;
            var count = (first ? 1 : 0)+(second ? 1 : 0);
            if (slice < position.Rules.MinimumQuantity || slice*price < position.Rules.MinimumNotional ||
                position.RemainingQuantity-slice*count < position.Rules.MinimumQuantity)
            {
                position = position with { FirstTargetDone = position.FirstTargetDone || first,SecondTargetDone = position.SecondTargetDone || second };
                events.Add(new(Id($"{positionId}:minimum:{evidenceId}"),positionId,"ScaleOutSkipped",now,0,null,0,0,0,0,
                    "BelowMinimumSliceOrRunnerQuantity",evidenceId));
            }
            else requested = slice*count;
        }
        if (requested > 0)
        {
            var available = Math.Floor(Math.Min(requested,levels.Sum(level => level.Quantity))/position.Rules.StepSize)*position.Rules.StepSize;
            if (!stopped && available < requested) return Apply(portfolio,position,events,now,"TargetExitDepthInsufficient");
            if (available <= 0) return Apply(portfolio,position,events,now,"StopExitDepthInsufficient");
            decimal remaining = available,cost = 0;
            foreach (var level in levels)
            {
                var fill = Math.Min(remaining,level.Quantity);
                cost += fill*level.Price; remaining -= fill;
                if (remaining == 0) break;
            }
            var fillPrice = cost/available;
            var pnl = sign*(fillPrice-position.EntryPrice)*available;
            var fee = cost*position.Costs.FeePerSide;
            var left = position.RemainingQuantity-available;
            position = position with { RemainingQuantity = left,RealizedPnl = position.RealizedPnl+pnl,
                FeesPaid = position.FeesPaid+fee,ClosedAt = left == 0 ? now : null,
                FirstTargetDone = position.FirstTargetDone || !stopped && first,
                SecondTargetDone = position.SecondTargetDone || !stopped && second };
            events.Add(new(Id($"{positionId}:exit:{evidenceId}"),positionId,"Reduce",now,available,fillPrice,pnl,fee,0,pnl-fee,
                stopped ? "StructuralOrRunnerStop" : first && second ? "Targets1.5RAnd2.5R" : first ? "Target1.5R" : "Target2.5R",evidenceId));
        }
        if (!stopped && position.FirstTargetDone && position.RemainingQuantity > 0)
        {
            var candidate = position.SecondTargetDone ? price-sign*2*distance : position.EntryPrice;
            candidate = position.Side == TradeSide.Long ? Math.Floor(candidate/position.Rules.PriceTick)*position.Rules.PriceTick :
                Math.Ceiling(candidate/position.Rules.PriceTick)*position.Rules.PriceTick;
            position = position with { StopPrice = position.Side == TradeSide.Long ? Math.Max(position.StopPrice,candidate) : Math.Min(position.StopPrice,candidate) };
        }
        return Apply(portfolio,position,events,now,position.RemainingQuantity == 0 ? "Closed" : stopped ? "StopPartialRetryRequired" : "Observed");
    }

    public PaperTransition Funding(PaperPortfolio portfolio,Guid positionId,FundingSettlement settlement,
        IReadOnlyList<PaperLedgerEvent> history,Guid evidenceId,DateTimeOffset now)
    {
        var position = portfolio.Positions.SingleOrDefault(item => item.PositionId == positionId);
        if (position is null || history.Any(item => item.PositionId != positionId)) return Reject(portfolio,"PositionHistoryRequired");
        if (history.Count(item => item.Type == "Open" && item.Quantity == position.InitialQuantity && item.At == position.OpenedAt) != 1)
            return Reject(portfolio,"PositionHistoryRequired");
        if (history.Any(item => item.At > now) ||
            position.InitialQuantity-history.Where(item => item.Type == "Reduce").Sum(item => item.Quantity) != position.RemainingQuantity ||
            history.Sum(item => item.Fee) != position.FeesPaid || history.Sum(item => item.Funding) != position.FundingPnl)
            return Reject(portfolio,"CompletePositionHistoryRequired");
        if (history.Any(item => item.Type == "Reduce" && Math.Abs((item.At-settlement.SettledAt).TotalSeconds) <= 15))
            return Reject(portfolio,"FundingBoundaryAmbiguous");
        var quantity = position.InitialQuantity-history.Where(item => item.Type == "Reduce" && item.At < settlement.SettledAt).Sum(item => item.Quantity);
        if (quantity <= 0) return Reject(portfolio,"NotHeldAtSettlement");
        var flow = SizingEngine.SettledCashflow(position.AssetId,position.Side,quantity,position.OpenedAt,position.ClosedAt,settlement,now);
        if (flow.Status != "Calculated") return Reject(portfolio,flow.Status);
        var id = Id($"{positionId}:funding:{settlement.SettledAt.ToUnixTimeMilliseconds()}:{settlement.RateType}");
        if (history.Any(item => item.EventId == id || item.Type == "Funding" && item.SettlementAt == settlement.SettledAt))
            return Reject(portfolio,"FundingAlreadyApplied");
        var ledger = new PaperLedgerEvent(id,positionId,"Funding",now,quantity,settlement.MarkPrice,0,0,flow.Cashflow!.Value,
            flow.Cashflow.Value,"ActualRegularSettlement",evidenceId,settlement.SettledAt);
        return Apply(portfolio,position with { FundingPnl = position.FundingPnl+flow.Cashflow.Value },[ledger],now,"FundingApplied");
    }

    public PaperTransition ConservativeFundingBoundary(PaperPortfolio portfolio,Guid positionId,FundingSettlement settlement,
        IReadOnlyList<PaperLedgerEvent> history,Guid evidenceId,DateTimeOffset now)
    {
        var strict = Funding(portfolio,positionId,settlement,history,evidenceId,now);
        if (strict.Status is not ("FundingBoundaryAmbiguous" or "BoundaryAmbiguous")) return strict;
        if (now < settlement.SettledAt.AddSeconds(15)) return Reject(portfolio,"FundingBoundaryWindowPending");
        var position = portfolio.Positions.Single(item => item.PositionId == positionId);
        if (settlement.AssetId != position.AssetId || settlement.ReceivedTime > now || settlement.SettledAt > settlement.ReceivedTime ||
            settlement.RateType != "Regular" || settlement.MarkPrice is not > 0 || Math.Abs(settlement.Rate) > 1)
            return Reject(portfolio,"UnsupportedFundingBoundaryEvidence");
        if (history.Any(item => item.Type == "Funding" && item.SettlementAt == settlement.SettledAt))
            return Reject(portfolio,"FundingAlreadyApplied");
        var start = settlement.SettledAt.AddSeconds(-15); var end = settlement.SettledAt.AddSeconds(15);
        var fills = history.Where(item => item.Type is "Open" or "Reduce").OrderBy(item => item.At)
            .ThenBy(item => item.Type == "Open" ? 0 : 1).ToArray();
        decimal held = fills.Where(item => item.At < start).Sum(item => item.Type == "Open" ? item.Quantity : -item.Quantity);
        var minimum = held; var maximum = held;
        foreach (var fill in fills.Where(item => item.At >= start && item.At <= end))
        {
            held += fill.Type == "Open" ? fill.Quantity : -fill.Quantity;
            minimum = Math.Min(minimum,held); maximum = Math.Max(maximum,held);
        }
        var sign = position.Side == TradeSide.Long ? 1m : -1m;
        var quantity = sign*settlement.Rate >= 0 ? maximum : minimum;
        var flow = -sign*Math.Max(0,quantity)*settlement.MarkPrice.Value*settlement.Rate;
        var ledger = new PaperLedgerEvent(Id($"{positionId}:funding:{settlement.SettledAt.ToUnixTimeMilliseconds()}:Regular"),
            positionId,"Funding",now,Math.Max(0,quantity),settlement.MarkPrice,0,0,flow,flow,"ConservativeBoundaryEstimate",evidenceId,
            settlement.SettledAt,"FundingBoundary-Conservative-2.0.0");
        return Apply(portfolio,position with { FundingPnl = position.FundingPnl+flow },[ledger],now,"FundingEstimateApplied");
    }
}
