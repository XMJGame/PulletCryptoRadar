using System.Text.Json;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;

namespace PulletCryptoRadar.Tests;

public sealed class DepthCollectionTests
{
    private static readonly DateTimeOffset Now = new(2026,11,3,8,0,0,TimeSpan.Zero);
    private static MarketInstrument[] Universe() => [new("AUSDT","A",Now),new("BUSDT","B",Now),new("CUSDT","C",Now)];

    [Fact]
    public void RotationPersistsAcrossRestartAndDoesNotStarveLaterSymbols()
    {
        InStore((store,folder) =>
        {
            Assert.Equal(new[] { "AUSDT","BUSDT" },store.ReserveDepthBatch(Universe(),2,Now).Select(item => item.Symbol));
            store = new FoundationStore(folder);
            Assert.Equal("CUSDT",Assert.Single(store.ReserveDepthBatch(Universe(),2,Now.AddMinutes(1))).Symbol);
            Assert.Empty(store.ReserveDepthBatch(Universe(),2,Now.AddMinutes(2)));
            Assert.Equal(new[] { "AUSDT","BUSDT" },store.ReserveDepthBatch(Universe(),2,Now.AddMinutes(16)).Select(item => item.Symbol));
            Assert.Equal("CUSDT",Assert.Single(store.ReserveDepthBatch(Universe(),2,Now.AddMinutes(17))).Symbol);
            var changedUniverse = new[] { Universe()[2],new MarketInstrument("DUSDT","D",Now) };
            Assert.Equal("DUSDT",Assert.Single(store.ReserveDepthBatch(changedUniverse,2,Now.AddMinutes(18))).Symbol);
            using var status = JsonDocument.Parse(JsonSerializer.Serialize(store.DepthStatus(Now)));
            Assert.Equal(4,status.RootElement.GetProperty("reservedSymbols").GetInt32());
        });
    }

    [Fact]
    public void SupportsExchangeIssuedChineseSymbolsWithoutAcceptingUrlFragments()
    {
        InStore((store,_) =>
        {
            var chinese = new MarketInstrument("币安人生USDT","币安人生",Now);
            Assert.Equal(chinese,Assert.Single(store.ReserveDepthBatch([chinese],2,Now)));
            Assert.Throws<ArgumentException>(() => store.ReserveDepthBatch([new("BTCUSDT?symbol=ETHUSDT","BTC",Now)],2,Now));
        });
    }

    [Fact]
    public void ConcurrentStoresCannotReserveTheSameSymbolAtTheSameTime()
    {
        InStore((store,folder) =>
        {
            var batches = Task.WhenAll(Enumerable.Range(0,4).Select(_ => Task.Run(() =>
                new FoundationStore(folder).ReserveDepthBatch(Universe(),2,Now)))).GetAwaiter().GetResult();
            var symbols = batches.SelectMany(batch => batch).Select(item => item.Symbol).ToArray();
            Assert.Equal(3,symbols.Length);
            Assert.Equal(3,symbols.Distinct().Count());
            Assert.Throws<ArgumentException>(() => store.ReserveDepthBatch(Universe(),5,Now));
            Assert.Throws<ArgumentException>(() => store.ReserveDepthBatch([Universe()[0],Universe()[0]],2,Now));
        });
    }

    private static void InStore(Action<FoundationStore,string> action)
    {
        var folder = Path.Combine(Path.GetTempPath(),"v2-depth-"+Guid.NewGuid());
        try
        {
            var store = new FoundationStore(folder);
            store.Initialize(Now);
            action(store,folder);
        }
        finally
        {
            foreach (var file in Directory.GetFiles(folder)) File.Delete(file);
            Directory.Delete(folder);
        }
    }
}
