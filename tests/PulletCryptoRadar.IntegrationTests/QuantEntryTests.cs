using System.IO.Compression;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;
namespace PulletCryptoRadar.IntegrationTests;

public sealed class QuantEntryTests
{
    private static (QuantRehearsalInput Fixture,PaperBatch Pending) Pending(bool shortSide=false)
    {
        var f=QuantRehearsalTests.ReadyInput(shortSide);var batch=QuantPaperEngine.Tick(QuantLiveTests.State(f) with{Revision=1},[],[],QuantLiveTests.Input(f,f.CapturedAt));
        Assert.Single(batch.State.Pending);Assert.Equal("Created",Assert.Single(batch.EntryReviews).Outcome);return(f,batch);
    }
    [Theory][InlineData(false)][InlineData(true)]public void APriceImprovementThatWouldExpandSizingUsesTheOriginalApprovedCaps(bool shortSide)
    {
        var (f,pending)=Pending(shortSide);var old=pending.State.Pending[0].QuantIntent!.Assessment.Plan!;
        var price=f.Markets[0].Price!.Value+(shortSide?.1m:-.1m);var input=QuantLiveTests.Input(f,f.CapturedAt.AddSeconds(3),price);
        var result=QuantPaperEngine.Tick(pending.State,[],[],input);var p=Assert.Single(result.State.Positions);var review=Assert.Single(result.EntryReviews);
        Assert.Equal("Filled",review.Outcome);Assert.True(review.Proposed!.Plan!.Quantity>old.Quantity);Assert.True(QuantEntryEngine.Summary(review).Capped);
        Assert.True(p.Quantity<=old.Quantity);Assert.True(p.Quant!.Plan.EstimatedRisk<=old.EstimatedRisk);Assert.True(p.Quant.Plan.Leverage<=old.Leverage);Assert.Equal(old.Stop,p.StopPrice);Assert.DoesNotContain(review.Values,v=>v.Passed==false);
        Assert.Equal(QuantEntryEngine.Version,p.Quant.EntryImplementationVersion);Assert.Equal(review.Id,p.Quant.EntryReviewId);Assert.Equal(pending.State.Cash-p.InitialMargin-p.OpenFee,result.State.Cash);
    }
    [Theory][InlineData(false)][InlineData(true)]public void AdversePriceStillRespectsBothOriginalAndCurrentRiskBudgets(bool shortSide)
    {
        var (f,pending)=Pending(shortSide);var old=pending.State.Pending[0].QuantIntent!.Assessment.Plan!;var price=f.Markets[0].Price!.Value+(shortSide?-.1m:.1m);
        var result=QuantPaperEngine.Tick(pending.State,[],[],QuantLiveTests.Input(f,f.CapturedAt.AddSeconds(3),price));var p=Assert.Single(result.State.Positions);
        Assert.True(p.Quantity<old.Quantity);Assert.True(p.Quant!.Plan.EstimatedRisk<=old.EstimatedRisk);Assert.DoesNotContain(result.EntryReviews[0].Selected!.Checks,c=>!c.Passed);
    }
    [Fact]public void SerializedEvidenceReproducesBothUnboundedAndChosenPlansWithoutExternalReads()
    {
        var (f,pending)=Pending();var result=QuantPaperEngine.Tick(pending.State,[],[],QuantLiveTests.Input(f,f.CapturedAt.AddSeconds(3),f.Markets[0].Price!.Value-.1m));
        var review=JsonSerializer.Deserialize<QuantEntryReview>(JsonSerializer.Serialize(Assert.Single(result.EntryReviews)))!;
        var replay=QuantEntryEngine.Evaluate(review.Intent,review.Control,review.Evidence,review.At,review.Revision);
        Assert.Equal(QuantRules.HashValue(review.Proposed),QuantRules.HashValue(replay.Proposed));Assert.Equal(QuantRules.HashValue(review.Selected),QuantRules.HashValue(replay.Selected));Assert.Equal("Ready",replay.Outcome);
        Assert.DoesNotContain("\"Frames\"",JsonSerializer.Serialize(result.State));Assert.True(review.Evidence.Frames.All(frame=>frame.Bars.Count<=review.Control.Parameters.CorrelationHours+1));
    }
    [Fact]public void APreviouslyApprovedLowerLeverageIsNeverRaisedToTheGlobalMaximum()
    {
        var (f,pending)=Pending();var intent=pending.State.Pending[0];var approved=intent.QuantIntent!.Assessment.Plan!;
        approved=approved with{Leverage=2,Margin=approved.Quantity*approved.Entry/2};intent=intent with{QuantIntent=intent.QuantIntent with{Assessment=intent.QuantIntent.Assessment with{Plan=approved}}};
        var fill=QuantPaperEngine.Tick(pending.State with{Pending=[intent]},[],[],QuantLiveTests.Input(f,f.CapturedAt.AddSeconds(3)));var review=Assert.Single(fill.EntryReviews);var position=Assert.Single(fill.State.Positions);
        Assert.Equal(5,review.Proposed!.Plan!.Leverage);Assert.Equal(2,position.Quant!.Plan.Leverage);Assert.Equal(approved.Stop,position.StopPrice);Assert.True(position.Quantity<=approved.Quantity);
    }
    [Theory][InlineData(false)][InlineData(true)]public void FundingCrowdingRemainsRejectedWithTheSpecificFailure(bool shortSide)
    {
        var (f,pending)=Pending(shortSide);var input=QuantLiveTests.Input(f,f.CapturedAt.AddSeconds(3));input=input with{Markets=input.Markets.Select(m=>m with{FundingRate=shortSide?-.01m:.01m}).ToArray()};
        var result=QuantPaperEngine.Tick(pending.State,[],[],input);Assert.Empty(result.State.Positions);Assert.Empty(result.State.Pending);var review=Assert.Single(result.EntryReviews);
        Assert.Equal("Cancelled",review.Outcome);Assert.Contains("资金费",review.Reason);Assert.Contains(review.Selected!.Checks,c=>c.Key=="Funding"&&!c.Passed);
    }
    [Fact]public void WaitingForSubsequentQuotesIsRecordedAndNeverFillsInTheIntentTick()
    {
        var (f,pending)=Pending();var result=QuantPaperEngine.Tick(pending.State,[],[],QuantLiveTests.Input(f,f.CapturedAt));Assert.Empty(result.State.Positions);Assert.Single(result.State.Pending);
        var review=Assert.Single(result.EntryReviews);Assert.Equal("Waiting",review.Outcome);Assert.Equal("SubsequentQuotes",review.Phase);Assert.Null(review.Selected);
    }
    [Fact]public void ReservedCashCannotBeBorrowedFromAnotherUnfilledIntent()
    {
        var (f,pending)=Pending();var at=f.CapturedAt.AddSeconds(3);var intent=pending.State.Pending[0];var input=QuantLiveTests.Input(f,at);var portfolio=new QuantPortfolio(pending.State.Revision,at,1000,1000,1000,[]);
        var evidence=QuantEntryEngine.Evidence(intent,pending.State.QuantControl!,input,portfolio,1000,[],true);var review=QuantEntryEngine.Evaluate(intent,pending.State.QuantControl!,evidence,at,3);
        Assert.Equal("Cancelled",review.Outcome);Assert.Contains(review.Values,v=>v.Key=="Cash"&&v.Passed==false&&v.Selected==0);Assert.Contains("现金",review.Reason);
    }
    [Fact]public void AChangedRoundedStopIsRejectedRatherThanMovedToObtainAPlan()
    {
        var (f,pending)=Pending();var intent=pending.State.Pending[0];intent=intent with{QuantIntent=intent.QuantIntent! with{Assessment=intent.QuantIntent.Assessment with{Plan=intent.QuantIntent.Assessment.Plan! with{Stop=intent.QuantIntent.Assessment.Plan!.Stop-.001m}}}};
        var result=QuantPaperEngine.Tick(pending.State with{Pending=[intent]},[],[],QuantLiveTests.Input(f,f.CapturedAt.AddSeconds(3)));
        Assert.Empty(result.State.Positions);Assert.Equal("Cancelled",result.EntryReviews[0].Outcome);Assert.Contains("止损",result.EntryReviews[0].Reason);
    }
    [Fact]public void ExpiredSignalsAndUnknownFundingCannotBeRescuedBySmallerSizing()
    {
        var (f,pending)=Pending();var intent=pending.State.Pending[0];var at=f.CapturedAt.AddSeconds(3);var input=QuantLiveTests.Input(f,at);var portfolio=new QuantPortfolio(2,at,1000,1000,1000,[]);
        var evidence=QuantEntryEngine.Evidence(intent,pending.State.QuantControl!,input,portfolio,0,[new(Guid.NewGuid(),"OTHER","Unknown",null,at,"missing")],true);
        Assert.Equal("Waiting",QuantEntryEngine.Evaluate(intent,pending.State.QuantControl!,evidence,at,3).Outcome);
        var expired=QuantEntryEngine.Evaluate(intent,pending.State.QuantControl!,evidence,intent.QuantIntent!.Decision.ExpiresAt.AddSeconds(1),3);Assert.Equal("Cancelled",expired.Outcome);Assert.Contains("过期",expired.Reason);
    }
    [Fact]public void ReviewPositionAndCashCommitAtomicallyAndDuplicateReviewRollsEverythingBack()
    {
        using var temp=new Database();var (f,pending)=Pending();var initial=QuantLiveTests.State(f) with{Revision=1};temp.Db.SavePaper(new(initial,0,[]));temp.Db.SavePaper(pending);
        var fill=QuantPaperEngine.Tick(pending.State,[],[],QuantLiveTests.Input(f,f.CapturedAt.AddSeconds(3)));var review=Assert.Single(fill.EntryReviews);
        using(var c=temp.Db.Open())c.Execute("INSERT INTO PaperEntryReview VALUES(@Id,@Time,@Revision,@DecisionId,@Summary,@Payload)",new{review.Id,Time=review.At.ToUnixTimeMilliseconds(),review.Revision,DecisionId=review.Intent.QuantIntent!.Decision.Id,Summary=Encode(QuantEntryEngine.Summary(review)),Payload=Encode(review)});
        Assert.Throws<SqliteException>(()=>temp.Db.SavePaper(fill));var repo=new PaperRepository(temp.Db);Assert.Equal(pending.State.Cash,repo.Restore()!.Cash);Assert.Empty(repo.Restore()!.Positions);
        using(var c=temp.Db.Open())c.Execute("DELETE FROM PaperEntryReview WHERE Id=@id",new{id=review.Id});temp.Db.SavePaper(fill);
        var restored=new PaperRepository(temp.Db);Assert.Equal(fill.State.Cash,restored.Restore()!.Cash);Assert.Single(restored.Restore()!.Positions);Assert.Equal(QuantRules.HashValue(review),QuantRules.HashValue(restored.EntryReview(review.Id)));
        var summaries=restored.EntryReviews(f.CapturedAt.AddMinutes(-1),f.CapturedAt.AddMinutes(1),1);Assert.Single(summaries.Records);Assert.True(summaries.Truncated);Assert.NotNull(restored.FirstEntryReviewAt());
        Assert.Throws<MappingConflictException>(()=>temp.Db.SavePaper(fill));Assert.Equal(2,restored.EntryReviews(f.CapturedAt.AddMinutes(-1),f.CapturedAt.AddMinutes(1)).Records.Count);
    }
    [Fact]public void ReviewFromAnotherRevisionCannotBeAttachedToTheAccountBatch()
    {
        using var temp=new Database();var (f,pending)=Pending();var initial=QuantLiveTests.State(f) with{Revision=1};temp.Db.SavePaper(new(initial,0,[]));
        Assert.Throws<InvalidDataException>(()=>temp.Db.SavePaper(pending with{EntryReviews=[pending.EntryReviews[0] with{Revision=3}]}));Assert.Equal(1,new PaperRepository(temp.Db).Restore()!.Revision);
    }
    [Fact]public async Task UserPauseCancelsPendingIntentsWithAnAtomicTerminalReviewAndPreservesCash()
    {
        using var temp=new Database();var (f,pending)=Pending();temp.Db.SavePaper(new(QuantLiveTests.State(f) with{Revision=1},0,[]));temp.Db.SavePaper(pending);var repository=new PaperRepository(temp.Db);
        var service=new PaperService(repository,new Direct(temp.Db),null!,null!,new Clock(f.CapturedAt.AddSeconds(1)),null!,null!);
        await service.SetEnabledAsync(false,default);var restored=repository.Restore()!;Assert.Empty(restored.Pending);Assert.False(restored.Enabled);Assert.Equal(pending.State.Cash,restored.Cash);
        var reviews=repository.EntryReviews(f.CapturedAt.AddMinutes(-1),f.CapturedAt.AddMinutes(1));Assert.Equal(2,reviews.Records.Count);Assert.Equal("Cancelled",reviews.Records[0].Outcome);Assert.Equal("UserControl",reviews.Records[0].Phase);Assert.Contains("暂停",reviews.Records[0].Reason);Assert.Contains(restored.Decisions,d=>d.Action=="取消新意向");
    }
    private sealed class Direct(SqliteDatabase db):IPaperPersistence {public Task SaveAsync(PaperBatch batch,CancellationToken ct){db.SavePaper(batch);return Task.CompletedTask;}}
    private sealed class Clock(DateTimeOffset at):TimeProvider {public override DateTimeOffset GetUtcNow()=>at;}
    private static byte[] Encode<T>(T value){using var stream=new MemoryStream();using(var gzip=new GZipStream(stream,CompressionLevel.Fastest,true))JsonSerializer.Serialize(gzip,value);return stream.ToArray();}
    private sealed class Database:IDisposable
    {
        private readonly string root=Path.Combine(Path.GetTempPath(),"Pullet-Entry-"+Guid.NewGuid().ToString("N"));public SqliteDatabase Db{get;}
        public Database(){Db=new(Options.Create(new StorageOptions{Directory=root}));Db.Initialize();}
        public void Dispose(){SqliteConnection.ClearAllPools();var full=Path.GetFullPath(root);if(!full.StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException();if(Directory.Exists(full))Directory.Delete(full,true);}
    }
}
