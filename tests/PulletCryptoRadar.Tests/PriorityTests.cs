using PulletCryptoRadar.Core;
using PulletCryptoRadar.Features;
using PulletCryptoRadar.Infrastructure;

namespace PulletCryptoRadar.Tests;

public sealed class PriorityTests
{
    private static readonly DateTimeOffset Now = new(2026,11,3,8,0,0,TimeSpan.Zero);
    [Fact]
    public void SelectsLiquidRisingAndFallingContractsAndRejectsStaleFutureAndUnknown()
    {
        var universe = new[] { "AUSDT","BUSDT","CUSDT","DUSDT","EUSDT","FUSDT","BTCUSDT" }
            .Select(symbol => new MarketInstrument(symbol,symbol[..^4],Now)).ToArray();
        MarketTicker Ticker(string symbol,decimal change,decimal volume = 10_000_000,DateTimeOffset? at = null) =>
            new(symbol,change,volume,at ?? Now);
        var selected = new PrioritySelection().Select(universe,[Ticker("AUSDT",20),Ticker("BUSDT",10),Ticker("CUSDT",50,1),
            Ticker("DUSDT",-20),Ticker("EUSDT",-10,at:Now.AddMinutes(-2)),Ticker("FUSDT",40,at:Now.AddSeconds(1)),
            Ticker("BTCUSDT",100),Ticker("UNKNOWNUSDT",80),Ticker("AUSDT",20)],Now);
        Assert.Equal(new[] { "AUSDT","BUSDT","DUSDT" },selected.Select(item => item.Symbol));
        Assert.Equal("LiquidFallingMover",selected[^1].Reason);
    }

    [Fact]
    public void RefreshPreservesFairReservationOrderAndExpirySurvivesRestart()
    {
        var folder = Path.Combine(Path.GetTempPath(),"v2-priority-"+Guid.NewGuid());
        try
        {
            var store = new FoundationStore(folder);
            store.Initialize(Now);
            var candidates = new[] { "AUSDT","BUSDT","CUSDT","DUSDT" }
                .Select(symbol => new PriorityCandidate(symbol,10,10_000_000,Now,"test")).ToArray();
            store.RefreshPriority(candidates,Now);
            Assert.Equal("AUSDT",store.ReservePriority(Now)!.Symbol);
            store = new FoundationStore(folder);
            Assert.Equal("BUSDT",store.ReservePriority(Now.AddMinutes(1))!.Symbol);
            var refreshed = candidates.Select(item => item with { SourceTime = Now.AddMinutes(2) }).ToArray();
            store.RefreshPriority(refreshed,Now.AddMinutes(2));
            Assert.Equal("CUSDT",store.ReservePriority(Now.AddMinutes(2))!.Symbol);
            Assert.Equal("DUSDT",store.ReservePriority(Now.AddMinutes(3))!.Symbol);
            Assert.Null(store.ReservePriority(Now.AddMinutes(6)));
            Assert.Empty(store.Priority(Now.AddMinutes(6)));
            store.RefreshPriority([],Now.AddMinutes(7));
            Assert.Empty(store.Priority(Now.AddMinutes(7)));
        }
        finally
        {
            foreach (var file in Directory.GetFiles(folder)) File.Delete(file);
            Directory.Delete(folder);
        }
    }
}
