using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Features;

public sealed class HistoricalResearchEngine
{
    public const string Version="PriceParticipationBaseline-1.0";
    public HistoricalSampleResult Evaluate(HistoricalSample sample,IReadOnlyList<HourCandle> candles,IReadOnlyList<string> missing)
    {
        var ordered=candles.OrderBy(row=>row.OpenAt).ToArray();
        if(ordered.Any(row=>row.AssetId!=BinanceIdentity.AssetId(sample.Symbol)||row.OpenAt.ToUnixTimeMilliseconds()%3600000!=0||
            row.ClosedAt-row.OpenAt!=TimeSpan.FromHours(1)||row.Low<=0||row.High<row.Low||row.Open<row.Low||row.Open>row.High||
            row.Close<row.Low||row.Close>row.High||row.QuoteVolume<0||row.TakerBuyQuoteVolume is null||row.TakerBuyQuoteVolume<0||row.TakerBuyQuoteVolume>row.QuoteVolume)||
            ordered.Select(row=>row.OpenAt).Distinct().Count()!=ordered.Length)
            throw new InvalidDataException("Invalid historical candle identity, ordering or values.");
        var gaps=ordered.Zip(ordered.Skip(1)).Sum(pair=>Math.Max(0,(int)((pair.Second.OpenAt-pair.First.ClosedAt).TotalHours)));
        var triggers=new List<ResearchTrigger>();DateTimeOffset? lastTrigger=null;
        // Deliberately price-only hypothesis; no historical ownership, market-cap, OI or executable fill is invented.
        // Signals use only candles closed by the decision time. Outcome windows may overlap and are not independent trades.
        for(var index=60;index<ordered.Length;index++)
        {
            var bar=ordered[index];
            if(bar.ClosedAt<sample.StudyStart||bar.ClosedAt>=sample.StudyEnd||lastTrigger is {} previous&&bar.ClosedAt-previous<TimeSpan.FromDays(7))continue;
            var window=ordered[(index-60)..(index+1)];
            if(window.Zip(window.Skip(1)).Any(pair=>pair.First.ClosedAt!=pair.Second.OpenAt))continue;
            var prior=ordered[(index-20)..index];var mean=prior.Average(row=>row.QuoteVolume);
            if(mean<=0||bar.QuoteVolume<=0)continue;
            var ratio=bar.QuoteVolume/mean;var return24=(bar.Close/ordered[index-24].Close-1)*100;
            var taker=bar.TakerBuyQuoteVolume!.Value/bar.QuoteVolume;
            if(ratio<1.5m||return24<0||return24>15||taker<.52m||bar.Close<=prior.Max(row=>row.High))continue;
            lastTrigger=bar.ClosedAt;
            triggers.Add(new(bar.ClosedAt,bar.Close,ratio,return24,taker,new[]{7,30}.Select(days=>Outcome(ordered,index,days)).ToArray()));
        }
        var study=ordered.Where(row=>row.ClosedAt>=sample.StudyStart&&row.ClosedAt<sample.StudyEnd).ToArray();
        var covered=study.Length>0&&study[0].ClosedAt<=sample.StudyStart.AddHours(1)&&study[^1].ClosedAt>=sample.StudyEnd.AddHours(-1);
        return new(sample,!covered||missing.Count>0||gaps>0?"PartialCoverage":"PriceHistoryReady",ordered.Length,gaps,triggers,missing,
            ["价格/成交量假设研究，未检验低市值、历史持币、关联主体或解锁条件",
             "这是闭合价参考后的区间结果，不是可成交回测；不含手续费、资金费、盘口、杠杆与强平",
             "样本由已知案例和当前存续币构成，存在选择/幸存者偏差；不是样本外有效性证明",
             "同币相隔至少7天记录一次，30天结果仍可能重叠；不得当作独立交易胜率"]);
    }

    public static ForwardOutcome Outcome(IReadOnlyList<HourCandle> candles,int index,int days)
    {
        var reference=candles[index];var count=days*24;
        var future=candles.Skip(index+1).Take(count).ToArray();
        var complete=future.Length==count&&future[0].OpenAt==reference.ClosedAt&&
            future.Zip(future.Skip(1)).All(pair=>pair.First.ClosedAt==pair.Second.OpenAt);
        if(!complete)return new(days,false,null,null,null,null);
        var peak=reference.Close;var drawdown=0m;
        // Close-to-close drawdown avoids making up intrabar high/low ordering.
        foreach(var bar in future){peak=Math.Max(peak,bar.Close);drawdown=Math.Min(drawdown,(bar.Close/peak-1)*100);}
        return new(days,true,(future[^1].Close/reference.Close-1)*100,(future.Max(row=>row.High)/reference.Close-1)*100,
            (future.Min(row=>row.Low)/reference.Close-1)*100,drawdown);
    }
}
