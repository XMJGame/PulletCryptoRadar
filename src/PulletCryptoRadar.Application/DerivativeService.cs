using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Application;

public sealed class DerivativeService(IMarketRuntimeStore markets, IDerivativeRuntimeStore derivatives, IProviderHealthStore health)
{
    public IReadOnlyList<MarketView> GetAssets() => markets.GetAll();
    public string? GetCooldown() => health.GetAll().FirstOrDefault(x => x.Name == "Binance Cooldown")?.Error;
    public DerivativeRuntimeSnapshot? GetRaw(int assetId, int limit = 100)
    {
        var m = markets.Get(assetId); if (m is null) return null;
        var raw = derivatives.Get(m.Instrument.Symbol); limit = Math.Clamp(limit, 1, 500);
        return raw with { Series = raw.Series.ToDictionary(x => x.Key, x => (IReadOnlyList<DerivativeSample>)x.Value.TakeLast(limit).ToArray()),
            Liquidations = raw.Liquidations.TakeLast(limit).ToArray() };
    }
}
