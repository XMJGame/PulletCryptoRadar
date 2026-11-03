using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.IntegrationTests;
public sealed class OnChainScheduleTests
{
    private static readonly DateTimeOffset Now=new(2026,10,31,1,0,0,TimeSpan.Zero);
    private static ChainBinding Binding(int id)=>new(id,"bsc","0x"+id.ToString("x40"),"coin-"+id,true,true,1,Now,"Fixture",[]);
    private static ChainAttempt Attempt(ChainBinding b,DateTimeOffset next)=>new(b.Chain,b.ContractAddress,Now.AddHours(-6),next,null);
    [Fact] public void ConfigurationsBeyondTheFirstFiveAreStillCollectedWhenThoseFiveAreCoolingDown()
    {
        var bindings=Enumerable.Range(1,9).Select(Binding).ToArray();
        var attempts=bindings.Take(5).Select(b=>Attempt(b,Now.AddMinutes(30))).ToArray();
        var batch=OnChainService.DueBatch(bindings,attempts,new HashSet<int>{1,2,3},Now,5);
        Assert.Equal(new[]{6,7,8,9},batch.Select(b=>b.AssetId));
    }
    [Fact] public void NewTargetsRotateAfterReservationAndDisabledOrBackoffTargetsStayOut()
    {
        var bindings=Enumerable.Range(1,8).Select(Binding).ToArray();bindings[7]=bindings[7] with{Enabled=false};
        var first=OnChainService.DueBatch(bindings,[],new HashSet<int>(),Now,5);
        var attempts=first.Select(b=>Attempt(b,Now.AddHours(6))).Append(Attempt(bindings[5],Now.AddHours(2)) with{Error="HTTP 429"}).ToArray();
        Assert.Equal(new[]{7},OnChainService.DueBatch(bindings,attempts,new HashSet<int>(),Now,5).Select(b=>b.AssetId));
    }
    [Fact] public void OldestDueTaskPreventsPermanentStarvationAndWatchPriorityBreaksTies()
    {
        var bindings=Enumerable.Range(1,4).Select(Binding).ToArray();
        var attempts=bindings.Select(b=>Attempt(b,b.AssetId==4?Now.AddHours(-2):Now.AddHours(-1))).ToArray();
        Assert.Equal(new[]{4,3,1},OnChainService.DueBatch(bindings,attempts,new HashSet<int>{3},Now,3).Select(b=>b.AssetId));
    }
}
