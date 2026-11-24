using System.Text.Json;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Host;

namespace PulletCryptoRadar.Tests;

public sealed class QuantExpansionTests
{
    [Fact]
    public void ReplayRetainsBothDirectionsAndStrategiesInGateDiagnostics()
    {
        var start=new DateTimeOffset(2025,9,1,0,0,0,TimeSpan.Zero);var received=start.AddMonths(2);
        var asset=BinanceIdentity.AssetId("TESTUSDT");
        var bars=Enumerable.Range(-300,324).Select(i=>new HourCandle(asset,start.AddHours(i),start.AddHours(i+1),received,
            100,101,99,100,1000,600)).ToArray();
        var points=Enumerable.Range(-300,600).Select(i=>new OpenInterestPoint(asset,start.AddMinutes(i*5),received,1000+i,100000+i*100)).ToArray();
        var data=new QuantResearchDataset("TESTUSDT",start,start.AddDays(1),"Development",bars,[],points,[],[],[]);
        var result=new QuantReplayEngine().Run(data,MarketState.Mixed);
        Assert.Empty(result.Trades);Assert.NotNull(result.EntryDiagnostics);
        Assert.Equal(4,result.EntryDiagnostics.Select(row=>(row.Strategy,row.Side)).Distinct().Count());
        Assert.Equal(result.Reasons["TrendNotAligned"],result.EntryDiagnostics.Where(row=>row.Reason=="TrendNotAligned").Sum(row=>row.Count));
        Assert.All(result.EntryDiagnostics.Where(row=>row.Reason=="TrendNotAligned"),row=>Assert.Equal(result.DecisionBars,row.Count));
    }
    [Fact]
    public void FrozenPlanCannotSilentlyChangeAfterOutcomesWereViewed()
    {
        var folder=Path.Combine(Path.GetTempPath(),"quant-plan-"+Guid.NewGuid());
        try
        {
            QuantExpansionCommand.FreezePlan(folder);
            var path=Path.Combine(folder,"Research",QuantExpansionCommand.PlanFileName);var original=File.ReadAllText(path);
            QuantExpansionCommand.FreezePlan(folder);Assert.Equal(original,File.ReadAllText(path));
            using var json=JsonDocument.Parse(original);
            Assert.Equal(6,json.RootElement.GetProperty("Symbols").GetArrayLength());
            Assert.Single(json.RootElement.GetProperty("HoldoutProfiles").EnumerateArray());
            File.WriteAllText(path,original.Replace("2026-03-01","2026-04-01"));
            Assert.Throws<InvalidDataException>(()=>QuantExpansionCommand.FreezePlan(folder));
        }
        finally{if(Directory.Exists(folder))Directory.Delete(folder,true);}
    }
    [Fact]
    public async Task MissingStudyDatasetsAreReportedInsteadOfInventingZeroTradeSuccess()
    {
        var folder=Path.Combine(Path.GetTempPath(),"quant-missing-"+Guid.NewGuid());
        try
        {
            Assert.Equal(2,await QuantExpansionCommand.RunAsync(folder,true));
            var path=Path.Combine(folder,"Research","quant-expanded-holdout.json");
            using var report=JsonDocument.Parse(File.ReadAllText(path));
            Assert.Empty(report.RootElement.GetProperty("Results").EnumerateArray());
            Assert.Equal(6,report.RootElement.GetProperty("Limitations").EnumerateArray().Count(row=>row.GetString()!.StartsWith("缺失：",StringComparison.Ordinal)));
        }
        finally{if(Directory.Exists(folder))Directory.Delete(folder,true);}
    }
}
