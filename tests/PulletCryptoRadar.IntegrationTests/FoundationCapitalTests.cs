using PulletCryptoRadar.Host;

namespace PulletCryptoRadar.IntegrationTests;

public sealed class FoundationCapitalTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(100, 115, 0, "Confirmed", "High", 100)]
    [InlineData(100, 116, 0, "Conflict", "Low", 100)]
    [InlineData(100, 116, 31, "SingleSource", "Limited", 116)]
    [InlineData(0, 116, 0, "SingleSource", "Limited", 116)]
    [InlineData(100, 0, -1, "Stale", "Unknown", null)]
    [InlineData(0, 0, 0, "Missing", "Unknown", null)]
    public void ResolvesMarketCapitalWithoutAveraging(int primary, int secondary, int primaryAge,
        string status, string confidence, int? canonical)
    {
        var result = MarketCapitalResolver.Resolve(Observation("Gecko", primary, primaryAge),
            Observation("CMC", secondary, 0), Now);
        Assert.Equal(status, result.Status);
        Assert.Equal(confidence, result.Confidence);
        Assert.Equal((decimal?)canonical, result.CanonicalUsd);
    }

    private static DataObservation<MarketCapital> Observation(string provider, int value, int age) =>
        new(provider, DataCapability.MarketCapital, Now.AddMinutes(-age), Now, TimeQuality.ProviderTimestamp,
            value == 0 ? ProviderError.NoData : ProviderError.None, value == 0 ? null : new(value, null, null));
}
