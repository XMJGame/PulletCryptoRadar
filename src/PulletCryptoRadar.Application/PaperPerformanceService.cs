using PulletCryptoRadar.Core;
namespace PulletCryptoRadar.Application;

public sealed class PaperPerformanceService(PaperService paper,IPaperRepository repository,TimeProvider time)
{
    private readonly object gate=new();
    private DateTimeOffset cachedAt=DateTimeOffset.MinValue;
    private IReadOnlyList<PaperEquityPoint> points=[];
    private readonly Dictionary<DateOnly,IReadOnlyList<PaperEquitySample>> dates=[];
    private DateTimeOffset? first;
    public async Task<PaperPerformance> GetAsync(DateOnly? requested,CancellationToken ct)
    {
        var context=await paper.ReviewContextAsync(ct);
        return ForContext(requested,time.GetUtcNow(),context.State,context.Trades,context.Funding);
    }
    public PaperPerformance ForContext(DateOnly? requested,DateTimeOffset now,PaperState state,IReadOnlyList<PaperTrade> trades,IReadOnlyList<QuantFundingEvent> funding)
    {
        var today=PaperEngine.Day(now);var date=requested??today;
        if(date>today||date<today.AddDays(-60))throw new ArgumentException("请选择最近60天内且不晚于今天的上海日期");
        var current=PaperEquityRecorder.Capture(state,trades,funding,now);
        lock(gate)
        {
            if(now<cachedAt||now-cachedAt>=TimeSpan.FromMinutes(1))
            {points=repository.EquityPoints(now.AddDays(-90),now);first=repository.FirstEquitySampleAt();dates.Clear();cachedAt=now;}
            if(!dates.TryGetValue(date,out var rows))
            {rows=repository.EquitySamples(QuantDailyEngine.Start(date),QuantDailyEngine.Start(date).AddDays(1).AddMinutes(1));dates[date]=rows;}
            return PaperPerformanceEngine.Build(date,now,current,points,rows,first);
        }
    }
}
