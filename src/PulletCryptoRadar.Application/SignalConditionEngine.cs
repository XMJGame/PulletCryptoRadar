using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Application;
public static class SignalConditionEngine
{
    // Explanations of the same existing comparisons, not a second set of trading rules.
    public static IReadOnlyList<SignalCondition> Check(string strategy,string direction,decimal low72,decimal high72,decimal close,
        decimal low,decimal high,decimal returnFraction,decimal volumeRatio,ForwardRules rules)
    {
        var longSide=direction=="Long";var width=high72/low72-1;var location=high72>low72?(close-low72)/(high72-low72):(decimal?)null;
        var strength=high>low?(close-low)/(high-low):(decimal?)null;
        return strategy=="A"?
        [new("Range72h","72小时区间振幅%",width*100,$"≤{rules.CompressionMax*100:G}%",width<=rules.CompressionMax),
         new("RangePosition","收盘在72小时区间的位置%",location*100,longSide?"70%～100%":"0%～30%",longSide?close>=low72+(high72-low72)*.7m&&close<=high72:close<=low72+(high72-low72)*.3m&&close>=low72),
         new("Return24h","24小时闭合价变化%",returnFraction*100,longSide?"0%～12%":"-12%～0%",longSide?returnFraction is >=0 and <=.12m:returnFraction is >=-.12m and <=0),
         new("VolumeRatio","本小时/前24小时成交额中位数",volumeRatio,$"≥{rules.VolumeRatioA:G}倍",volumeRatio>=rules.VolumeRatioA)]:
        [new("Return24h","24小时闭合价变化%",returnFraction*100,longSide?"8%～40%":"-40%～-8%",longSide?returnFraction is >=.08m and <=.4m:returnFraction is >=-.4m and <=-.08m),
         new("Break72h",longSide?"高于前72小时高点%":"低于前72小时低点%",(close/(longSide?high72:low72)-1)*100,longSide?$">{rules.BreakoutBuffer*100:G}%":$"<-{rules.BreakoutBuffer*100:G}%",longSide?close>high72*(1+rules.BreakoutBuffer):close<low72*(1-rules.BreakoutBuffer)),
         new("VolumeRatio","本小时/前24小时成交额中位数",volumeRatio,$"≥{rules.VolumeRatioB:G}倍",volumeRatio>=rules.VolumeRatioB),
         new("CloseStrength","本小时收盘在高低区间的位置%",strength*100,longSide?"≥70%且非零振幅":"≤30%且非零振幅",high>low&&(longSide?strength>=.7m:strength<=.3m))];
    }
}
