using PulletCryptoRadar.Core;
using PulletCryptoRadar.Host;

namespace PulletCryptoRadar.Tests;

public sealed class LifecycleVerificationTests
{
    [Fact]
    public void SixPositionSharedAccountReconcilesFundingScaleOutThinExitsAndRestart()
    {
        var folder=Path.Combine(Path.GetTempPath(),"portfolio-lifecycle-"+Guid.NewGuid());
        try
        {
            var report=PortfolioLifecycleVerification.Run(folder);
            Assert.True(report.Passed,report.Error);Assert.Equal(6,report.PeakPositions);Assert.Equal(6,report.ClosedPositions);
            Assert.Equal("MaximumPositionsReached",report.SeventhAdmission);Assert.Equal(6,report.FundingEvents);
            Assert.Equal(18,report.Reductions);Assert.True(report.Fees>0);
            Assert.True(report.QuantitiesReconciled);Assert.True(report.CashReconciled);Assert.True(report.RiskWithinLimits);
            Assert.True(report.RestartPreserved);Assert.True(report.DuplicateIgnored);
            Assert.Equal(.0025m,new TradingLimits().RiskPerTradeFraction);
            Assert.Throws<InvalidOperationException>(()=>PortfolioLifecycleVerification.Run(folder));
        }
        finally{if(Directory.Exists(folder))Directory.Delete(folder,true);}
    }
    [Theory]
    [InlineData(TradeSide.Long,false)]
    [InlineData(TradeSide.Short,false)]
    [InlineData(TradeSide.Long,true)]
    [InlineData(TradeSide.Short,true)]
    public void SizingFeesFundingRestartAndAllExitsReconcileInIsolatedStore(TradeSide side,bool gap)
    {
        var folder=Path.Combine(Path.GetTempPath(),"paper-lifecycle-"+Guid.NewGuid());
        try
        {
            var report=PaperLifecycleVerification.Run(folder,side,gap);Assert.True(report.Passed,report.Error);
            Assert.True(report.Fees>0);Assert.True(report.CashReconciled);Assert.True(report.RestartPreserved);Assert.True(report.DuplicateIgnored);
            Assert.Equal(side==TradeSide.Long?-1:1,Math.Sign(report.Funding));
            Assert.Equal(gap?1:3,report.Reductions);
        }
        finally{if(Directory.Exists(folder))Directory.Delete(folder,true);}
    }
}
