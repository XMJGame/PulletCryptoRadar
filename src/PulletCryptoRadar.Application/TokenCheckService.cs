using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Application;
public sealed class TokenCheckService(ICapitalRuntimeStore capital,IMarketRuntimeStore markets,ITokenCheckProvider projectProvider,
    IMarketRestProvider prices,IPriorityQuoteProvider priority,IDerivativeRestProvider derivatives,IOnChainProvider onChainProvider,
    OnChainService onChain,IOnChainRepository chainRepository,OnChainOptions chainOptions,ITokenCheckRepository reports,TimeProvider time,
    ITokenResearchRepository? research=null,IFlowRepository? flows=null,ITokenFollowupRepository? followups=null,IAssetRepository? history=null)
{
    private readonly SemaphoreSlim gate=new(1,1);
    public IReadOnlyList<TokenCheckCandidate> Search(string query)
    {
        query=query.Trim();if(query.Length is <2 or >100)return [];
        var assets=markets.GetAll().Select(m=>m.Instrument).Where(i=>i.Active).ToArray();
        var q=query.EndsWith("USDT",StringComparison.OrdinalIgnoreCase)?query[..^4]:query;
        return (capital.Catalog?.Coins??[]).Where(c=>c.Symbol.Equals(q,StringComparison.OrdinalIgnoreCase)||c.Id.Contains(query,StringComparison.OrdinalIgnoreCase)
                ||c.Name.Contains(query,StringComparison.OrdinalIgnoreCase)||c.Platforms.Any(p=>TokenCheckEngine.Same(p.Key,p.Value,query)))
            .Select(c=>new TokenCheckCandidate(c,assets.Where(i=>i.BaseAsset.Equals(c.Symbol,StringComparison.OrdinalIgnoreCase)).ToArray(),
                c.Symbol.Equals(q,StringComparison.OrdinalIgnoreCase)||c.Id.Equals(query,StringComparison.OrdinalIgnoreCase)||c.Name.Equals(query,StringComparison.OrdinalIgnoreCase)||c.Platforms.Any(p=>TokenCheckEngine.Same(p.Key,p.Value,query))))
            .OrderByDescending(c=>c.ExactMatch).ThenBy(c=>c.Coin.Id,StringComparer.Ordinal).Take(25).ToArray();
    }
    public IReadOnlyList<TokenCheckReport> Recent()=>reports.Recent(12);
    public TokenCheckReport? Get(Guid id)=>reports.Get(id);
    public async Task<TokenFollowup?> FollowupAsync(Guid id,CancellationToken ct)
    {
        var report=reports.Get(id);if(report is null)return null;
        var now=time.GetUtcNow();
        var candles=report.AssetId is {} asset&&markets.Get(asset)?.Instrument.Symbol==report.Instrument?
            ForwardEngine.Hourly(history is not null?await history.ReadHourlyHistoryAsync(asset,400,ct):[],markets.GetCandles(asset,5),now):[];
        return TokenFollowupEngine.Evaluate(report,candles,time.GetUtcNow(),followups?.Get(id));
    }
    public async Task<TokenCheckReport> CheckAsync(TokenCheckRequest request,CancellationToken ct)
    {
        if(!await gate.WaitAsync(0,ct))throw new InvalidOperationException("另一份快查正在生成，请等待完成");
        try
        {
            if(string.IsNullOrWhiteSpace(request.CoinGeckoId)||string.IsNullOrWhiteSpace(request.Platform)||string.IsNullOrWhiteSpace(request.ContractAddress))throw new ArgumentException("项目ID、链与完整合约/Mint不能为空");
            var now=time.GetUtcNow();var coin=capital.Catalog?.Coins.FirstOrDefault(c=>c.Id==request.CoinGeckoId)??throw new ArgumentException("项目ID不在当前目录中，请重新搜索");
            if(!coin.Platforms.TryGetValue(request.Platform,out var address)||!TokenCheckEngine.Same(request.Platform,address,request.ContractAddress))throw new ArgumentException("所选链与完整合约/Mint不匹配，禁止按同名自动分析");
            request=request with{ContractAddress=address};
            var chain=TokenCheckEngine.Chain(request.Platform);
            if(chain is not null&&!ChainIdentity.Address(chain,request.ContractAddress))throw new ArgumentException("所选链的合约/Mint格式无效");
            var instrument=request.AssetId is {} asset?markets.Get(asset)?.Instrument:null;
            if(request.AssetId is not null&&(instrument is null||!instrument.Active||!instrument.BaseAsset.Equals(coin.Symbol,StringComparison.OrdinalIgnoreCase)))throw new ArgumentException("交易对不属于该项目代码，请重新选择");
            var recent=reports.Recent(100);
            var cached=recent.FirstOrDefault(r=>r.Version==TokenCheckEngine.Version&&r.Coin.Id==coin.Id&&r.Platform==request.Platform&&r.ContractAddress==request.ContractAddress&&r.AssetId==request.AssetId&&now>=r.AsOf&&now-r.AsOf<TimeSpan.FromMinutes(1));
            if(cached is not null)return cached;
            if(recent.Count(r=>r.AsOf>now.AddHours(-1))>=12||recent.Count(r=>r.AsOf.ToOffset(TimeSpan.FromHours(8)).Date==now.ToOffset(TimeSpan.FromHours(8)).Date)>=48)
                throw new InvalidOperationException("按需快查额度已达每小时12份或每日48份，已有报告仍可查看");
            var sources=new List<TokenCheckSource>();var failures=new List<string>();var retryTimes=new Dictionary<string,DateTimeOffset>();
            async Task<T?> Read<T>(Func<Task<T>> work,string label,string url)where T:class
            {
                try{return await work();}
                catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
                catch(Exception ex)when(ex is HttpRequestException or OperationCanceledException or InvalidDataException or System.Text.Json.JsonException or OnChainProviderException or NetworkBudgetExceededException)
                {
                    var note=ex is OnChainProviderException or InvalidDataException or NetworkBudgetExceededException?ex.Message:label+"本次采集失败，未用旧时间冒充实时";
                    if(ex is OnChainProviderException limited)retryTimes[label]=limited.RetryAt;
                    failures.Add(note);sources.Add(new(label,"Unavailable",null,time.GetUtcNow(),url,note));return null;
                }
            }
            var geckoUrl=$"https://www.coingecko.com/en/coins/{Uri.EscapeDataString(coin.Id)}";
            var project=await Read(()=>projectProvider.ProjectAsync(coin.Id,ct),"CoinGecko项目资料",geckoUrl);
            if(project is not null)sources.Add(new("CoinGecko项目资料",TokenCheckEngine.Fresh(project.SourceTime,project.ReceivedTime,time.GetUtcNow(),TimeSpan.FromHours(1))?"Valid":"Stale",project.SourceTime,project.ReceivedTime,geckoUrl,"按精确ID获取；USD价格/市值/现货聚合成交额与永续数据分列"));
            var gateUrl=$"https://api.gateio.ws/api/v4/spot/currencies/{Uri.EscapeDataString(coin.Symbol.ToUpperInvariant())}";
            var venue=await Read(()=>projectProvider.IdentityAsync(coin.Symbol.ToUpperInvariant(),ct),"Gate链合约元数据",gateUrl);
            if(venue is not null)sources.Add(new("Gate链合约元数据","Observed",venue.SourceTime,venue.ReceivedTime,venue.EvidenceUrl,"只核对身份，不把Gate暂停充提等同其他交易所下架"));
            var cross=TokenCheckEngine.IdentityMatches(coin,request.Platform,request.ContractAddress,venue,time.GetUtcNow());
            var mapping=instrument is not null?capital.GetMapping(instrument.AssetId):null;
            var mapped=mapping?.Status is MappingStatus.ContractVerified or MappingStatus.ManualVerified&&mapping.CoinGeckoId==coin.Id
                &&mapping.Platform==request.Platform&&mapping.ContractAddress is {} contract&&TokenCheckEngine.Same(request.Platform,contract,request.ContractAddress);
            var identity=cross||mapped;
            if(project is not null&&(!project.Platforms.TryGetValue(request.Platform,out var currentAddress)||!TokenCheckEngine.Same(request.Platform,currentAddress,request.ContractAddress)))
            {identity=false;failures.Add("项目详情与本地目录的链合约发生冲突，停止关联交易对数据");}
            var reason=identity?cross?"本次Gate链合约元数据与精确CoinGecko ID/链/完整地址一致":"复用已人工核验的完整ID/链/合约映射；本次独立元数据可能不可用":"独立链合约证据未对应；选择候选并不等于已证明身份";
            TickerEvent? ticker=null;MarkPriceEvent? mark=null;IReadOnlyList<Candle> candles=[];IReadOnlyList<DerivativeSample> oi=[];OnChainSample? sample=null;
            if(identity&&instrument is not null)
            {
                var url=$"https://www.binance.com/en/futures/{instrument.Symbol}";
                var quotes=await Read(()=>priority.GetPriorityQuotesAsync(instrument.Symbol,ct),"Binance永续报价",url);
                ticker=quotes?.OfType<TickerEvent>().SingleOrDefault(q=>q.Symbol==instrument.Symbol);mark=quotes?.OfType<MarkPriceEvent>().SingleOrDefault(q=>q.Symbol==instrument.Symbol);
                if(ticker is not null)sources.Add(new("Binance永续报价","Observed",ticker.SourceTime,ticker.ReceivedTime,url,"该合约报价成交额不代表现货深度；资金费率按本次原值，不假设固定8小时结算"));
                candles=await Read(()=>prices.GetCandlesAsync(instrument.Symbol,60,169,time.GetUtcNow(),ct),"Binance闭合小时线",url)??[];
                if(candles.Count>0)sources.Add(new("Binance闭合小时线","Observed",candles.Max(b=>b.SourceTime),candles.Max(b=>b.ReceivedTime),url,"按需169根；结构拐点须有右侧两根闭合线；不向模拟仓或全市场缓存回填"));
                oi=await Read(()=>derivatives.GetSeriesAsync(instrument.Symbol,DerivativeKind.OpenInterest5m,289,ct),"Binance合约OI数量",url)??[];
                if(oi.Count>0)sources.Add(new("Binance合约OI数量","Observed",oi.Max(s=>s.SourceTime),oi.Max(s=>s.ReceivedTime),url,"比较同合约数量，24h间隔允许10分钟误差，不能用价格上涨造成的名义额变化代替增仓"));
            }
            if(identity&&chain is not null)
            {
                sample=chainRepository.History(chain,request.ContractAddress,1).FirstOrDefault();
                if(sample is null||!TokenCheckEngine.Fresh(sample.SourceTime,sample.ReceivedTime,time.GetUtcNow(),TimeSpan.FromMinutes(45)))
                {
                    var binding=new ChainBinding(request.AssetId??0,chain,ChainIdentity.Normalize(chain,request.ContractAddress),coin.Id,true,true,0,time.GetUtcNow(),"Quick-check only: exact venue/catalog contract cross-check",[gateUrl,geckoUrl]);
                    var at=time.GetUtcNow();var attempt=new ChainAttempt(chain,binding.ContractAddress,at,at.AddHours(6),"快查采集已预留，等待返回");
                    if(await onChain.ReserveAsync(attempt,chainOptions,ct))
                    {
                        var captured=await Read(()=>onChainProvider.FetchAsync(binding,ct),"链上索引器",geckoUrl);
                        if(captured is not null)
                        {
                            sample=captured;var retry=captured.Security?.RetryAt;
                            await onChain.SaveSampleAsync(new(captured,attempt with{Error=captured.Security?.Error,NextAttempt=retry>attempt.NextAttempt?retry.Value:attempt.NextAttempt}),ct);
                        }
                        else
                        {
                            var retry=time.GetUtcNow().AddMinutes(30);if(retryTimes.TryGetValue("链上索引器",out var providerRetry)&&providerRetry>retry)retry=providerRetry;
                            await onChain.SaveSampleAsync(new(null,attempt with{NextAttempt=retry,Error="快查链上采集未完成；保留冷却与原始缓存"}),ct);
                        }
                    }
                    else failures.Add("链上任务处于共享预算/冷却窗口，本次不绕过调度；已有快照保留原时间");
                }
                if(sample is not null)
                {
                    var url=sample.Provider=="Binplorer"?$"https://binplorer.com/token/{bindingAddress()}":sample.Provider=="Ethplorer"?$"https://ethplorer.io/address/{bindingAddress()}":sample.Security?.EvidenceUrl??geckoUrl;
                    sources.Add(new(sample.Provider,TokenCheckEngine.Fresh(sample.SourceTime,sample.ReceivedTime,time.GetUtcNow(),TimeSpan.FromMinutes(45))?"Valid":"Stale",sample.SourceTime,sample.ReceivedTime,url,sample.TimeBasis));
                    if(sample.Security is {} security)sources.Add(new(security.Provider+"安全检测",security.Error is not null?"Unavailable":TokenCheckEngine.Fresh(security.SourceTime,security.ReceivedTime,time.GetUtcNow(),TimeSpan.FromMinutes(45))?"Valid":"Stale",security.SourceTime,security.ReceivedTime,security.EvidenceUrl,security.Error??"提供者检测声明，不自动确认地址归属或保证安全"));
                }
                string bindingAddress()=>Uri.EscapeDataString(request.ContractAddress);
            }
            var report=TokenCheckEngine.Build(coin,request,identity,reason,project,ticker,mark,candles,oi,sample,sources,failures,time.GetUtcNow(),instrument?.Symbol);
            if(chain is not null)
            {
                await onChain.RestoreAsync(ct);
                var cursor=flows?.Cursor(chain,request.ContractAddress);
                var transfers=identity?flows?.Transfers(chain,request.ContractAddress,report.AsOf.AddHours(-24),1000)??[]:[];
                var clues=(research?.Claims()??[]).Where(c=>c.CoinGeckoId==coin.Id).ToArray();
                var holders=TokenHolderResearch.Analyze(chain,request.ContractAddress,identity,sample,identity?chainRepository.History(chain,request.ContractAddress,40):[],onChain.Labels(),transfers,cursor,report.AsOf,clues);
                report=TokenResearchEngine.Enrich(report,holders,TokenResearchEngine.Analyze(report,research?.Claims()??[]));
            }
            else report=report with{Research=TokenResearchEngine.Analyze(report,research?.Claims()??[])};
            reports.Save(report);return report;
        }
        finally{gate.Release();}
    }
}
