using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PulletCryptoRadar.Host;
using PulletCryptoRadar.Infrastructure;

namespace PulletCryptoRadar.IntegrationTests;

public sealed class FoundationRouterTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "foundation-" + Guid.NewGuid());
    private readonly TestClock clock = new();

    private ProviderRouter Router() => new(Options.Create(new StorageOptions { Directory = directory }), clock);

    private static Task<(string?, DateTimeOffset?, DateTimeOffset?)> Success(string? key) =>
        Task.FromResult<(string?, DateTimeOffset?, DateTimeOffset?)>(("value", null, null));

    [Theory]
    [InlineData(ProviderError.RateLimited)]
    [InlineData(ProviderError.Timeout)]
    [InlineData(ProviderError.SchemaChanged)]
    public async Task ProviderFailureDoesNotBlockAnotherSource(ProviderError expected)
    {
        var router = Router();
        var route = ProviderRouter.Gecko with { KeyVariable = null };
        var failure = await router.Observe<string>(route, _ => throw expected switch
        {
            ProviderError.RateLimited => new HttpRequestException(null, null, HttpStatusCode.TooManyRequests),
            ProviderError.Timeout => new TaskCanceledException(),
            _ => new JsonException()
        }, default);
        Assert.Equal(expected, failure.Error);
        Assert.Equal(ProviderError.Cooldown, (await router.Observe(route, Success, default)).Error);
        Assert.Equal(ProviderError.None, (await router.Observe(ProviderRouter.Dex, Success, default)).Error);
        clock.Now = clock.Now.AddMinutes(16);
        Assert.Equal(ProviderError.None, (await router.Observe(route, Success, default)).Error);
    }

    [Fact]
    public async Task InsightMonthExhaustionBlocksBothCapabilitiesAfterRestart()
    {
        var router = Router();
        var distribution = ProviderRouter.Distribution with { KeyVariable = null };
        var clusters = ProviderRouter.Clusters with { KeyVariable = null };
        await router.Observe<string>(distribution, _ =>
        {
            using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            router.RecordInsightQuota(response);
            throw new HttpRequestException(null, null, response.StatusCode);
        }, default);
        var restarted = Router();
        Assert.Equal(ProviderError.QuotaExhausted, (await restarted.Observe(distribution, Success, default)).Error);
        Assert.Equal(ProviderError.QuotaExhausted, (await restarted.Observe(clusters, Success, default)).Error);
        clock.Now = clock.Now.AddMonths(1);
        Assert.Equal(ProviderError.None, (await restarted.Observe(clusters, Success, default)).Error);
    }

    [Fact]
    public async Task DailyReservationSurvivesRestartAndCorruptLedgerFailsClosed()
    {
        var route = ProviderRouter.Cmc with { KeyVariable = null };
        var router = Router();
        for (int i = 0; i < 16; i++)
            Assert.Equal(ProviderError.None, (await router.Observe(route, Success, default)).Error);
        Assert.Equal(ProviderError.QuotaExhausted, (await Router().Observe(route, Success, default)).Error);
        clock.Now = clock.Now.AddDays(1);
        Assert.Equal(ProviderError.None, (await Router().Observe(route, Success, default)).Error);
        File.WriteAllText(Path.Combine(directory, "foundation-quota.json"), "broken");
        Assert.Equal(ProviderError.QuotaExhausted, (await Router().Observe(route, Success, default)).Error);
    }

    public void Dispose()
    {
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(file);
        Directory.Delete(directory);
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 3, 8, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
