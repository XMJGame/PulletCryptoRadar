using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;

namespace PulletCryptoRadar.IntegrationTests;

public sealed class OpportunityTests
{
    private static readonly DateTimeOffset Now=new(2026,10,31,0,30,0,TimeSpan.Zero);
    private static MarketView Market(int id,decimal ret=0)=>new(new(id,$"TOKEN{id:D3}USDT","TOKEN",Now.AddDays(-100),true),100,10_000_000,ret,100,0,Now,Now,Now,0,0,0,new());
    private static IReadOnlyList<Candle> Bars(string symbol,DateTimeOffset end,int count=337)=>Enumerable.Range(0,count).Select(i=>new Candle(symbol,60,end.AddHours(i-count),100,103,99,100,i==336?150:100,end.AddHours(i-count+1).AddMilliseconds(-1),end.AddHours(i-count+1))).ToArray();
    private static DerivativeRuntimeSnapshot Oi(string symbol,DateTimeOffset now)
    {
        var hour=DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds()/3600*3600);
        return new(symbol,new Dictionary<DerivativeKind,IReadOnlyList<DerivativeSample>>{{DerivativeKind.OpenInterest5m,
            [new(symbol,DerivativeKind.OpenInterest5m,hour.AddMinutes(-5).AddHours(-24),now,Quantity:100),new(symbol,DerivativeKind.OpenInterest5m,hour.AddMinutes(-5),now,Quantity:110)]}},[],null,null);
    }
    [Fact]public void UnverifiedIdentityAndMissingOiDoNotPreventPriceVolumeDiscovery()
    {
        var markets=Enumerable.Range(1,522).Select(i=>Market(i,i<=100?2:i<=200?10:-8)).ToArray();
        var selected=OpportunityEngine.Select(markets,Now);
        Assert.Equal(60,selected.Count);Assert.Equal(60,selected.Select(r=>r.Market.Instrument.Symbol).Distinct().Count());
        Assert.Contains(selected,r=>r.Tracks.Contains("Early"));Assert.Contains(selected,r=>r.Tracks.Contains("Momentum"));
        Assert.Contains(selected,r=>r.Tracks.Contains("Weakness"));Assert.Contains(selected,r=>r.Tracks.Contains("Explore"));
        Assert.All(selected,r=>Assert.False(r.Market.Features.Capital.TrustedIdentity));
    }
    [Fact]public void StaleFutureInactiveAndIlliquidMarketsAreExplicitlyExcluded()
    {
        var m=Market(1);var invalid=new[]{m with{PriceSourceTime=Now.AddMinutes(-2)},m with{PriceReceivedTime=Now.AddSeconds(1)},m with{Volume24h=4_999_999},m with{Instrument=m.Instrument with{Active=false}},m with{Price=null}};
        Assert.All(invalid,x=>Assert.NotEqual("可参与粗筛",OpportunityEngine.Eligibility(x,Now)));
        Assert.Empty(OpportunityEngine.Select(invalid,Now));Assert.Equal("可参与粗筛",OpportunityEngine.Eligibility(m,Now));
    }
    [Fact]public void RotationGivesUnselectedSymbolsAChanceAndNeverDuplicatesTheUniverse()
    {
        var markets=Enumerable.Range(1,200).Select(i=>Market(i)).ToArray();var first=OpportunityEngine.Select(markets,Now);
        var later=OpportunityEngine.Select(markets.Select(m=>m with{PriceSourceTime=Now.AddMinutes(5),PriceReceivedTime=Now.AddMinutes(5)}).ToArray(),Now.AddMinutes(5));
        Assert.NotEqual(first.Where(r=>r.Tracks.Contains("Explore")).Select(r=>r.Market.Instrument.Symbol),later.Where(r=>r.Tracks.Contains("Explore")).Select(r=>r.Market.Instrument.Symbol));
        Assert.Equal(later.Count,later.Select(r=>r.Market.Instrument.Symbol).Distinct().Count());
    }
    [Fact]public void HourlyCollectionFetchesOnlyNewBarsUnlessThereIsAGap()
    {
        var bars=Bars("TOKEN001USDT",Now.AddMinutes(-30),744);
        Assert.Equal(2,OpportunityEngine.HourlyFetchLimit(bars,Now.AddMinutes(30)));
        Assert.Equal(800,OpportunityEngine.HourlyFetchLimit(bars.Where((_,i)=>i!=30).ToArray(),Now.AddMinutes(30)));
        Assert.Equal(800,OpportunityEngine.HourlyFetchLimit([],Now));
    }
    [Fact]public void ResearchWorksOutsideExecutionWindowButCannotBeAcceptedAsExecutionEvidence()
    {
        var m=Market(1);var end=Now.AddMinutes(-30);var bars=Bars(m.Instrument.Symbol,end).ToArray();bars[330]=bars[330] with{High=104};
        var oi=Oi(m.Instrument.Symbol,Now);var row=OpportunityEngine.Evaluate(m,["Early"],bars,oi,Now);
        Assert.Equal(337,row.Hours);Assert.NotNull(row.Range72hPercent);Assert.NotNull(row.HourVolumeRatio);
        Assert.Equal(10,row.Oi24hPercent);Assert.NotEmpty(row.Plans);
        Assert.All(row.Plans,p=>Assert.NotEqual(PaperAnalysis.RulesHash,p.RulesHash));
        Assert.Contains(row.Missing,line=>line.Contains("身份"));Assert.Contains(row.Missing,line=>line.Contains("钱包"));
        Assert.All(PaperAnalysis.Analyze(end.AddDays(-1),m,bars,oi,Now),p=>Assert.Equal("Insufficient",p.Status));
    }
    [Fact]public void MissingFutureOrGappedEvidenceNeverBecomesACompleteCandidate()
    {
        var m=Market(1);var end=Now.AddMinutes(-30);var bars=Bars(m.Instrument.Symbol,end).ToArray();
        var row=OpportunityEngine.Evaluate(m,["Early"],bars.Where((_,i)=>i!=200).ToArray(),Oi(m.Instrument.Symbol,Now),Now);
        Assert.Equal("待补数据",row.State);Assert.Null(row.Range72hPercent);Assert.Empty(row.Plans);
        row=OpportunityEngine.Evaluate(m,["Early"],bars.Select(c=>c with{ReceivedTime=Now.AddSeconds(1)}).ToArray(),Oi(m.Instrument.Symbol,Now),Now);
        Assert.Equal(0,row.Hours);Assert.Equal("待补数据",row.State);
        row=OpportunityEngine.Evaluate(m,["Early"],bars,new(m.Instrument.Symbol,new Dictionary<DerivativeKind,IReadOnlyList<DerivativeSample>>(),[],null,null),Now);
        Assert.Equal("待补数据",row.State);Assert.Null(row.Oi24hPercent);
    }
    private sealed class Clock:TimeProvider{public DateTimeOffset Current=Now;public override DateTimeOffset GetUtcNow()=>Current;}
    [Fact]public void ExpandedHourlyAuditKeepsAllTwoHundredFortyDirectionalDecisions()
    {
        var root=Path.Combine(Path.GetTempPath(),"Pullet-Pool-Audit-"+Guid.NewGuid());
        try
        {
            var db=new SqliteDatabase(Options.Create(new StorageOptions{Directory=root}));db.Initialize();
            var signals=Enumerable.Range(1,240).Select(i=>new ForwardDecision(i.ToString(),"TOKEN001USDT","A",Now,Now,"NoSignal","test",337,1,null,null,null,null,null,"Unknown",null,PaperAnalysis.RulesHash)).ToArray();
            var state=new PaperState{Revision=1,UpdatedAt=Now,Rules=StructuralPaperEngine.Rules(),AnalysisHour=Now,UniverseVersion=OpportunityEngine.Version,
                SignalObservations=signals,Decisions=[new(Now,"账户","双向分析快照","240条") ]};db.SavePaper(new(state,0,[]));
            using var c=db.Open();using var command=c.CreateCommand();command.CommandText="SELECT Payload FROM PaperAudit";
            using var input=new MemoryStream((byte[])command.ExecuteScalar()!);using var gzip=new System.IO.Compression.GZipStream(input,System.IO.Compression.CompressionMode.Decompress);
            using var doc=System.Text.Json.JsonDocument.Parse(gzip);Assert.Equal(240,doc.RootElement.GetProperty("Signals").GetArrayLength());
            Assert.Equal(OpportunityEngine.Version,doc.RootElement.GetProperty("UniverseVersion").GetString());
        }
        finally{SqliteConnection.ClearAllPools();if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    private sealed class History:IAssetRepository
    {public Task<IReadOnlyList<Candle>> ReadHourlyHistoryAsync(int id,int limit,CancellationToken ct)=>Task.FromResult<IReadOnlyList<Candle>>([]);public Task<IReadOnlyList<Instrument>> ReadInstrumentsAsync(CancellationToken ct)=>Task.FromResult<IReadOnlyList<Instrument>>([]);}
    [Fact]public async Task RepositoryRestoresSnapshotsAndMinuteRefreshDoesNotLoseFiveMinuteArchiving()
    {
        var root=Path.Combine(Path.GetTempPath(),"Pullet-Pools-"+Guid.NewGuid());
        try
        {
            var db=new SqliteDatabase(Options.Create(new StorageOptions{Directory=root}));db.Initialize();var repo=new OpportunityRepository(db);var clock=new Clock();var market=new MarketRuntimeStore();
            var service=new OpportunityService(market,new DerivativeRuntimeStore(),new History(),repo,new(),clock);
            await service.RefreshAsync(default);Assert.Null(repo.Latest());market.Register([Market(1).Instrument]);
            await service.RefreshAsync(default);for(var i=1;i<=6;i++){clock.Current=Now.AddMinutes(i);await service.RefreshAsync(default);}
            Assert.Equal(2,repo.History(12).Count);Assert.Equal(Now.AddMinutes(5),repo.Latest()!.AsOf);Assert.Equal(Now.AddMinutes(6),service.Latest()!.AsOf);
            Assert.Equal(2,new OpportunityRepository(db).History(12).Count);
            Assert.Throws<SqliteException>(()=>repo.Save(repo.Latest()!));Assert.Equal(2,repo.History(12).Count);
            repo.Save(repo.Latest()! with{Id=Guid.NewGuid(),AsOf=Now.AddMinutes(7),Version="old-pool"});
            var restored=new OpportunityService(market,new DerivativeRuntimeStore(),new History(),repo,new(),clock);
            Assert.Null(restored.Latest());clock.Current=Now.AddMinutes(8);await restored.RefreshAsync(default);
            Assert.Equal(OpportunityEngine.Version,restored.Latest()!.Version);
        }
        finally{SqliteConnection.ClearAllPools();if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
