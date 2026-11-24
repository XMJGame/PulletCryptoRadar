using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;

namespace PulletCryptoRadar.Host;

public sealed class ObservationWorker(FoundationStore store, TimeProvider clock,
    ILogger<ObservationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        var runId = Guid.NewGuid();
        store.BeginObservation(runId, clock.GetUtcNow());
        using var probe = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromSeconds(3) };
        var responsive = false;
        void Sample(bool stopped = false)
        {
            var now = clock.GetUtcNow();
            var context = store.MarketContext();
            var contextFresh = context is not null && context.AsOf <= now && now-context.AsOf <= TimeSpan.FromMinutes(3);
            var failedSources = store.SourceHealth(now).Count(item => item.Failure != CollectionFailure.None);
            // Preserve the storage format shared with historical observations.
            store.RecordObservation(runId, new(now,true,contextFresh,0,0,failedSources,0,0,0,"ResearchOnly",responsive),stopped);
        }
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var response = await probe.GetAsync("http://localhost:5218/health",token);
                    responsive = response.IsSuccessStatusCode;
                }
                catch (Exception error) when (!token.IsCancellationRequested && error is HttpRequestException or TaskCanceledException)
                {
                    responsive = false;
                }
                try { Sample(); }
                catch (Exception error) { logger.LogError("Observation heartbeat failed ({ErrorType})",error.GetType().Name); }
                await Task.Delay(TimeSpan.FromMinutes(1),clock,token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            try { Sample(true); }
            catch (Exception error) { logger.LogError("Observation stop record failed ({ErrorType})",error.GetType().Name); }
        }
    }
}
