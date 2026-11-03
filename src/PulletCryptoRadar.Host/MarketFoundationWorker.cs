namespace PulletCryptoRadar.Host;

public sealed class MarketFoundationWorker(MarketFoundation foundation, IConfiguration configuration,
    ILogger<MarketFoundationWorker> logger, TimeProvider clock) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue<bool>("DataFoundation:AriaShadowEnabled")) return;
        var interval = TimeSpan.FromHours(6);
        var duration = TimeSpan.FromDays(7);
        if (!DateTimeOffset.TryParse(configuration["DataFoundation:StartedAt"], out var start)) return;
        var end = start + duration;
        while (clock.GetUtcNow() < end)
        {
            try
            {
                var result = await foundation.Collect("aria-ai", "binance-smart-chain", true, stoppingToken, scheduled: true);
                if (result is IStatusCodeHttpResult { StatusCode: >= 400 } rejected)
                {
                    logger.LogInformation("ARIA shadow not collected yet ({Status})", rejected.StatusCode);
                    await Task.Delay(TimeSpan.FromMinutes(5), clock, stoppingToken);
                    continue;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                logger.LogWarning("ARIA shadow collection failed ({ErrorType})", error.GetType().Name);
            }
            await Task.Delay(interval, clock, stoppingToken);
        }
    }
}
