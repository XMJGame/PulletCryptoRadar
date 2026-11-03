using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Application;
public static class TokenFollowupEngine
{
    public static TokenFollowup Evaluate(TokenCheckReport report,IReadOnlyList<Candle> candles,DateTimeOffset now,TokenFollowup? previous=null)
    {
        var validQuote=report.IdentityVerified&&report.Instrument is not null&&report.Price>0&&report.Sources.Any(s=>s.Name=="Binance永续报价"
            &&TokenCheckEngine.Fresh(s.SourceTime,s.ReceivedTime,report.AsOf,TimeSpan.FromSeconds(90)));
        var points=new[]{24,72,168}.Select(hours=>
        {
            var due=report.AsOf.AddHours(hours);
            var saved=previous?.ReportId==report.Id?previous.Points.FirstOrDefault(p=>p.Hours==hours&&p.Status=="已观察"):null;
            if(saved is not null)return saved;
            if(!validQuote)return new TokenFollowupPoint(hours,due,"无可比基准",null,null,null,null,"原报告没有身份关联且新鲜的永续报价；不拿聚合现货价格与合约混比");
            if(now<due)return new(hours,due,"未到观察时间",null,null,null,null,"不读取未来价格");
            var bar=candles.Where(b=>b.Symbol==report.Instrument&&b.IntervalMinutes==60&&b.IsValid&&b.EndTime>=due
                    &&b.EndTime<due.AddHours(1)&&b.EndTime<=now&&b.SourceTime>=b.EndTime.AddMilliseconds(-1)&&b.SourceTime<=b.ReceivedTime&&b.ReceivedTime<=now)
                .MinBy(b=>b.EndTime);
            return bar is null?new TokenFollowupPoint(hours,due,"等待本地数据",null,null,null,null,"缺少目标时点后1小时内的闭合小时线；不以当前价替代，不额外下载"):
                new(hours,due,"已观察",bar.EndTime,bar.Close,(bar.Close/report.Price!.Value-1)*100,
                    report.Technical.Invalidation is {} invalid?bar.Close<invalid:null,"同合约首个目标时点后闭合小时收盘，最多偏移不足1小时；未计费用、不模拟成交，低于失效位仅是该收盘时点判断");
        }).ToArray();
        return new(report.Id,now,"原报告新鲜永续报价 → 同合约闭合小时收盘；仅复盘价格表现，不是策略收益或完整路径止损检测",points);
    }
}
