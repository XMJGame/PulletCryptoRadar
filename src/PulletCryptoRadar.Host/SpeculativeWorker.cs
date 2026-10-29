using Microsoft.Extensions.Options;
using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;
using PulletCryptoRadar.Providers.Market;
using PulletCryptoRadar.Scanner;

namespace PulletCryptoRadar.Host;

public sealed class SpeculativeWorker(SpeculativeService service,PersistenceQueue queue,SpeculativeOptions options,
    IProviderHealthStore health,TimeProvider time,ILogger<SpeculativeWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await queue.Ready.Task.WaitAsync(ct);
        while(!ct.IsCancellationRequested)
        {
            try{var s=await service.ScanAsync(ct);if(s.Rows.Count>0)health.Success("Speculative Radar",freshnessSeconds:options.IntervalSeconds+60);}
            catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
            catch(Exception ex){health.Failure("Speculative Radar",ex.Message);logger.LogWarning(ex,"Speculative scan failed");}
            await Task.Delay(TimeSpan.FromSeconds(service.Latest()?.Rows.Count>0?options.IntervalSeconds:2),time,ct);
        }
    }
}
public sealed class AlternativeWorker(IAlternativeMarketProvider provider,IAlternativeMarketStore alternative,IMarketRuntimeStore market,
    PersistenceQueue queue,IOptions<AlternativeOptions> options,IProviderHealthStore health,TimeProvider time,ILogger<AlternativeWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if(!options.Value.Enabled)return;await queue.Ready.Task.WaitAsync(ct);
        while(!ct.IsCancellationRequested)
        {
            var assets=market.GetAll();if(assets.Count==0){await Task.Delay(TimeSpan.FromSeconds(2),time,ct);continue;}
            var bases=assets.OrderByDescending(m=>m.Instrument.Symbol is "ARIAUSDT" or "BTCUSDT" or "ETHUSDT")
                .ThenByDescending(m=>Math.Abs(m.Features.PriceRobustZ5m.Value??0)+Math.Max(0,m.Features.VolumeRobustZ.Value??0))
                .Select(m=>m.Instrument.BaseAsset).Distinct(StringComparer.Ordinal).Take(options.Value.DeepCandidates).ToArray();
            try{var quotes=await provider.GetGateAsync(bases,ct);alternative.Set(quotes);if(quotes.Count>0)health.Success("Gate Perpetual",freshnessSeconds:options.Value.PollSeconds+120);}
            catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
            catch(Exception ex){health.Failure("Gate Perpetual",ex.Message);logger.LogWarning("Gate collection: {Error}",ex.Message);}
            // Coinbase is an independent spot reference when Binance is unavailable. It cannot supply perpetual OI.
            try
            {
                var quotes=await provider.GetCoinbaseAsync(bases,ct);alternative.Set(quotes);
                if(quotes.Count>0)health.Success("Coinbase Spot Reference",freshnessSeconds:options.Value.PollSeconds+120);
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
            catch(Exception ex){health.Failure("Coinbase Spot Reference",ex.Message);logger.LogWarning("Coinbase collection: {Error}",ex.Message);}
            await Task.Delay(TimeSpan.FromSeconds(options.Value.PollSeconds),time,ct);
        }
    }
}
