using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Application;

public sealed class CapitalFeatureEngine
{
    public CapitalFeatures Calculate(MarketView market, CapitalInput input, DateTimeOffset now)
    {
        var mapping = input.Mapping; var snapshot = input.Snapshot?.CoinGeckoId == mapping?.CoinGeckoId ? input.Snapshot : null;
        var trusted = input.CatalogContainsId && mapping?.Status is MappingStatus.ContractVerified or MappingStatus.ManualVerified;
        FeatureValue<decimal> Field(decimal? value, string source = "CoinGecko USD") => new(value,
            value is null ? DataStatus.Missing : value < 0 || snapshot!.SourceTime > now || snapshot.ReceivedTime > now ? DataStatus.Invalid
            : now - snapshot.SourceTime > TimeSpan.FromMinutes(60) ? DataStatus.Stale : DataStatus.Valid, snapshot?.SourceTime, source);
        var cap = Field(snapshot?.MarketCapUsd); var oi = market.Features.Derivatives.OpenInterestUsd;
        var capUsable = trusted && cap.Status == DataStatus.Valid && cap.Value > 0;
        var oiUsable = oi.Status == DataStatus.Valid && oi.Value >= 0 && oi.SourceTime <= now && now - oi.SourceTime <= TimeSpan.FromMinutes(11);
        var volumeUsable = market.Volume24h >= 0 && market.PriceSourceTime <= now && market.PriceReceivedTime <= now
            && now - market.PriceSourceTime <= TimeSpan.FromSeconds(30);
        var reason = !input.CatalogContainsId && mapping?.CoinGeckoId is not null ? "所选 ID 不在当前资产目录中，需复核身份" : mapping?.Reason ?? "资产映射待确认";
        var ratioSource = trusted ? "Binance USD / verified CoinGecko market cap; no contract multiplier applied twice" : "Verified identity required";
        FeatureValue<decimal> Ratio(bool usable, decimal? numerator, DateTimeOffset? numeratorTime)
        {
            if (!usable) return FeatureValue<decimal>.Missing(ratioSource);
            try { return new(numerator / cap.Value, DataStatus.Valid, new[] { cap.SourceTime!.Value, numeratorTime!.Value }.Min(), ratioSource); }
            catch (OverflowException) { return new(null, DataStatus.Invalid, cap.SourceTime, ratioSource + "; decimal overflow"); }
        }
        return new()
        {
            CoinGeckoId = mapping?.CoinGeckoId, MappingStatus = mapping?.Status ?? MappingStatus.Unknown,
            MappingRevision = mapping?.Revision ?? 0, TrustedIdentity = trusted, MappingReason = reason,
            MarketCapUsd = cap, FdvUsd = Field(snapshot?.FdvUsd), Volume24hUsd = Field(snapshot?.Volume24hUsd, "CoinGecko spot aggregate USD"),
            CirculatingSupply = Field(snapshot?.CirculatingSupply, "CoinGecko token supply"), TotalSupply = Field(snapshot?.TotalSupply, "CoinGecko token supply"),
            MaxSupply = Field(snapshot?.MaxSupply, "CoinGecko token supply"), AthUsd = Field(snapshot?.AthUsd),
            OiMarketCapRatio = Ratio(capUsable && oiUsable, oi.Value, oi.SourceTime),
            VolumeMarketCapRatio = Ratio(capUsable && volumeUsable, market.Volume24h, market.PriceSourceTime)
        };
    }
}
