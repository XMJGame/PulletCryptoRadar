using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Trading;

public sealed class SizingEngine
{
    public SizingProposal Evaluate(AssetIdentity asset, EntrySetup setup, ExecutionEvidence evidence, ContractRules? rules,
        decimal equity, decimal availableCash, TradingLimits limits, SizingAssumptions assumptions, DateTimeOffset now)
    {
        limits.Validate();
        if (equity <= 0 || availableCash < 0 || string.IsNullOrWhiteSpace(assumptions.Version) ||
            assumptions.FeePerSide is < 0 or > .01m || assumptions.StopSlippageFraction is < 0 or > .05m ||
            assumptions.FundingReserveFraction is < 0 or > .05m || assumptions.MaximumSpreadFraction is <= 0 or > .05m ||
            assumptions.MaximumEntrySlippageFraction is <= 0 or > .05m)
            throw new ArgumentException("Invalid sizing inputs.");
        var reasons = new List<string>();
        var budget = equity*limits.RiskPerTradeFraction;
        decimal? quantity = null, average = null, notional = null, margin = null, loss = null, spread = null, slippage = null;
        SizingProposal Result() => new("Sizing-2.0.0",asset.AssetId,setup.Side,now,false,quantity,average,notional,margin,loss,budget,
            spread,slippage,reasons.Concat(["PortfolioStateUnverified","LiquidationModelUnverified","FeeScheduleAssumed",
                "UpcomingFundingScheduleUnverified"]).ToArray(),assumptions.Version,evidence.Book.Evidence.Id,evidence.Funding.Evidence.Id,
            setup.SetupId,setup.Strategy,setup.StructuralStop,assumptions,rules,limits,equity,availableCash);
        if (setup.AssetId != asset.AssetId || evidence.AssetId != asset.AssetId || setup.Phase != SetupPhase.PatternConfirmed ||
            setup.ConfirmedAt is null || setup.ConfirmedAt > now || now-setup.ConfirmedAt > TimeSpan.FromMinutes(1) ||
            setup.ConfirmationBarClosedAt is null || setup.ConfirmationBarClosedAt > setup.ConfirmedAt || now >= setup.ExpiresAt)
        {
            reasons.Add("ConfirmedSetupMissingOrExpired");
            return Result();
        }
        var book = evidence.Book.OrderBook;
        if (evidence.AsOf > now || evidence.AsOf < evidence.Book.Evidence.ReceivedTime || evidence.AsOf < evidence.Funding.Evidence.ReceivedTime ||
            evidence.Book.Evidence.Provider != "Binance" || evidence.Book.Evidence.Symbol != asset.BinanceSymbol ||
            evidence.Book.Evidence.Failure != CollectionFailure.None || book is null || book.AssetId != asset.AssetId ||
            book.SourceTime < setup.ConfirmedAt || book.SourceTime > book.EventTime || book.EventTime > book.ReceivedTime ||
            book.ReceivedTime > evidence.Book.Evidence.ReceivedTime || evidence.Book.Evidence.ReceivedTime > now ||
            now-book.SourceTime > TimeSpan.FromSeconds(2) || book.Bids.Count == 0 || book.Asks.Count == 0 ||
            book.Bids.Concat(book.Asks).Any(level => level.Price <= 0 || level.Quantity <= 0) ||
            book.Bids.Zip(book.Bids.Skip(1),(a,b) => a.Price > b.Price).Any(valid => !valid) ||
            book.Asks.Zip(book.Asks.Skip(1),(a,b) => a.Price < b.Price).Any(valid => !valid) ||
            book.Bids[0].Price >= book.Asks[0].Price)
        {
            reasons.Add("FreshPostConfirmationBookUnavailable");
            return Result();
        }
        if (rules is null || rules.Symbol != asset.BinanceSymbol || rules.StepSize <= 0 || rules.PriceTick <= 0 ||
            rules.MinimumQuantity <= 0 || rules.MaximumQuantity < rules.MinimumQuantity || rules.MinimumNotional <= 0)
        {
            reasons.Add("MarketContractRulesUnavailable");
            return Result();
        }
        var funding = evidence.Funding.FundingSettlements;
        var lastFunding = funding?.Where(item => item.RateType == "Regular").OrderByDescending(item => item.SettledAt).FirstOrDefault();
        if (evidence.Funding.Evidence.Provider != "Binance" || evidence.Funding.Evidence.Symbol != asset.BinanceSymbol ||
            evidence.Funding.Evidence.Failure != CollectionFailure.None || evidence.Funding.Evidence.ReceivedTime > now ||
            now-evidence.Funding.Evidence.ReceivedTime > TimeSpan.FromMinutes(10) || lastFunding is null ||
            funding!.Any(item => item.AssetId != asset.AssetId || item.SettledAt > item.ReceivedTime || item.ReceivedTime > now) ||
            lastFunding.MarkPrice is not > 0 || now-lastFunding.SettledAt > TimeSpan.FromHours(12))
        {
            reasons.Add("SettledFundingEvidenceUnavailable");
            return Result();
        }
        var sign = setup.Side == TradeSide.Long ? 1m : -1m;
        if (sign*lastFunding.Rate > .003m)
        {
            reasons.Add("AdverseSettledFundingAboveResearchLimit");
            return Result();
        }
        var best = setup.Side == TradeSide.Long ? book.Asks[0].Price : book.Bids[0].Price;
        spread = (book.Asks[0].Price-book.Bids[0].Price)/((book.Asks[0].Price+book.Bids[0].Price)/2);
        if (spread > assumptions.MaximumSpreadFraction)
        {
            reasons.Add("SpreadTooWide");
            return Result();
        }
        var stop = setup.Side == TradeSide.Long ? Math.Floor(setup.StructuralStop/rules.PriceTick)*rules.PriceTick :
            Math.Ceiling(setup.StructuralStop/rules.PriceTick)*rules.PriceTick;
        var adverseStop = stop*(1-sign*assumptions.StopSlippageFraction);
        var adverseEntry = best*(1+sign*assumptions.MaximumEntrySlippageFraction);
        if (stop <= 0 || adverseEntry <= 0 || sign*(best-stop) <= 0)
        {
            reasons.Add("StructuralStopWrongSideOfMarket");
            return Result();
        }
        decimal UnitLoss(decimal entry) => sign*(entry-adverseStop)+(entry+adverseStop)*assumptions.FeePerSide+
            entry*assumptions.FundingReserveFraction;
        var unitRisk = UnitLoss(adverseEntry);
        var cashBoundPrice = best*(1+assumptions.MaximumEntrySlippageFraction);
        var cap = Math.Min(budget/unitRisk,Math.Min(limits.MaximumMarginPerPosition*limits.MaximumLeverage/cashBoundPrice,
            availableCash/(cashBoundPrice*(1m/limits.MaximumLeverage+assumptions.FeePerSide+assumptions.FundingReserveFraction))));
        var plannedQuantity = Math.Floor(Math.Min(cap,rules.MaximumQuantity)/rules.StepSize)*rules.StepSize;
        if (plannedQuantity < rules.MinimumQuantity || plannedQuantity <= 0)
        {
            reasons.Add("QuantityBelowExchangeMinimum");
            return Result();
        }
        decimal remaining = plannedQuantity, cost = 0;
        foreach (var level in setup.Side == TradeSide.Long ? book.Asks : book.Bids)
        {
            var filled = Math.Min(remaining,level.Quantity);
            cost += filled*level.Price;
            remaining -= filled;
            if (remaining == 0) break;
        }
        if (remaining > 0)
        {
            reasons.Add("DisplayedDepthInsufficient");
            return Result();
        }
        var entryPrice = cost/plannedQuantity;
        slippage = sign*(entryPrice/best-1);
        if (slippage > assumptions.MaximumEntrySlippageFraction)
        {
            reasons.Add("EntrySlippageTooLarge");
            return Result();
        }
        if (cost < rules.MinimumNotional)
        {
            reasons.Add("NotionalBelowExchangeMinimum");
            return Result();
        }
        var plannedRisk = plannedQuantity*UnitLoss(entryPrice);
        var plannedMargin = cost/limits.MaximumLeverage;
        if (plannedRisk > budget || plannedMargin > limits.MaximumMarginPerPosition ||
            plannedMargin+cost*(assumptions.FeePerSide+assumptions.FundingReserveFraction) > availableCash)
        {
            reasons.Add("CostOrCashBudgetExceeded");
            return Result();
        }
        quantity = plannedQuantity; average = entryPrice; notional = cost; margin = plannedMargin; loss = plannedRisk;
        reasons.Add("MarketAndSingleTradeSizingChecksPassed");
        return Result();
    }

    public static FundingCashflow SettledCashflow(Guid assetId,TradeSide side,decimal quantity,DateTimeOffset enteredAt,
        DateTimeOffset? exitedAt,FundingSettlement settlement,DateTimeOffset asOf)
    {
        if (quantity <= 0 || enteredAt > asOf || exitedAt < enteredAt || settlement.AssetId != assetId ||
            settlement.ReceivedTime > asOf || settlement.SettledAt > settlement.ReceivedTime || Math.Abs(settlement.Rate) > 1)
            return new("InvalidEvidence",null);
        if (settlement.RateType != "Regular" || settlement.MarkPrice is not > 0) return new("UnsupportedOrMissingMark",null);
        if (Math.Abs((enteredAt-settlement.SettledAt).TotalSeconds) <= 15 ||
            exitedAt is { } exit && Math.Abs((exit-settlement.SettledAt).TotalSeconds) <= 15)
            return new("BoundaryAmbiguous",null);
        if (enteredAt > settlement.SettledAt || exitedAt < settlement.SettledAt) return new("NotHeldAtSettlement",0);
        return new("Calculated",-(side == TradeSide.Long ? 1 : -1)*quantity*settlement.MarkPrice*settlement.Rate);
    }
}
