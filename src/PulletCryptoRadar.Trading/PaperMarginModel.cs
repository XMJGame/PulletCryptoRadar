using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Trading;

public static class PaperMarginModel
{
    public static bool StopHasBuffer(SizingProposal proposal,PaperRuntimePolicy policy)
    {
        policy.Validate();
        if (proposal.Quantity is not > 0 || proposal.AverageEntry is not > 0 || proposal.Margin is not > 0) return false;
        var price = proposal.AverageEntry.Value;
        var marginPerUnit = proposal.Margin.Value/proposal.Quantity.Value;
        var liquidation = proposal.Side == TradeSide.Long ? (price-marginPerUnit)/(1-policy.MaintenanceFraction) :
            (price+marginPerUnit)/(1+policy.MaintenanceFraction);
        var sign = proposal.Side == TradeSide.Long ? 1m : -1m;
        var adverseStop = proposal.StructuralStop*(1-sign*proposal.Assumptions.StopSlippageFraction);
        return sign*(adverseStop-liquidation) > price*policy.LiquidationBufferFraction;
    }

    public static bool NeedsSafetyExit(PaperPosition position,MarketMark mark,PaperRuntimePolicy policy)
    {
        policy.Validate();
        var sign = position.Side == TradeSide.Long ? 1m : -1m;
        var remainingMargin = position.InitialMargin*position.RemainingQuantity/position.InitialQuantity;
        var marginEquity = remainingMargin+sign*(mark.Price-position.EntryPrice)*position.RemainingQuantity+
            Math.Min(0,position.FundingPnl)-position.FeesPaid;
        return marginEquity <= mark.Price*position.RemainingQuantity*(policy.MaintenanceFraction+policy.LiquidationBufferFraction);
    }
}
