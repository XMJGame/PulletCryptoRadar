using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;
using PulletCryptoRadar.Providers.Market;
using PulletCryptoRadar.Scanner;

namespace PulletCryptoRadar.IntegrationTests;

public sealed class AlternativeTests
{
    private static readonly DateTimeOffset Now=new(2026,10,29,7,0,0,TimeSpan.Zero);
    private static JsonElement J(string json)=>JsonDocument.Parse(json).RootElement.Clone();
    private static VenueQuote Gate()=>AlternativeParser.GateTickers(J("""[{"contract":"BTC_USDT","last":"80000.123456789","volume_24h_quote":"12000000","volume_24h":"999999999","total_size":"99999999999999","funding_rate":"0.0001"}]"""),
        J("""[{"name":"BTC_USDT","type":"direct","status":"trading","funding_interval":14400}]"""),Now,Now)[0];
    private static SqliteDatabase Db(){var db=new SqliteDatabase(Options.Create(new StorageOptions{Directory=Path.Combine(Path.GetTempPath(),"Pullet-Spec-"+Guid.NewGuid())}));db.Initialize();return db;}
    [Fact] public void GateUsesQuoteTurnoverAndVerifiedFundingIntervalButNeverInfersOiFromTotalSize()
    {
        var q=Gate();Assert.Equal(80000.123456789m,q.Price);Assert.Equal(12_000_000m,q.Volume24hQuote);Assert.Equal(.0002m,q.FundingRate8h);
        Assert.Null(q.OpenInterestUsd.Value);Assert.Equal("BTC_USDT",q.Symbol);Assert.Equal("BTC",q.BaseAsset);
    }
    [Fact] public void GateStatsUsesReportedUsdAndQuantityChangesWithExactHorizon()
    {
        var source=Now.ToUnixTimeSeconds();var json=J($$"""[{"time":{{source-900}},"open_interest":"100","open_interest_usd":"1000000","top_lsr_size":"1"},{"time":{{source}},"open_interest":"105","open_interest_usd":"1100000.123456789","top_lsr_size":"3","lsr_account":"4"}]""");
        var q=AlternativeParser.GateStats(Gate(),json,Now);Assert.Equal(5,q.OiChange15m.Value);Assert.Equal(1100000.123456789m,q.OpenInterestUsd.Value);
        Assert.Equal(.8m,q.LongShare.Value);Assert.Equal(25,q.TopPositionChange15m.Value);Assert.Null(q.OiRobustZ.Value);
        Assert.Equal(Now,q.OpenInterestUsd.SourceTime);
    }
    [Fact] public void FutureStatsAndMissingHorizonCannotInventCurrentOiOrChanges()
    {
        var source=Now.ToUnixTimeSeconds();var q=AlternativeParser.GateStats(Gate(),J($$"""[{"time":{{source+300}},"open_interest_usd":"200"},{"time":{{source-1200}},"open_interest_usd":"100"}]"""),Now);
        Assert.Equal(DataStatus.Stale,q.OpenInterestUsd.Status);Assert.Null(q.OiChange15m.Value);Assert.Null(q.OiRobustZ.Value);
    }
    [Fact] public void CoinbaseSpotDoesNotPretendBaseVolumeIsUsdTurnoverOrPerpetualOi()
    {
        var q=AlternativeParser.CoinbaseTicker("BTC",J("""{"price":"80000","volume":"1234","time":"2026-10-29T07:00:00Z"}"""),Now)!;
        Assert.Equal("Spot",q.Kind);Assert.Null(q.Volume24hQuote);Assert.Null(q.OpenInterestUsd.Value);
        Assert.Null(AlternativeParser.CoinbaseTicker("BTC",J("""{"price":"1","time":"2026-10-30T07:00:00Z"}"""),Now));
    }
    [Fact] public void VenueCacheRejectsOlderSourceAndKeepsExchangesSeparate()
    {
        var store=new AlternativeMarketStore();var q=Gate();store.Set([q,q with{Exchange="Coinbase",Price=1},q with{SourceTime=Now.AddMinutes(-1),Price=2}]);
        Assert.Equal(2,store.GetAll().Count);Assert.Equal(q.Price,store.GetAll().Single(x=>x.Exchange=="Gate").Price);
    }
    [Fact] public void VenueBindingCasAndUniqueNativeInstrumentAreTransactional()
    {
        var db=Db();var ids=db.Synchronize([new("BTCUSDT","BTC",Now),new("OTHERUSDT","OTHER",Now)]).ToArray();
        db.SaveVenueBinding(new(ids[0].AssetId,"Gate","BTC_USDT",1,Now),0);
        Assert.Throws<MappingConflictException>(()=>db.SaveVenueBinding(new(ids[0].AssetId,"Gate","BTC_USDT",2,Now),0));
        Assert.Throws<MappingConflictException>(()=>db.SaveVenueBinding(new(ids[1].AssetId,"Gate","BTC_USDT",1,Now),0));
        Assert.Single(new SpeculativeRepository(db).Bindings());using var c=db.Open();Assert.Equal(1,c.QuerySingle<int>("SELECT COUNT(*) FROM VenueBindingAudit"));
        db.SaveVenueBinding(new(ids[0].AssetId,"Gate",null,2,Now),1);
        db.SaveVenueBinding(new(ids[1].AssetId,"Gate","BTC_USDT",1,Now),0);
        Assert.Null(new SpeculativeRepository(db).Bindings().Single(b=>b.AssetId==ids[0].AssetId).Symbol);
        Assert.Equal(3,c.QuerySingle<int>("SELECT COUNT(*) FROM VenueBindingAudit"));
    }
    [Fact] public void ExternalInputNeverCopiesBinancePriceVolumeOrReturnBaselines()
    {
        var market=new MarketView(new(1,"BTCUSDT","BTC",Now,true),100,100,null,null,null,Now,Now,null,0,0,0,new(){PriceRobustZ5m=new(10,DataStatus.Valid,Now,"Binance")});
        var external=SpeculativeService.External(market,Gate(),false,Now);
        Assert.Null(external.PriceRobustZ.Value);Assert.Null(external.Return1h.Value);Assert.False(external.VenueIdentityVerified);
        Assert.Equal(Gate().Price,external.Price.Value);
    }
    [Fact] public void FrozenSpeculativeSnapshotRestoresInputsAndRecomputesExactly()
    {
        var db=Db();var market=new MarketView(new(1,"BTCUSDT","BTC",Now,true),100,100,null,null,null,Now,Now,null,0,0,0,new());
        var input=SpeculativeService.External(market,Gate(),false,Now);var settings=new SpeculativeOptions();
        var assessment=new SpeculativeDetector(settings).Detect(input,Now);var snapshot=new SpeculativeSnapshot(Guid.NewGuid(),Now,SpeculativeDetector.Version,JsonSerializer.Serialize(settings),[new(input,assessment,true)]);
        db.SaveSpeculative(snapshot);db.SaveSpeculative(snapshot);var restored=Assert.Single(new SpeculativeRepository(db).History(3));
        Assert.Equal(JsonSerializer.Serialize(assessment),JsonSerializer.Serialize(new SpeculativeDetector(settings).Detect(restored.Rows[0].Input,restored.AsOf)));
        Assert.Equal(80000.123456789m,restored.Rows[0].Input.Price.Value);
    }
    [Fact] public void SavedBinanceCooldownSurvivesRestartAndClearedHealthDoesNotRestoreIt()
    {
        var db=Db();var until=Now.AddHours(2);var health=new ProviderHealth("Binance Cooldown",null,Now,null,1,$"REST 冷却至 UTC {until:O}；等待交易所恢复。实时流独立运行。",60);
        db.Save([], [health]);Assert.Equal(until,db.ReadBinanceCooldown());
        db.Save([], [health with{LastSuccess=Now.AddMinutes(1),Error=null}]);Assert.Null(db.ReadBinanceCooldown());
    }
    [Fact] public void GateOiRobustBaselineExcludesTheCurrentSpike()
    {
        var points=Enumerable.Range(0,100).Select(i=>new Dictionary<string,object>{["time"]=Now.ToUnixTimeSeconds()-(99-i)*300,
            ["open_interest"]=i==99?"150":(100+i%3).ToString(),["open_interest_usd"]="1000000"}).ToArray();
        var quote=AlternativeParser.GateStats(Gate(),J(JsonSerializer.Serialize(points)),Now);
        Assert.Equal(DataStatus.Valid,quote.OiRobustZ.Status);Assert.True(quote.OiRobustZ.Value>3);
    }
    [Fact] public async Task Gate429PausesNextCallWithoutRepeatedHttpRequests()
    {
        var handler=new RateLimitHandler();var provider=new AlternativeProvider(new HttpClient(handler),TimeProvider.System,Options.Create(new AlternativeOptions()));
        await Assert.ThrowsAsync<HttpRequestException>(()=>provider.GetGateAsync(["BTC"],default));
        await Assert.ThrowsAsync<HttpRequestException>(()=>provider.GetGateAsync(["BTC"],default));Assert.Equal(1,handler.Calls);
    }
    private sealed class RateLimitHandler:HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
        {Calls++;var response=new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);response.Headers.RetryAfter=new(TimeSpan.FromMinutes(3));return Task.FromResult(response);}
    }
    [Fact] public async Task BinanceRemainsSelectedAndStalePriceAllowsIndependentReference()
    {
        var db=Db();var clock=new Clock();var queue=new PersistenceQueue();var health=new ProviderHealthStore(clock);
        using var worker=new PersistenceWorker(db,queue,health,Microsoft.Extensions.Logging.Abstractions.NullLogger<PersistenceWorker>.Instance);
        await worker.StartAsync(default);await queue.Ready.Task;
        try
        {
            var instruments=await queue.SyncAsync([new("BTCUSDT","BTC",Now.AddDays(-30))],default);var id=instruments[0].AssetId;
            var market=new MarketRuntimeStore();market.Register(instruments);market.Apply(new TickerEvent("BTCUSDT",80_000,100_000_000,3,Now,Now));
            var alt=new AlternativeMarketStore();alt.Set([Gate()with{OpenInterestUsd=new(10_000_000,DataStatus.Valid,Now,"Gate"),OiRobustZ=new(3,DataStatus.Valid,Now,"Gate")}]);
            var capital=new CapitalRuntimeStore();capital.SetCatalog(new(Now,[new("bitcoin","btc","Bitcoin",new Dictionary<string,string>())]));
            capital.SetMappings([new(id,"bitcoin",MappingStatus.ManualVerified,["bitcoin"],1,null,null,1,Now,"Fixture")]);
            capital.SetMarkets([new("bitcoin",Now,Now,50_000_000,100_000_000,null,null,null,null,null)]);
            var settings=new SpeculativeOptions();var service=new SpeculativeService(market,alt,new SpeculativeRepository(db),new SpeculativePersistence(queue),settings,new(settings),new(),capital,new(new()),new(new()),clock);
            var first = await service.ScanAsync(default);
            Assert.Equal("Binance", Assert.Single(first.Rows).Input.Exchange);
            clock.Current = Now.AddSeconds(31);
            var stale = await service.ScanAsync(default);
            Assert.Equal("Binance", Assert.Single(stale.Rows, r => r.IsSelectedSource).Input.Exchange);
            var gate = Assert.Single(stale.Rows, r => r.Input.Exchange == "Gate");
            Assert.False(gate.IsSelectedSource);
            Assert.Null(gate.Assessment.AnomalyScore);
            await Assert.ThrowsAsync<ArgumentException>(()=>service.SetVenueBindingAsync(id,new("Gate","BTC_USDT",0,false),default));
            await service.SetVenueBindingAsync(id,new("Gate","BTC_USDT",0,true),default);var second=await service.ScanAsync(default);
            Assert.NotNull(second.Rows.Single(r=>r.Input.Exchange=="Gate").Assessment.AnomalyScore);
            settings.EntryScore=99;Assert.All(service.Audit(3),a=>Assert.Equal("Match",a.Status));
            await Assert.ThrowsAsync<MappingConflictException>(()=>service.SetVenueBindingAsync(id,new("Gate","BTC_USDT",0,true),default));
            await service.SetVenueBindingAsync(id,new("Gate","BTC_USDT",1,false,true),default);
            Assert.Null((await service.ScanAsync(default)).Rows.Single(r=>r.Input.Exchange=="Gate").Assessment.AnomalyScore);
        }
        finally{await worker.StopAsync(default);}
    }
    private sealed class Clock:TimeProvider
    {
        public DateTimeOffset Current { get; set; } = Now;
        public override DateTimeOffset GetUtcNow() => Current;
    }
}
