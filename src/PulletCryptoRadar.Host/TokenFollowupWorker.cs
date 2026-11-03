using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;

namespace PulletCryptoRadar.Host;
public sealed class TokenFollowupWorker(ITokenCheckRepository reports,ITokenFollowupRepository observations,IMarketRuntimeStore market,IAssetRepository history,
    PersistenceQueue queue,TimeProvider time,ILogger<TokenFollowupWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await queue.Ready.Task.WaitAsync(ct);
        while(!ct.IsCancellationRequested)
        {
            IReadOnlyList<TokenCheckReport> batch;
            try{batch=reports.ForFollowup(time.GetUtcNow().AddDays(-8));}
            catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){logger.LogWarning("Token followup report enumeration failed: {Type}",ex.GetType().Name);await Task.Delay(TimeSpan.FromMinutes(1),ct);continue;}
            foreach(var report in batch)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var prior=observations.Get(report.Id);
                    if(prior?.Points.All(p=>p.Status is "已观察" or "无可比基准")==true)continue;
                    var candles=report.AssetId is {} id&&market.Get(id)?.Instrument.Symbol==report.Instrument?
                        ForwardEngine.Hourly(await history.ReadHourlyHistoryAsync(id,400,ct),market.GetCandles(id,5),time.GetUtcNow()):[];
                    observations.Save(TokenFollowupEngine.Evaluate(report,candles,time.GetUtcNow(),prior));
                }
                catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or Microsoft.Data.Sqlite.SqliteException){logger.LogWarning("Token followup {Id} read/write failed: {Type}",report.Id,ex.GetType().Name);}
            }
            await Task.Delay(TimeSpan.FromMinutes(15),ct);
        }
    }
}
