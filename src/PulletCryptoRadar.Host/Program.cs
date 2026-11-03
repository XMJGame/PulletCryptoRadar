using Microsoft.Extensions.Options;
using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Host;
using PulletCryptoRadar.Infrastructure;
using PulletCryptoRadar.Providers.Binance;
using PulletCryptoRadar.Providers.Market;
using PulletCryptoRadar.Providers.OnChain;
using PulletCryptoRadar.Scanner;
using Serilog;

ConsoleSetup.Initialize();
if(OperatingSystem.IsWindows()&&!Console.IsOutputRedirected)Console.Title="PulletCryptoRadar Server - http://localhost:5218";

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Configuration.AddEnvironmentVariables().AddCommandLine(args);
builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://localhost:5218");
builder.WebHost.UseStaticWebAssets();
Directory.SetCurrentDirectory(AppContext.BaseDirectory);
builder.Host.UseSerilog((context, services, logger) => logger.MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("System.Net.Http.HttpClient", Serilog.Events.LogEventLevel.Warning)
    .WriteTo.Console().WriteTo.File("Logs/PulletCryptoRadar-.log", rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14));
ServerInstanceLease serverLease;
try{serverLease=new ServerInstanceLease(builder.Configuration["Storage:Directory"]??"Data");}
catch(InvalidOperationException ex){Console.Error.WriteLine(ex.Message);Environment.ExitCode=2;return;}
using var serverOwnership=serverLease;
builder.Services.Configure<HostOptions>(options=>options.ShutdownTimeout=TimeSpan.FromSeconds(45));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddOptions<NetworkOptions>().BindConfiguration("Network").Validate(o=>o.IsValid(),"Invalid network settings").ValidateOnStart();
builder.Services.AddSingleton(s=>s.GetRequiredService<IOptions<NetworkOptions>>().Value);
builder.Services.AddSingleton<IOpportunityRepository,OpportunityRepository>();
builder.Services.AddSingleton<OpportunityService>();
builder.Services.AddSingleton<NetworkUsage>();
builder.Services.AddSingleton<PulletCryptoRadar.Host.MarketFoundation>();
builder.Services.AddSingleton<PulletCryptoRadar.Host.ProviderRouter>();
builder.Services.AddHostedService<PulletCryptoRadar.Host.MarketFoundationWorker>();
builder.Services.AddHttpClient("MarketFoundation", http => http.Timeout = TimeSpan.FromSeconds(12)).RemoveAllLoggers();
builder.Services.AddSingleton<INetworkTraffic>(s=>s.GetRequiredService<NetworkUsage>());
builder.Services.AddTransient<MeteredNetworkHandler>();
builder.Services.ConfigureHttpClientDefaults(client=>client.ConfigureHttpClient(http=>http.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip"))
    .AddHttpMessageHandler<MeteredNetworkHandler>()
    .ConfigurePrimaryHttpMessageHandler(()=>new HttpClientHandler { AllowAutoRedirect=false,
        UseProxy=builder.Configuration.GetValue<bool>("Network:UseSystemProxy"),
        AutomaticDecompression=System.Net.DecompressionMethods.None }));

builder.Services.AddOptions<BinanceOptions>().BindConfiguration("Binance")
    .Validate(o => o.RequestWeightPerMinute > 0 && o.StatisticsRequestsPerMinute is > 0 and <= 180 && o.HistoryConcurrency is > 0 and <= 10
        && o.StreamSymbolsPerConnection is > 0 and <= 300 && o.MinuteHistoryCount is >= 65 and <= 2880
        && o.FiveMinuteHistoryCount is >= 40 and <= 2016 && o.FifteenMinuteHistoryCount is >= 40 and <= 2880
        && o.ReceiveTimeoutSeconds >= 5 && Uri.TryCreate(o.RestBaseUrl, UriKind.Absolute, out var rest) && rest.Scheme == "https"
        && Uri.TryCreate(o.WebSocketUrl, UriKind.Absolute, out var ws) && ws.Scheme == "wss", "Invalid Binance settings").ValidateOnStart();
builder.Services.AddOptions<StorageOptions>().BindConfiguration("Storage")
    .Validate(o => o.Snapshot5mRetentionDays > 0 && o.BackupRetentionCount > 0, "Invalid storage retention").ValidateOnStart();
builder.Services.AddSingleton<IMarketEventBus, MarketEventBus>();
builder.Services.AddSingleton<IMarketRuntimeStore, MarketRuntimeStore>();
builder.Services.AddSingleton<DerivativeRuntimeStore>();
builder.Services.AddSingleton<IDerivativeRuntimeStore>(s => s.GetRequiredService<DerivativeRuntimeStore>());
builder.Services.AddSingleton<DerivativeFeatureEngine>();
builder.Services.AddSingleton<DerivativeRepository>();
builder.Services.AddSingleton<DerivativeService>();
builder.Services.AddSingleton<ICapitalRuntimeStore, CapitalRuntimeStore>();
builder.Services.AddSingleton<CapitalFeatureEngine>();
builder.Services.AddSingleton<AssetMappingEngine>();
builder.Services.AddSingleton<IMappingRepository, MappingRepository>();
builder.Services.AddSingleton<IMappingPersistence, MappingPersistence>();
builder.Services.AddSingleton<MappingService>();
builder.Services.AddSingleton<ITokenCheckRepository,TokenCheckRepository>();
builder.Services.AddSingleton<ITokenResearchRepository,TokenResearchRepository>();
builder.Services.AddSingleton<ITokenFollowupRepository,TokenFollowupRepository>();
builder.Services.AddSingleton<ITokenMonitorRepository,TokenMonitorRepository>();
builder.Services.AddSingleton<TokenMonitorService>();
builder.Services.AddSingleton<RuntimeReliabilityService>();
builder.Services.AddSingleton<IDailyResearchRepository,DailyResearchRepository>();
builder.Services.AddSingleton<DailyResearchService>();
builder.Services.AddHttpClient<ITokenMonitorProvider,TokenMonitorProvider>(http=>
{
    var endpoint=Environment.GetEnvironmentVariable("PULLET_BSC_RPC_URL")??"https://bsc-rpc.publicnode.com";
    if(!Uri.TryCreate(endpoint,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.UserInfo.Length>0)throw new InvalidOperationException("BSC research RPC must be HTTPS without user info");
    http.BaseAddress=uri;http.Timeout=TimeSpan.FromSeconds(25);http.DefaultRequestHeaders.UserAgent.ParseAdd("PulletCryptoRadar/1.0");
}).RemoveAllLoggers();
builder.Services.AddSingleton<TokenCheckService>();
builder.Services.AddHttpClient<ITokenCheckProvider,TokenCheckProvider>(http=>{http.Timeout=TimeSpan.FromSeconds(20);http.DefaultRequestHeaders.UserAgent.ParseAdd("PulletCryptoRadar/1.0");}).RemoveAllLoggers();
builder.Services.AddOptions<OnChainOptions>().BindConfiguration("OnChain").Validate(o=>o.IsValid(),"Invalid OnChain settings").ValidateOnStart();
builder.Services.AddSingleton(s=>s.GetRequiredService<IOptions<OnChainOptions>>().Value);
builder.Services.AddSingleton<OnChainFeatureEngine>();
builder.Services.AddSingleton<IOnChainRepository,OnChainRepository>();
builder.Services.AddSingleton<IOnChainPersistence,OnChainPersistence>();
builder.Services.AddSingleton<OnChainService>();
builder.Services.AddSingleton<IOnChainFeatureSource>(s=>s.GetRequiredService<OnChainService>());
builder.Services.AddSingleton<SpeculativeOnChainDetector>();
builder.Services.AddSingleton<WatchOnChainStrategyEngine>();
builder.Services.AddOptions<FlowOptions>().BindConfiguration("Flows").Validate(o=>o.IsValid(),"Invalid Flows settings").ValidateOnStart();
builder.Services.AddSingleton(s=>s.GetRequiredService<IOptions<FlowOptions>>().Value);
builder.Services.AddSingleton<IFlowRepository,FlowRepository>();
builder.Services.AddSingleton<IFlowPersistence,FlowPersistence>();
builder.Services.AddSingleton<FlowService>();
builder.Services.AddSingleton<IFlowFeatureSource>(s=>s.GetRequiredService<FlowService>());
builder.Services.AddSingleton<WatchFlowStrategyEngine>();
builder.Services.AddHttpClient<IFlowProvider,FlowProvider>(http=>
{
    var endpoint=Environment.GetEnvironmentVariable("PULLET_BSC_RPC_URL")??"https://bsc-rpc.publicnode.com";
    if(!Uri.TryCreate(endpoint,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.UserInfo.Length>0)throw new InvalidOperationException("PULLET_BSC_RPC_URL must be an HTTPS RPC endpoint without user info");
    http.BaseAddress=uri;http.Timeout=TimeSpan.FromSeconds(25);http.DefaultRequestHeaders.UserAgent.ParseAdd("PulletCryptoRadar/1.0");
}).RemoveAllLoggers();
builder.Services.AddHttpClient<IOnChainProvider,OnChainProvider>(http=>{http.Timeout=TimeSpan.FromSeconds(25);http.DefaultRequestHeaders.UserAgent.ParseAdd("PulletCryptoRadar/1.0");})
    .RemoveAllLoggers();
builder.Services.AddOptions<WatchOptions>().BindConfiguration("WatchTower").Validate(o=>o.IsValid(),"Invalid WatchTower settings").ValidateOnStart();
builder.Services.AddSingleton(s=>s.GetRequiredService<IOptions<WatchOptions>>().Value);
builder.Services.AddSingleton<WatchStrategyEngine>();
builder.Services.AddSingleton<WatchService>();
builder.Services.AddSingleton<IPaperRepository,PaperRepository>();
builder.Services.AddSingleton<IPaperPersistence,PaperPersistence>();
builder.Services.AddSingleton<PaperService>();
builder.Services.AddSingleton<PaperPerformanceService>();
builder.Services.AddSingleton<IQuantExecutionFeed,QuantExecutionFeed>();
builder.Services.AddSingleton<IQuantExecutionCoordinator,QuantExecutionCoordinator>();
builder.Services.AddSingleton<IExecutionEvidenceRepository,ExecutionEvidenceRepository>();
builder.Services.AddSingleton<ExecutionEvidenceService>();
builder.Services.AddSingleton<IQuantShadowRepository,QuantShadowRepository>();
builder.Services.AddSingleton<QuantShadowService>();
builder.Services.AddSingleton<IQuantDailyRepository,QuantDailyRepository>();
builder.Services.AddSingleton<QuantDailyService>();
builder.Services.AddSingleton<IQuantPortfolioValidationRepository,QuantPortfolioValidationRepository>();
builder.Services.AddSingleton<QuantPortfolioValidationService>();
builder.Services.AddSingleton<IHourlyWarmupRepository,HourlyWarmupRepository>();
builder.Services.AddSingleton<HourlyWarmupTracker>();
builder.Services.AddHostedService<QuantDailyWorker>();
builder.Services.AddSingleton<IQuantShadowTargets>(s=>s.GetRequiredService<QuantShadowService>());
builder.Services.AddHostedService<ExecutionEvidenceWorker>();
builder.Services.AddHostedService<QuantShadowWorker>();
builder.Services.AddSingleton<IQuantStudyRepository,QuantStudyRepository>();
builder.Services.AddSingleton<QuantStudyService>();
builder.Services.AddSingleton<QuantDiagnosticService>();
builder.Services.AddSingleton<IQuantRehearsalRepository,QuantRehearsalRepository>();
builder.Services.AddSingleton<QuantRehearsalService>();
builder.Services.AddHostedService<QuantStudyWorker>();
builder.Services.AddSingleton<IForwardRepository,ForwardRepository>();
builder.Services.AddSingleton<ForwardService>();
builder.Services.AddSingleton<IWatchRepository,WatchRepository>();
builder.Services.AddSingleton<IWatchPersistence,WatchPersistence>();
builder.Services.AddOptions<SpeculativeOptions>().BindConfiguration("Speculative").Validate(o=>o.IsValid(),"Invalid speculative settings").ValidateOnStart();
builder.Services.AddSingleton(s=>s.GetRequiredService<IOptions<SpeculativeOptions>>().Value);
builder.Services.AddSingleton<SpeculativeDetector>();
builder.Services.AddSingleton<SpeculativeService>();
builder.Services.AddSingleton<IAlternativeMarketStore,AlternativeMarketStore>();
builder.Services.AddSingleton<ISpeculativeRepository,SpeculativeRepository>();
builder.Services.AddSingleton<ISpeculativePersistence,SpeculativePersistence>();
builder.Services.AddOptions<AlternativeOptions>().BindConfiguration("Alternatives").Validate(o=>o.IsValid(),"Invalid alternative venue settings").ValidateOnStart();
builder.Services.AddHttpClient<IAlternativeMarketProvider,AlternativeProvider>(http=>
{
    http.Timeout=TimeSpan.FromSeconds(20);http.DefaultRequestHeaders.UserAgent.ParseAdd("PulletCryptoRadar/1.0");
});
builder.Services.AddOptions<CoinGeckoOptions>().BindConfiguration("CoinGecko").Validate(o => o.IsValid(), "Invalid CoinGecko settings").ValidateOnStart();
builder.Services.AddSingleton<CoinGeckoPacer>();
builder.Services.AddHttpClient<IMarketCapitalProvider, CoinGeckoProvider>((services, http) =>
{
    http.BaseAddress = new(services.GetRequiredService<IOptions<CoinGeckoOptions>>().Value.BaseUrl);
    http.Timeout = TimeSpan.FromSeconds(30); http.DefaultRequestHeaders.UserAgent.ParseAdd("PulletCryptoRadar/1.0");
});
builder.Services.AddOptions<DerivativeOptions>().BindConfiguration("Derivatives").Validate(o => o.IsValid(), "Invalid derivative settings").ValidateOnStart();
builder.Services.AddSingleton<IProviderHealthStore, ProviderHealthStore>();
builder.Services.AddSingleton<IRequestLimiter, RequestLimiter>();
builder.Services.AddSingleton<SqliteDatabase>();
builder.Services.AddSingleton<AssetRepository>();
builder.Services.AddSingleton<IAssetRepository>(s => s.GetRequiredService<AssetRepository>());
builder.Services.AddSingleton<PersistenceQueue>();
builder.Services.AddSingleton<FeatureEngine>();
builder.Services.AddSingleton<MarketService>();
builder.Services.AddSingleton<MarketHistoryLoader>();
builder.Services.AddOptions<VolatilityOptions>().BindConfiguration("Volatility")
    .Validate(o => o.IsValid(), "Invalid volatility radar settings").ValidateOnStart();
builder.Services.AddSingleton(s => s.GetRequiredService<IOptions<VolatilityOptions>>().Value);
builder.Services.AddSingleton<UpMoveDetector>();
builder.Services.AddSingleton<DownMoveDetector>();
builder.Services.AddSingleton<MarketRegimeDetector>();
builder.Services.AddSingleton<EpisodeEngine>();
builder.Services.AddSingleton<IRadarRepository, RadarRepository>();
builder.Services.AddSingleton<IRadarPersistence, RadarPersistence>();
builder.Services.AddSingleton<VolatilityRadarService>();
builder.Services.AddSingleton<IRadarSnapshotSource>(s => s.GetRequiredService<VolatilityRadarService>());
builder.Services.AddOptions<AlertOptions>().BindConfiguration("Alert").Validate(o => o.IsValid(), "Invalid alert settings").ValidateOnStart();
builder.Services.AddSingleton(s => s.GetRequiredService<IOptions<AlertOptions>>().Value);
builder.Services.AddSingleton<AlertPolicyEngine>();
builder.Services.AddSingleton<IAlertRepository, AlertRepository>();
builder.Services.AddSingleton<IAlertPersistence, AlertPersistence>();
builder.Services.AddSingleton<INotificationSender, WindowsToastSender>();
builder.Services.AddSingleton<AlertService>();
builder.Services.AddOptions<EvaluationOptions>().BindConfiguration("Evaluation").Validate(o => o.IsValid(), "Invalid evaluation settings").ValidateOnStart();
builder.Services.AddSingleton(s => s.GetRequiredService<IOptions<EvaluationOptions>>().Value);
builder.Services.AddSingleton<SignalEvaluationEngine>();
builder.Services.AddSingleton<ReplayEngine>();
builder.Services.AddSingleton<IEvaluationRepository, EvaluationRepository>();
builder.Services.AddSingleton<IEvaluationPersistence, EvaluationPersistence>();
builder.Services.AddSingleton<EvaluationService>();
builder.Services.AddHttpClient<IMarketRestProvider, BinanceRestClient>((services, http) =>
{
    http.BaseAddress = new Uri(services.GetRequiredService<IOptions<BinanceOptions>>().Value.RestBaseUrl);
    http.Timeout = TimeSpan.FromSeconds(20);
    http.DefaultRequestHeaders.UserAgent.ParseAdd("PulletCryptoRadar/1.0");
});
builder.Services.AddSingleton<IMarketStreamProvider, BinanceWebSocketClient>();
builder.Services.AddTransient<IDerivativeRestProvider>(s => (BinanceRestClient)s.GetRequiredService<IMarketRestProvider>());
builder.Services.AddTransient<IPriorityQuoteProvider>(s => (BinanceRestClient)s.GetRequiredService<IMarketRestProvider>());
builder.Services.AddTransient<IExecutionEvidenceProvider>(s => (BinanceRestClient)s.GetRequiredService<IMarketRestProvider>());
builder.Services.AddHostedService<PersistenceWorker>();
builder.Services.AddHostedService<LocalServerControl>();
builder.Services.AddHostedService<MarketEventWorker>();
builder.Services.AddHostedService<FeatureWorker>();
builder.Services.AddHostedService<SnapshotWorker>();
builder.Services.AddHostedService<BinanceWorker>();
builder.Services.AddHostedService<ForwardWorker>();
builder.Services.AddHostedService<DerivativeWorker>();
builder.Services.AddHostedService<DerivativePersistenceWorker>();
builder.Services.AddHostedService<VolatilityRadarWorker>();
builder.Services.AddHostedService<AlertWorker>();
builder.Services.AddHostedService<EvaluationWorker>();
builder.Services.AddHostedService<MarketCapitalWorker>();
builder.Services.AddHostedService<AlternativeWorker>();
builder.Services.AddHostedService<SpeculativeWorker>();
builder.Services.AddHostedService<WatchWorker>();
builder.Services.AddHostedService<OnChainWorker>();
builder.Services.AddHostedService<FlowWorker>();
builder.Services.AddHostedService<PaperWorker>();
builder.Services.AddHostedService<PaperWarmupWorker>();
builder.Services.AddHostedService<OpportunityWorker>();
builder.Services.AddHostedService<TokenFollowupWorker>();
builder.Services.AddHostedService<TokenMonitorWorker>();
builder.Services.AddHostedService<DailyResearchWorker>();
builder.Services.AddResponseCompression();
var app = builder.Build();
app.UseResponseCompression();
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = context =>
{
    if (context.File.Name == "index.html") context.Context.Response.Headers.CacheControl = "no-cache";
} });
app.MapGet("/api/assets", (MarketService service) => service.GetAssets());
app.MapGet("/api/assets/{id:int}", (int id, MarketService service) => service.GetAsset(id) is { } m ? Results.Ok(m) : Results.NotFound());
app.MapGet("/api/assets/{id:int}/history", (int id, int? interval, MarketService service) =>
    interval is not (null or 1 or 5 or 15) ? Results.BadRequest("interval must be 1, 5 or 15") : service.GetAsset(id) is null ? Results.NotFound()
    : Results.Ok(service.GetHistory(id, interval ?? 5)));
app.MapGet("/api/providers/health", (MarketService service, TimeProvider time) => service.GetHealth().Select(x => new { Health = x, Status = x.Status(time.GetUtcNow()) }));
app.MapGet("/api/assets/{id:int}/derivatives", (int id, int? limit, DerivativeService service) => service.GetRaw(id, limit ?? 100) is { } raw ? Results.Ok(raw) : Results.NotFound());
app.MapGet("/api/volatility", (VolatilityRadarService radar) => radar.GetSnapshot() is { } snapshot ? Results.Ok(snapshot) : Results.StatusCode(503));
app.MapGet("/api/volatility/events", async (int? assetId, int? limit, VolatilityRadarService radar, CancellationToken ct) => await radar.GetEventsAsync(assetId, limit ?? 50, ct));
app.MapGet("/api/episodes", async (int? assetId, int? limit, VolatilityRadarService radar, CancellationToken ct) => await radar.GetEpisodesAsync(assetId, limit ?? 50, ct));
app.MapGet("/api/alerts", async (int? limit, AlertService alerts, CancellationToken ct) => await alerts.GetNotificationsAsync(limit ?? 50, ct));
app.MapGet("/api/alerts/policy", (AlertService alerts) => alerts.GetSnapshot() is { } snapshot ? Results.Ok(snapshot) : Results.StatusCode(503));
app.MapGet("/api/evaluation", (EvaluationService service) => service.GetLatest() is { } report ? Results.Ok(report) : Results.StatusCode(503));
app.MapPost("/api/evaluation/refresh", async (EvaluationService service, CancellationToken ct) => Results.Ok(await service.RefreshAsync(ct)));
app.MapGet("/api/replay/availability", (EvaluationService service) => service.GetAvailability());
app.MapGet("/api/replay/audit", (int? limit, EvaluationService service) => service.Audit(limit ?? 100));
app.MapGet("/api/mappings", (MappingService service) => service.GetOverview());
app.MapGet("/api/onchain",(OnChainService service)=>service.Overview());
app.MapGet("/api/token-check/search",(string? q,TokenCheckService service)=>service.Search(q??""));
app.MapGet("/api/token-monitoring",(TokenMonitorService service)=>new{Targets=service.Overview(),Budget=service.Budget(),PollMinutes=30,Scope="固定合约研究；不改自选/绑定/模拟仓"});
app.MapGet("/api/runtime/reliability",(RuntimeReliabilityService service)=>service.Status());
app.MapGet("/api/research/daily",(string? date,DailyResearchService service,TimeProvider time)=>
{
    var selected=DateOnly.FromDateTime(time.GetUtcNow().ToOffset(TimeSpan.FromHours(8)).DateTime);
    if(date is not null&&!DateOnly.TryParseExact(date,"yyyy-MM-dd",out selected))return Results.BadRequest(new{Error="日期格式应为yyyy-MM-dd"});
    try{return Results.Ok(service.Build(selected));}catch(ArgumentException ex){return Results.BadRequest(new{Error=ex.Message});}
});
app.MapGet("/api/research/daily/archive",(string date,DailyResearchService service)=>DateOnly.TryParseExact(date,"yyyy-MM-dd",out var selected)?
    service.Saved(selected) is {} saved?Results.Ok(saved):Results.NotFound():Results.BadRequest(new{Error="日期格式应为yyyy-MM-dd"}));
app.MapGet("/api/token-check/recent",(TokenCheckService service)=>service.Recent().Select(r=>new{r.Id,r.Version,r.AsOf,r.ExpiresAt,r.Coin,r.Verdict,r.EvidenceCoverage,r.Platform,r.ContractAddress}));
app.MapGet("/api/token-check/{id:guid}",(Guid id,TokenCheckService service)=>service.Get(id) is {} r?Results.Ok(r):Results.NotFound());
app.MapGet("/api/token-check/{id:guid}/followup",async(Guid id,TokenCheckService service,CancellationToken ct)=>await service.FollowupAsync(id,ct) is {} r?Results.Ok(r):Results.NotFound());
app.MapPost("/api/token-check",async(TokenCheckRequest request,TokenCheckService service,CancellationToken ct)=>
{
    try{return Results.Ok(await service.CheckAsync(request,ct));}
    catch(ArgumentException ex){return Results.BadRequest(new{Error=ex.Message});}
    catch(InvalidOperationException ex){return Results.Conflict(new{Error=ex.Message});}
});
app.MapGet("/api/flows",(FlowService service)=>service.Overview());
app.MapGet("/api/paper/holding-history",async(Guid? positionId,int? limit,long? before,PaperService service,CancellationToken ct)=>
{try{return Results.Ok(await service.HoldingHistoryAsync(positionId,Math.Clamp(limit??200,1,1000),before,ct));}catch(ArgumentException ex){return Results.BadRequest(new{Error=ex.Message});}});
app.MapGet("/api/paper",(PaperService service,CancellationToken ct)=>service.OverviewAsync(ct));
app.MapGet("/api/paper/performance",async(string? date,PaperPerformanceService service,CancellationToken ct)=>
{try{DateOnly? selected=date is null?null:DateOnly.ParseExact(date,"yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture);return Results.Ok(await service.GetAsync(selected,ct));}
 catch(FormatException){return Results.BadRequest(new{error="日期格式应为yyyy-MM-dd"});}catch(ArgumentException ex){return Results.BadRequest(new{error=ex.Message});}});
app.MapGet("/api/paper/activity",(PaperService service,CancellationToken ct)=>service.ActivityAsync(ct));
app.MapGet("/api/execution/status",async(ExecutionEvidenceService evidence,PaperService paper,IMarketRuntimeStore market,CancellationToken ct)=>
    evidence.Status((await paper.ExecutionContextAsync(ct)).State,market.GetAll()));
app.MapPost("/api/execution/preview",(ExecutionQuoteRequest request,ExecutionEvidenceService evidence)=>Results.Ok(evidence.Preview(request)));
app.MapGet("/api/quant/status",(QuantShadowService service)=>Results.Ok(service.Status()));
app.MapGet("/api/quant/warmup",(HourlyWarmupTracker tracker)=>Results.Ok(tracker.Status()));
app.MapGet("/api/quant/execution",async(PaperService paper,CancellationToken ct)=>(await paper.QuantStatusAsync(ct)) with{EntryImplementationVersion=QuantEntryEngine.Version});
app.MapGet("/api/quant/entry-review/{id}",(string id,IPaperRepository repository)=>
{
    if(id.Length!=64||!id.All(Uri.IsHexDigit))return Results.BadRequest(new{error="入场复核ID须为64位摘要"});
    return repository.EntryReview(id) is {} review?Results.Ok(review):Results.NotFound(new{error="没有已提交的入场复核记录"});
});
app.MapPost("/api/quant/execution/activate",async(PaperService paper,QuantShadowService shadow,ExecutionEvidenceService execution,CancellationToken ct)=>
{try{var cohort=shadow.Status().Cohort;if(cohort is null)return Results.Conflict(new{error="前瞻核心尚未就绪"});await paper.ActivateQuantAsync(cohort,execution.Model,ct);return Results.Ok(await paper.QuantStatusAsync(ct));}catch(InvalidOperationException ex){return Results.Conflict(new{error=ex.Message});}});
app.MapGet("/api/quant/history",(QuantShadowService service)=>Results.Ok(service.Status().ForwardHours));
app.MapGet("/api/quant/daily",async(string? date,QuantDailyService service,CancellationToken ct)=>
{
    if(date is not null&&!DateOnly.TryParseExact(date,"yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.None,out _))return Results.BadRequest(new{error="日期格式须为yyyy-MM-dd"});
    try{return Results.Ok(await service.GetAsync(date is null?null:DateOnly.ParseExact(date,"yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture),ct));}
    catch(ArgumentException ex){return Results.BadRequest(new{error=ex.Message});}
});
app.MapGet("/api/quant/validation",(QuantStudyService service)=>Results.Ok(service.Status()));
app.MapGet("/api/quant/portfolio-validation",(QuantPortfolioValidationService service)=>
{try{var report=service.Status();return report is null?Results.Content("null","application/json"):Results.Ok(report);}catch(Exception ex)when(ex is InvalidDataException or IOException){return Results.Conflict(new{error="组合核验档案缺失或未通过检查"});}});
app.MapGet("/api/quant/portfolio-validation/runs/{id:guid}",(Guid id,QuantPortfolioValidationService service)=>
{try{return service.Read(id) is {} run?Results.Ok(run):Results.NotFound();}catch(Exception ex)when(ex is InvalidDataException or IOException){return Results.Conflict(new{error="冻结组合资料缺失、冲突或无法重放"});}});
app.MapPost("/api/quant/portfolio-validation/run",async(string? date,QuantPortfolioValidationService service,CancellationToken ct)=>
{
    if(date is not null&&!DateOnly.TryParseExact(date,"yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.None,out _))return Results.BadRequest(new{error="日期格式须为yyyy-MM-dd"});
    try{return Results.Ok(await service.RunAsync(date is null?null:DateOnly.ParseExact(date,"yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture),ct));}
    catch(ArgumentException ex){return Results.BadRequest(new{error=ex.Message});}
    catch(InvalidOperationException ex){return Results.Conflict(new{error=ex.Message});}
    catch(Exception ex)when(ex is InvalidDataException or IOException){return Results.Conflict(new{error="冻结组合资料核验失败；未修改原模拟账户"});}
});
app.MapGet("/api/quant/rehearsal",async(QuantRehearsalService service,PaperService paper,CancellationToken ct)=>{var active=(await paper.ExecutionContextAsync(ct)).State.QuantControl is not null;return Results.Ok(new{report=service.Status(),mode=active?"ArchivedLegacyCopy":"FrozenCopyOnly",canGenerate=!active});});
app.MapPost("/api/quant/rehearsal/run",async(QuantRehearsalService service,CancellationToken ct)=>
{
    try{return Results.Ok(await service.RunAsync(ct));}
    catch(InvalidOperationException ex){return Results.Conflict(new{error=ex.Message});}
    catch(InvalidDataException){return Results.Conflict(new{error="副本数据未通过一致性检查，未修改原账户"});}
});
app.MapGet("/api/quant/validation/diagnostics",(Guid id,string? window,string? strategy,string? policy,int? cost,QuantDiagnosticService service)=>
{
    try{var result=service.Read(id,window??"Holdout",strategy??"TrendPullback",policy??"ScaleTail",cost??1);return result is null?Results.NotFound(new{error="研究报告不存在"}):Results.Ok(result);}
    catch(ArgumentException ex){return Results.BadRequest(new{error=ex.Message});}
    catch(InvalidDataException){return Results.Conflict(new{error="研究存档校对未通过，诊断结果未生成"});}
});
app.MapPost("/api/quant/validation/run",(QuantStudyService service)=>
{try{return Results.Accepted("/api/quant/validation",service.Start());}catch(InvalidOperationException ex){return Results.Conflict(new{error=ex.Message});}});
app.MapGet("/api/quant/validation/trades",(Guid id,string? window,string? strategy,string? policy,int? cost,int? limit,IQuantStudyRepository repository)=>
    Results.Ok(repository.Trades(id,window,strategy,policy,Math.Clamp(cost??1,1,3),Math.Clamp(limit??25,1,100))));
app.MapGet("/api/paper/strategies",(PaperService paper,OpportunityService opportunities,CancellationToken ct)=>paper.StrategyReviewAsync(opportunities.Latest(),ct));
app.MapGet("/api/opportunities",(OpportunityService service)=>service.Latest() is {} snapshot?Results.Ok(snapshot):Results.StatusCode(503));
app.MapGet("/api/opportunities/history",(int? limit,OpportunityService service)=>service.History(limit??6));
app.MapGet("/api/research/tenfold",()=>Results.File(Path.Combine(AppContext.BaseDirectory,"Research","Tenfold","Summary.json"),"application/json"));
app.MapGet("/api/forward",(ForwardService service,CancellationToken ct)=>service.OverviewAsync(ct));
app.MapPost("/api/data-foundation/market", (string coinId, string chain, bool? includeOwnership, bool? compareMarketCapital, PulletCryptoRadar.Host.MarketFoundation foundation, CancellationToken ct) => foundation.Collect(coinId, chain, includeOwnership == true, ct, compareMarketCapital: compareMarketCapital == true));
app.MapGet("/api/network",(NetworkUsage usage)=>usage.Snapshot());
app.MapGet("/api/research/backtest",()=>Results.File(Path.Combine(AppContext.BaseDirectory,"Research","Backtest","Summary.json"),"application/json"));
app.MapGet("/api/research/backtest/{key}",(string key)=>
{
    if(!System.Text.RegularExpressions.Regex.IsMatch(key,@"\A\d{4}-\d{2}-\d{2}-[ab]-(?:oi|price)-cost[12]\z"))return Results.NotFound();
    var path=Path.Combine(AppContext.BaseDirectory,"Research","Backtest",key+".json");
    return File.Exists(path)?Results.File(path,"application/json"):Results.NotFound();
});
app.MapGet("/api/research/cases",()=>Results.File(Path.Combine(AppContext.BaseDirectory,"Research","CaseInventory.json"),"application/json"));
app.MapGet("/api/research/templates",()=>Results.File(Path.Combine(AppContext.BaseDirectory,"Research","Templates","Summary.json"),"application/json"));
app.MapGet("/api/research/templates/{key}",(string key)=>
{
    if(!System.Text.RegularExpressions.Regex.IsMatch(key,@"\A[A-Z0-9]{2,30}USDT-\d{4}-\d{2}\z"))return Results.NotFound();
    var path=Path.Combine(AppContext.BaseDirectory,"Research","Templates","Cases",key+".json");
    return File.Exists(path)?Results.File(path,"application/json"):Results.NotFound();
});
app.MapPost("/api/paper/pause",async(PaperService service,CancellationToken ct)=>{await service.SetEnabledAsync(false,ct);return Results.Ok(new{Status="Paused"});});
app.MapPost("/api/paper/resume",async(PaperService service,CancellationToken ct)=>{try{await service.SetEnabledAsync(true,ct);return Results.Ok(new{Status="Running"});}catch(ArgumentException ex){return Results.Conflict(new{Error=ex.Message});}});
app.MapPost("/api/flows/{id:int}/retry",async(int id,OnChainService chain,IFlowPersistence persistence,FlowService service,TimeProvider time,CancellationToken ct)=>
{
    var binding=chain.Bindings().FirstOrDefault(b=>b.AssetId==id&&b.Chain=="bsc");if(binding is null)return Results.NotFound();
    if(!await persistence.RetryTransportAsync(binding.Chain,binding.ContractAddress,time.GetUtcNow(),ct))return Results.Conflict(new{Error="仅网络失败满两分钟可申请重试；接口冷却、无效数据和分叉暂停不能绕过"});
    service.Refresh();return Results.Ok(new{Status="Queued",Message="等待下一轮调度，预算不重置"});
});
app.MapGet("/api/flows/status",(FlowOptions options)=>new{Settings=options,RpcConfigured=!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PULLET_BSC_RPC_URL")),Chain="bsc",ChainId=56});
app.MapGet("/api/onchain/{id:int}/history",(int id,int? limit,OnChainService service)=>service.History(id,limit??40));
app.MapGet("/api/onchain/status",(OnChainOptions options)=>new{ApiKeyConfigured=!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PULLET_EXPLORER_API_KEY")),Settings=options});
app.MapPut("/api/onchain/{id:int}",async(int id,ChainBindingRequest request,OnChainService service,CancellationToken ct)=>
{
    try{return Results.Ok(await service.SaveBindingAsync(id,request,ct));}
    catch(MappingConflictException ex){return Results.Conflict(new{Error=ex.Message});}
    catch(KeyNotFoundException ex){return Results.NotFound(new{Error=ex.Message});}
    catch(ArgumentException ex){return Results.BadRequest(new{Error=ex.Message});}
});
app.MapPut("/api/onchain/labels",async(AddressLabelRequest request,OnChainService service,CancellationToken ct)=>
{
    try{return Results.Ok(await service.SaveLabelAsync(request,ct));}
    catch(MappingConflictException ex){return Results.Conflict(new{Error=ex.Message});}
    catch(ArgumentException ex){return Results.BadRequest(new{Error=ex.Message});}
});
app.MapGet("/api/watchlist",(WatchService service)=>service.Overview());
app.MapGet("/api/watchlist/{id:int}/history",(int id,int? limit,WatchService service)=>service.History(id,limit??30));
app.MapGet("/api/watchlist/{id:int}/audit",(int id,int? limit,WatchService service)=>service.Audit(id,limit??30));
app.MapPost("/api/watchlist/evaluate",async(WatchService service,CancellationToken ct)=>{await service.TickAsync(ct);return Results.Ok(service.Overview());});
app.MapPut("/api/watchlist/{id:int}",async(int id,WatchRequest request,WatchService service,CancellationToken ct)=>
{
    try{return Results.Ok(await service.SaveAsync(id,request,ct));}
    catch(MappingConflictException ex){return Results.Conflict(new{Error=ex.Message});}
    catch(KeyNotFoundException ex){return Results.NotFound(new{Error=ex.Message});}
    catch(ArgumentException ex){return Results.BadRequest(new{Error=ex.Message});}
});
app.MapGet("/api/speculative",(SpeculativeService service)=>service.Latest() is {} s?Results.Ok(s):Results.StatusCode(503));
app.MapPost("/api/speculative/refresh",async(SpeculativeService service,CancellationToken ct)=>Results.Ok(await service.ScanAsync(ct)));
app.MapGet("/api/speculative/audit",(int? limit,SpeculativeService service)=>service.Audit(limit??3));
app.MapGet("/api/venues",(IAlternativeMarketStore store)=>store.GetAll());
app.MapGet("/api/venues/bindings",(SpeculativeService service)=>service.Bindings());
app.MapPut("/api/venues/bindings/{id:int}",async(int id,VenueBindingRequest request,SpeculativeService service,CancellationToken ct)=>
{
    try{await service.SetVenueBindingAsync(id,request,ct);return Results.Ok();}
    catch(MappingConflictException ex){return Results.Conflict(new{Error=ex.Message});}
    catch(KeyNotFoundException ex){return Results.NotFound(new{Error=ex.Message});}
    catch(ArgumentException ex){return Results.BadRequest(new{Error=ex.Message});}
});
app.MapGet("/api/mappings/status", (MappingService service) => service.Status());
app.MapGet("/api/coingecko/search", (string? q, MappingService service) => service.Search(q ?? ""));
app.MapGet("/api/mappings/{id:int}/candidates", (int id, MappingService service) => service.Candidates(id));
app.MapGet("/api/mappings/{id:int}/history", (int id, MappingService service) => service.History(id));
app.MapPut("/api/mappings/{id:int}", async (int id, ManualMappingRequest request, MappingService service, CancellationToken ct) =>
{
    try { return Results.Ok(await service.SetManualAsync(id, request, ct)); }
    catch (MappingConflictException ex) { return Results.Conflict(new { Error = ex.Message }); }
    catch (KeyNotFoundException ex) { return Results.NotFound(new { Error = ex.Message }); }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return Results.BadRequest(new { Error = ex.Message }); }
});
app.MapPost("/api/replay", async (ReplayRequest request, EvaluationService service, CancellationToken ct) =>
{
    try { return Results.Ok(await service.ReplayAsync(request, ct)); }
    catch (Exception ex) when (ex is ArgumentException or InvalidDataException) { return Results.BadRequest(new { Error = ex.Message }); }
});
app.MapGet("/api/settings", (IOptions<BinanceOptions> binance, IOptions<StorageOptions> storage, VolatilityOptions volatility, AlertOptions alert, IOptions<DerivativeOptions> derivatives, EvaluationOptions evaluation, IOptions<CoinGeckoOptions> coingecko) => new { Binance = binance.Value, Storage = storage.Value, Volatility = volatility, Alert = alert, Derivatives = derivatives.Value, Evaluation = evaluation, CoinGecko = coingecko.Value });
app.MapGet("/health", (PersistenceQueue queue) => queue.Ready.Task.IsCompletedSuccessfully ? Results.Ok(new { Application = "PulletCryptoRadar", Status = "Ready", Version = "1.0", Phase = "0-16", Frontend = "Vue 3",
    InstanceId=LocalServerControl.PipeName(AppContext.BaseDirectory),ProcessId=Environment.ProcessId }) : Results.StatusCode(503));
app.MapMethods("/api/{**path}", ["GET", "POST", "PUT", "PATCH", "DELETE"], () => Results.NotFound(new { Error = "API endpoint not found" }));
app.MapFallbackToFile("index.html");
using var sessionJournal=new RuntimeSessionJournal(Path.Combine(AppContext.BaseDirectory,"Logs"),app.Lifetime);
try{app.Run();}
finally{Log.CloseAndFlush();}
