using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Application;

public sealed class EvaluationService(IEvaluationRepository repository, IEvaluationPersistence persistence,
    SignalEvaluationEngine evaluation, ReplayEngine replay, EvaluationOptions options, TimeProvider time)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public EvaluationReport? GetLatest() => repository.ReadLatest();
    public ReplayAvailability GetAvailability() => repository.GetAvailability();
    public IReadOnlyList<ReplayAuditResult> Audit(int limit) => repository.ReadAuditInputs(Math.Clamp(limit, 1, 200))
        .Select(input =>
        {
            try { return replay.Audit(input); }
            catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException or ArgumentException)
            { return new ReplayAuditResult(input.Event.Id, input.Event.Symbol, input.Event.Time, input.Event.StrategyVersion, "InvalidInput", input.Event.Assessment?.Score, null, ex.Message); }
        }).ToArray();
    public async Task<EvaluationReport> RefreshAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var now = time.GetUtcNow(); var from = now.AddDays(-options.LookbackDays);
            var data = await Task.Run(() => repository.ReadData(from, now, options.MaximumSignals, ct), ct);
            var report = evaluation.Build(data, from, now, options.PrecisionK);
            await persistence.SaveAsync(report, ct); return report;
        }
        finally { _gate.Release(); }
    }
    public async Task<ReplayResult> ReplayAsync(ReplayRequest request, CancellationToken ct)
    {
        if (request.From >= request.To || request.To > time.GetUtcNow() || request.To - request.From > TimeSpan.FromHours(6)
            || string.IsNullOrWhiteSpace(request.Symbol) || request.Symbol.Length > 40) throw new ArgumentException("请选择过去 6 小时以内的区间和有效合约符号");
        await _gate.WaitAsync(ct);
        try { return await Task.Run(() => replay.Run(repository.ReadFrames(request.From, request.To, ct), request, ct), ct); }
        finally { _gate.Release(); }
    }
}
