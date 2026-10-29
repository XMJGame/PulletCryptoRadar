using System.Text.Json;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Scanner;

namespace PulletCryptoRadar.Application;

public sealed class VolatilityRadarService(IMarketRuntimeStore runtime, IRadarRepository repository, IRadarPersistence persistence,
    UpMoveDetector up, DownMoveDetector down, MarketRegimeDetector regime, EpisodeEngine episodes,
    VolatilityOptions options, TimeProvider time, AlertOptions? alertOptions = null, EvaluationOptions? evaluationOptions = null) : IRadarSnapshotSource
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<MoveEpisode> _active = [];
    private RadarSnapshot? _snapshot;
    private bool _restored;
    public RadarSnapshot? GetSnapshot() => Volatile.Read(ref _snapshot);
    public Task<IReadOnlyList<RadarEvent>> GetEventsAsync(int? assetId, int limit, CancellationToken ct)
        => repository.ReadEventsAsync(assetId, Math.Clamp(limit, 1, 200), ct);
    public Task<IReadOnlyList<MoveEpisode>> GetEpisodesAsync(int? assetId, int limit, CancellationToken ct)
        => repository.ReadEpisodesAsync(assetId, Math.Clamp(limit, 1, 200), ct);

    public async Task<RadarSnapshot> ScanAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!_restored)
            {
                var restore = await repository.RestoreAsync(ct);
                _active = restore.LatestEpisodes ?? restore.ActiveEpisodes; episodes.RestoreId(restore.MaxEpisodeId); _restored = true;
            }
            var now = time.GetUtcNow();
            var assets = runtime.GetAll();
            // A transient empty cache at startup is not evidence of exchange delisting.
            if (assets.Count == 0)
            {
                var empty = new RadarSnapshot(Guid.Empty, now, regime.Detect(assets, now), [],
                    _active.Where(e => e.State != EpisodeState.Closed).Select(e => e with { DataStatus = DataStatus.Missing }).ToArray(), [], JsonSerializer.Serialize(options));
                Volatile.Write(ref _snapshot, empty); return empty;
            }
            var scanId = Guid.NewGuid();
            var observations = assets.Select(m => new RadarObservation(m, up.Detect(m, now), down.Detect(m, now))).ToArray();
            var plan = episodes.Advance(_active, observations, scanId, now);
            var recent = plan.Events.Concat(_snapshot?.RecentEvents ?? []).OrderByDescending(x => x.Time).Take(50).ToArray();
            var snapshot = new RadarSnapshot(scanId, now, regime.Detect(assets, now), plan.Observations, plan.Active, recent, JsonSerializer.Serialize(options));
            await persistence.SaveAsync(new(snapshot, plan.Changed, plan.Events)
            {
                ReplayFrame = new(scanId, now, assets, snapshot.ParametersJson, JsonSerializer.Serialize(alertOptions ?? new AlertOptions()), ReplayEngine.CaptureVersion),
                ReplayRetentionDays = evaluationOptions?.ReplayRetentionDays ?? 7
            }, ct);
            _active = plan.Tracking;
            Volatile.Write(ref _snapshot, snapshot);
            return snapshot;
        }
        finally { _gate.Release(); }
    }
}
