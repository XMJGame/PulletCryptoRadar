using PulletCryptoRadar.Core;
namespace PulletCryptoRadar.Application;

public static class QuantExitTimingEngine
{
    public static QuantExitTiming Build(PaperTrade trade)
    {
        var observation=trade.Quant?.ExitObservation;var received=trade.ExitQuote?.Book?.ReceivedAt;var computed=trade.Quant?.ExitComputedAt??trade.ClosedAt;
        decimal? Elapsed(DateTimeOffset? start,DateTimeOffset end)=>start is {} at&&at<=end?(end.UtcTicks-at.UtcTicks)/(decimal)TimeSpan.TicksPerMillisecond:null;
        var valid=observation is not null&&computed>=trade.ClosedAt&&observation.ObservedAt>=trade.OpenedAt&&observation.ObservedAt<=computed&&observation.ReceivedAt<=observation.ObservedAt;
        return new(observation is null?"HistoricalPartial":valid?"Recorded":"Invalid",observation,computed,received,
            valid?Elapsed(observation!.ReceivedAt,observation.ObservedAt):null,valid?Elapsed(observation!.ObservedAt,computed):null,Elapsed(received,computed),
            valid&&received is {} book?book<=observation!.ObservedAt:null,
            "只计算本机接收、引擎判定和账本计算时点之间的间隔，未计持久化确认；判定与结算可在同轮为0。来源时钟与本机有偏差，不据两者相减推断网络延迟；缺失的旧判定时间不追补，也不能确定首次市场穿越时刻。");
    }
}
