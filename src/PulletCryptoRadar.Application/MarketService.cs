using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Application;

// The UI and REST endpoints share this application read service; neither accesses repositories.
public sealed class MarketService(IMarketRuntimeStore runtime, IProviderHealthStore health)
{
    public IReadOnlyList<MarketView> GetAssets() => runtime.GetAll();
    public MarketView? GetAsset(int id) => runtime.Get(id);
    public IReadOnlyList<Candle> GetHistory(int id, int intervalMinutes) => runtime.GetCandles(id, intervalMinutes);
    public IReadOnlyList<ProviderHealth> GetHealth() => health.GetAll();
}
