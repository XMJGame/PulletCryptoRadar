using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;

namespace PulletCryptoRadar.IntegrationTests;

public sealed class PaperTests:IDisposable
{
    private static readonly DateTimeOffset Now=new(2026,10,29,8,0,0,TimeSpan.Zero);
    private readonly string _root=Path.Combine(Path.GetTempPath(),"Pullet-Paper-"+Guid.NewGuid());
    private static MarketView Market(DateTimeOffset time,decimal price=100,int id=1)=>new(new(id,$"TEST{id}USDT","TEST",Now.AddDays(-90),true),price,10000000,3,price,0,time,time,time,500,500,500,
        new(){Returns=new(){[60]=new(3,DataStatus.Valid,time,"Fixture")}});
    private static RadarObservation Observation(int id=1)=>new(Market(Now,id:id),new(MoveDirection.Up,"UpMove-2.0",70,80,"Breakout",DataStatus.Valid,true,[],["Price","Volume","Structure"],[],[]),new(MoveDirection.Down,"DownMove-2.0",10,80,"Normal",DataStatus.Valid,false,[],[],[],[]),id);
    private static RadarSnapshot Radar(params RadarObservation[] observations)=>new(Guid.NewGuid(),Now,new(Now,MarketRegime.Normal,DataStatus.Valid,10,10,5,5,0,0,0,0,1,1,1,"Fixture"),observations,[],[],"{}");
    private static PaperState State()=>new(){Revision=1,StartedAt=Now.AddMinutes(-1),UpdatedAt=Now.AddSeconds(-1),Enabled=true,Rules=new(){ResearchValidated=true}};
    [Fact] public void NewAccountAndUnvalidatedStrategiesCannotAutoOpen()
    {
        Assert.False(new PaperState().Enabled);Assert.False(new PaperRules().ResearchValidated);
        var batch=PaperEngine.Tick(State() with{Rules=new()},Radar(Observation()),Markets(Now),Now);Assert.Empty(batch.State.Pending);Assert.Empty(batch.State.Positions);
    }
    private static IReadOnlyDictionary<int,MarketView> Markets(DateTimeOffset time,decimal price=100)=>new Dictionary<int,MarketView>{{1,Market(time,price)}};
    private static PaperState Opened()
    {var radar=Radar(Observation());var pending=PaperEngine.Tick(State(),radar,Markets(Now),Now).State;return PaperEngine.Tick(pending,radar,Markets(Now.AddSeconds(5)),Now.AddSeconds(5)).State;}
    [Fact] public void SignalWaitsForStrictlyLaterQuoteAndDoesNotBackfillPreStartSignals()
    {
        var radar=Radar(Observation());var pending=PaperEngine.Tick(State(),radar,Markets(Now),Now).State;Assert.Empty(pending.Positions);Assert.Single(pending.Pending);
        var same=PaperEngine.Tick(pending,radar,Markets(Now),Now.AddSeconds(1)).State;Assert.Empty(same.Positions);Assert.Single(same.Pending);
        var futureStart=State() with{StartedAt=Now.AddMinutes(1)};Assert.Empty(PaperEngine.Tick(futureStart,radar,Markets(Now),Now).State.Pending);
    }
    [Fact] public void OpeningReservesOneHundredMarginAndCostsWithTenfoldNotional()
    {
        var state=Opened();var p=Assert.Single(state.Positions);Assert.Equal(100,p.InitialMargin);Assert.Equal(1000,p.Quantity*p.EntryPrice,8);Assert.Equal(899.5m,state.Cash);Assert.Equal(1,p.Bucket);
        Assert.True(p.EntryPrice>100);Assert.Equal(p.EntryPrice*.985m,p.StopPrice);Assert.Equal(p.EntryPrice*1.03m,p.TakePrice);
        Assert.Single(state.UsedSignals);
    }
    [Fact] public void GapStopUsesNextObservedPriceAndPnlReconcilesCash()
    {
        var open=Opened();var result=PaperEngine.Tick(open,null,Markets(Now.AddMinutes(1),97),Now.AddMinutes(1));var t=Assert.Single(result.Trades);
        Assert.Empty(result.State.Positions);Assert.Equal(97*.9995m,t.ExitPrice);Assert.True(t.NetPnl< -15);Assert.True(t.QuoteGap);
        Assert.Equal(1000+t.NetPnl,result.State.Cash);Assert.Equal(t.GrossPnl-t.Fees-t.FundingCost+t.SettlementAdjustment,t.NetPnl,8);
    }
    [Fact] public void LiquidationIsExplicitPaperModelWithReconciledAdjustment()
    {
        var result=PaperEngine.Tick(Opened(),null,Markets(Now.AddSeconds(10),80),Now.AddSeconds(10));var t=Assert.Single(result.Trades);
        Assert.Contains("模拟强平",t.Reason);Assert.Equal(-100.5m,t.NetPnl);Assert.Equal(899.5m,result.State.Cash);Assert.True(t.SettlementAdjustment>0);
        Assert.Equal(t.GrossPnl-t.Fees-t.FundingCost+t.SettlementAdjustment,t.NetPnl,8);
    }
    [Theory] [InlineData("pause")] [InlineData("stale")] [InlineData("future")] [InlineData("chase")] [InlineData("reverse")]
    public void InvalidOrDisallowedExecutionDoesNotCreatePosition(string issue)
    {
        var o=Observation();var radar=Radar(o);var p=PaperEngine.Tick(State(),radar,Markets(Now),Now).State;var at=Now.AddSeconds(5);var m=Market(at);
        if(issue=="pause")p=p with{Enabled=false};if(issue=="stale")m=m with{PriceSourceTime=Now.AddMinutes(-1)};if(issue=="future")m=m with{PriceReceivedTime=at.AddSeconds(1)};
        if(issue=="chase")m=m with{Price=102};if(issue=="reverse")radar=radar with{Observations=[o with{Down=o.Down with{IsCandidate=true}}]};
        Assert.Empty(PaperEngine.Tick(p,radar,new Dictionary<int,MarketView>{{1,m}},at).State.Positions);
    }
    [Fact] public void PauseKeepsRiskExitsAndUsedEpisodeDoesNotReopen()
    {
        var open=Opened() with{Enabled=false};var exit=PaperEngine.Tick(open,Radar(Observation()),Markets(Now.AddSeconds(10),104),Now.AddSeconds(10));Assert.Single(exit.Trades);
        var reopened=PaperEngine.Tick(exit.State with{Enabled=true},Radar(Observation()),Markets(Now.AddSeconds(11)),Now.AddSeconds(11));Assert.Empty(reopened.State.Pending);Assert.Empty(reopened.State.Positions);
    }
    [Fact] public void FundingCostsReduceMarginAndAreNotInventedAsActualFunding()
    {
        var s=Opened();var later=Now.AddHours(8);var result=PaperEngine.Tick(s,null,Markets(later),later);var t=Assert.Single(result.Trades);Assert.Equal(.1m,t.FundingCost,8);Assert.Contains("超时",t.Reason);
        Assert.Equal(t.GrossPnl-t.Fees-t.FundingCost+t.SettlementAdjustment,t.NetPnl,8);
    }
    [Fact] public void TwoPositionLimitRetainsOtherEightMarginBuckets()
    {
        var radar=Radar(Observation(1),Observation(2),Observation(3));var markets=Enumerable.Range(1,3).ToDictionary(i=>i,i=>Market(Now,id:i));
        var waiting=PaperEngine.Tick(State(),radar,markets,Now).State;Assert.Equal(2,waiting.Pending.Count);
        markets=Enumerable.Range(1,3).ToDictionary(i=>i,i=>Market(Now.AddSeconds(5),id:i));var state=PaperEngine.Tick(waiting,radar,markets,Now.AddSeconds(5)).State;
        Assert.Equal(2,state.Positions.Count);Assert.Equal(200,state.Positions.Sum(p=>p.InitialMargin));Assert.Equal(799,state.Cash);Assert.Equal(2,state.Positions.Select(p=>p.Bucket).Distinct().Count());
    }
    [Fact] public void PersistenceIsAtomicAndRejectsDuplicateRevisionWithoutResettingAccount()
    {
        var db=new SqliteDatabase(Options.Create(new StorageOptions{Directory=_root}));db.Initialize();var state=State();db.SavePaper(new(state,0,[]));
        Assert.Throws<MappingConflictException>(()=>db.SavePaper(new(state,0,[])));var batch=PaperEngine.Tick(state,Radar(Observation()),Markets(Now),Now);db.SavePaper(batch);
        var restored=new PaperRepository(db).Restore()!;Assert.Equal(batch.State.Revision,restored.Revision);Assert.Single(restored.Pending);Assert.Equal(1000,restored.Cash);
        var roundtrip=JsonSerializer.Deserialize<PaperState>(JsonSerializer.Serialize(restored))!;Assert.Equal(restored.Pending[0].Evidence.Up.Score,roundtrip.Pending[0].Evidence.Up.Score);
    }
    public void Dispose(){SqliteConnection.ClearAllPools();if(Directory.Exists(_root))Directory.Delete(_root,true);}
}
