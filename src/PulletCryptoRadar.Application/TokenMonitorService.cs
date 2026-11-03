using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Application;
public sealed class TokenMonitorService(ITokenMonitorRepository repository,ITokenCheckRepository reports,ICapitalRuntimeStore capital,
    ITokenCheckProvider identities,ITokenMonitorProvider provider,IOnChainProvider holderProvider,OnChainService onChain,IOnChainRepository chainHistory,
    OnChainOptions chainOptions,ITokenResearchRepository research,TimeProvider time)
{
    private readonly SemaphoreSlim gate=new(1,1);private readonly object cacheGate=new();
    private readonly Dictionary<string,TokenMonitorState> states=[];private readonly Dictionary<string,IReadOnlyList<TokenMonitorSnapshot>> histories=[];
    private TokenMonitorState Load(TokenMonitorTarget target)
    {
        lock(cacheGate)
        {
            if(states.TryGetValue(target.Id,out var state))return state;
            state=repository.State(target.Id)??new(target.Id,null,false,DateTimeOffset.MinValue,DateTimeOffset.MinValue,DateTimeOffset.MinValue,null,null,false,null);
            if(state.TargetId!=target.Id)throw new InvalidDataException("持续研究状态目标不匹配");
            states[target.Id]=state;histories[target.Id]=repository.History(target.Id,400);return state;
        }
    }
    private void Save(TokenMonitorState state,TokenMonitorSnapshot? snapshot=null)
    {
        repository.Save(state,snapshot);lock(cacheGate)
        {
            states[state.TargetId]=state;
            if(snapshot is not null)histories[state.TargetId]=histories.GetValueOrDefault(state.TargetId,[]).Append(snapshot).OrderByDescending(s=>s.SourceTime).Take(400).ToArray();
        }
    }
    public IReadOnlyList<TokenMonitorView> Overview()
    {
        var now=time.GetUtcNow();return repository.Targets().Select(t=>
        {var state=Load(t);IReadOnlyList<TokenMonitorSnapshot> history;lock(cacheGate)history=histories[t.Id];return TokenMonitorEngine.View(t,state,history,onChain.Labels(),research.Claims(),now);}).ToArray();
    }
    public MonitorBudgetView Budget()=>repository.Budget(time.GetUtcNow());
    public static MonitorIdentity? ReportIdentity(TokenMonitorTarget target,IReadOnlyList<TokenCheckReport> rows,DateTimeOffset now)
    {
        var latest=rows.Where(r=>r.Coin.Id==target.CoinGeckoId&&r.Platform==target.Platform&&TokenCheckEngine.Same(r.Platform,r.ContractAddress,target.ContractAddress)
            &&r.AsOf<=now&&now-r.AsOf<=TimeSpan.FromDays(1)).MaxBy(r=>r.AsOf);
        if(latest?.IdentityVerified!=true||!latest.Coin.Platforms.TryGetValue(target.Platform,out var catalog)||!TokenCheckEngine.Same(target.Platform,catalog,target.ContractAddress))return null;
        var evidence=latest.Sources.FirstOrDefault(s=>s.Name=="Gate链合约元数据"&&TokenCheckEngine.Fresh(s.SourceTime,s.ReceivedTime,latest.AsOf,TimeSpan.FromMinutes(45))&&TokenResearchEngine.SafeUrl(s.Url));
        return evidence?.SourceTime is {} source?new(source,latest.AsOf,latest.AsOf.AddDays(7),evidence.Url,"复用当时完整链合约交叉核验的最新报告；固定合约研究留证7天，不用于实时交易映射"):null;
    }
    private static string Error(Exception ex)=>ex is InvalidDataException or OnChainProviderException or NetworkBudgetExceededException or FlowReorgException?ex.Message:"持续研究本轮网络/读取未完成，旧记录和断点保留";
    public async Task TickAsync(CancellationToken ct)
    {
        if(!await gate.WaitAsync(0,ct))return;
        try
        {
            await onChain.RestoreAsync(ct);
            foreach(var target in repository.Targets().OrderBy(t=>Load(t).NextRpcAttempt))
            {
                ct.ThrowIfCancellationRequested();var state=Load(target);var now=time.GetUtcNow();
                var coin=capital.Catalog?.Coins.FirstOrDefault(c=>c.Id==target.CoinGeckoId);
                if(coin is null)continue;
                if(!coin.Platforms.TryGetValue(target.Platform,out var address)||!TokenCheckEngine.Same(target.Platform,address,target.ContractAddress))
                {Save(state with{IdentityRevoked=true,IdentityError="当前目录与固定完整链合约冲突，停止研究采集"});continue;}
                if(state.Identity is null&&!state.IdentityRevoked&&ReportIdentity(target,reports.ForFollowup(now.AddDays(-1)),now) is {} pinned)
                {state=state with{Identity=pinned,NextIdentityAttempt=now.AddHours(6),IdentityError=null};Save(state);}
                if(state.NextIdentityAttempt<=now)
                {
                    state=state with{NextIdentityAttempt=now.AddMinutes(30)};Save(state);
                    try
                    {
                        var venue=await identities.IdentityAsync(target.Symbol,ct);var matched=TokenCheckEngine.IdentityMatches(coin,target.Platform,target.ContractAddress,venue,time.GetUtcNow());
                        state=matched?state with{Identity=new(venue.SourceTime,time.GetUtcNow(),time.GetUtcNow().AddDays(7),venue.EvidenceUrl,"本次精确ID/链/完整合约交叉核验；仅固定合约研究"),IdentityRevoked=false,IdentityError=null,NextIdentityAttempt=time.GetUtcNow().AddHours(6)}:
                            state with{IdentityRevoked=true,IdentityError="独立来源的完整合约/下架状态与研究目标冲突，停止采集"};
                    }
                    catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
                    catch(Exception ex)when(ex is HttpRequestException or OperationCanceledException or InvalidDataException or System.Text.Json.JsonException or OnChainProviderException or NetworkBudgetExceededException)
                    {state=state with{IdentityError=Error(ex),NextIdentityAttempt=ex is OnChainProviderException p&&p.RetryAt>state.NextIdentityAttempt?p.RetryAt:state.NextIdentityAttempt};}
                    Save(state);
                }
                now=time.GetUtcNow();if(!TokenMonitorEngine.ActiveIdentity(state,now))continue;
                if(state.NextHolderAttempt<=now)
                {
                    state=state with{NextHolderAttempt=now.AddMinutes(30)};Save(state);
                    var attempt=new ChainAttempt("bsc",target.ContractAddress,now,now.AddHours(6),"持续研究共享额度预留");
                    if(await onChain.ReserveAsync(attempt,chainOptions,ct))
                    {
                        var binding=new ChainBinding(0,"bsc",target.ContractAddress,target.CoinGeckoId,true,true,0,now,"Pinned independent research target",[state.Identity!.EvidenceUrl]);
                        try
                        {
                            var sample=await holderProvider.FetchAsync(binding,ct);var retry=sample.Security?.RetryAt;
                            await onChain.SaveSampleAsync(new(sample,attempt with{NextAttempt=retry>attempt.NextAttempt?retry.Value:attempt.NextAttempt,Error=sample.Security?.Error}),ct);
                            state=state with{NextHolderAttempt=now.AddHours(6)};Save(state);
                        }
                        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
                        catch(Exception ex)when(ex is HttpRequestException or OperationCanceledException or InvalidDataException or System.Text.Json.JsonException or OnChainProviderException or NetworkBudgetExceededException)
                        {var retry=ex is OnChainProviderException p&&p.RetryAt>attempt.NextAttempt?p.RetryAt:attempt.NextAttempt;await onChain.SaveSampleAsync(new(null,attempt with{NextAttempt=retry,Error=Error(ex)}),ct);}
                    }
                }
                now=time.GetUtcNow();if(state.NextRpcAttempt>now)continue;
                var sampleRows=chainHistory.History("bsc",target.ContractAddress,1).FirstOrDefault()?.Holders??[];
                var observed=sampleRows.Count>0?sampleRows.Take(10).Select(h=>h.Address):state.Latest?.Balances.Select(b=>b.Address)??[];
                var selected=observed.Concat(target.VestingContract is {} vest?[vest]:[]).Concat(state.Latest?.Vesting?.Allocations.Select(a=>a.Recipient)??[])
                    .Where(a=>ChainIdentity.Address("bsc",a)&&a!="0x"+new string('0',40)).Select(a=>a.ToLowerInvariant()).Distinct().Take(16).ToArray();
                if(selected.Length==0){Save(state with{RpcError="等待持币样本，不启动空地址观察",NextRpcAttempt=now.AddMinutes(30)});continue;}
                if(!repository.Reserve(now,64)){Save(state with{RpcError="持续研究RPC预留额度已达上限，等待下一窗口",NextRpcAttempt=now.AddMinutes(30)});continue;}
                state=state with{NextRpcAttempt=now.AddMinutes(30),RpcError="研究采集中；仅已提交快照可用"};Save(state);
                try
                {
                    var snapshot=await provider.FetchAsync(target,selected,state.Latest,ct);
                    if(snapshot.TargetId!=target.Id||snapshot.ContractAddress!=target.ContractAddress)throw new InvalidDataException("RPC研究快照身份不符");
                    IReadOnlyList<TokenMonitorSnapshot> history;lock(cacheGate)history=histories[target.Id];
                    var view=TokenMonitorEngine.View(target,state with{Latest=snapshot,RpcError=null},history,onChain.Labels(),research.Claims(),time.GetUtcNow());
                    snapshot=TokenMonitorEngine.FreezeFinding(snapshot,view,time.GetUtcNow());
                    state=state with{Latest=snapshot,RpcError=null};Save(state,snapshot);
                }
                catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
                catch(Exception ex)when(ex is HttpRequestException or OperationCanceledException or InvalidDataException or System.Text.Json.JsonException or OnChainProviderException or NetworkBudgetExceededException or FlowReorgException)
                {state=state with{RpcError=Error(ex),Halted=ex is FlowReorgException,NextRpcAttempt=ex is OnChainProviderException p&&p.RetryAt>state.NextRpcAttempt?p.RetryAt:state.NextRpcAttempt};Save(state);}
            }
        }
        finally{gate.Release();}
    }
}
