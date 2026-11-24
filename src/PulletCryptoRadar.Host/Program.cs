using System.Text.Json.Serialization;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;
using PulletCryptoRadar.DataFoundation;
using PulletCryptoRadar.Host;

ConsoleInputPolicy.Initialize();
if(args.Length==2&&args[0]=="--research-history")
{
    Environment.ExitCode=await HistoricalResearchCommand.RunAsync(Path.GetFullPath(args[1]));return;
}
if(args.Length is 2 or 3 && args[0]=="--verify-research-samples" && (args.Length==2 || args[2].StartsWith("--refresh=",StringComparison.Ordinal)))
{
    Environment.ExitCode=await ResearchSampleCommand.RunAsync(Path.GetFullPath(args[1]),args.Length==3?args[2][10..]:null);
    return;
}
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.WebHost.UseUrls("http://localhost:5218");
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
var dataDirectory = Path.Combine(AppContext.BaseDirectory, "Data");
var store = new FoundationStore(dataDirectory);
store.Initialize(DateTimeOffset.UtcNow);
store.ImportReviewedAddressEvidence(ReviewedAddressCatalog.Items);
builder.Services.AddSingleton(store);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<SharedFundingInfoCache>();
builder.Services.AddSingleton<ClosedHourCandleCache>();
builder.Services.AddSingleton<IRequestLedger>(store);
builder.Services.AddSingleton<IFoundationEvidenceStore>(store);
builder.Services.AddSingleton<IQuickCheckStore>(store);
builder.Services.AddSingleton<CollectionPipeline>();
builder.Services.AddSingleton<TokenSearchService>();
builder.Services.AddSingleton<QuickCheckService>();
builder.Services.AddTransient<DirectionAnalysisService>();
builder.Services.AddHttpClient<IMarketDataProvider, BinanceMarketProvider>(http =>
{
    http.Timeout = TimeSpan.FromSeconds(12);
    http.DefaultRequestHeaders.UserAgent.ParseAdd("PulletCryptoRadar/2.0");
}).ConfigurePrimaryHttpMessageHandler(() => HttpTransport.Create(false))
    .RemoveAllLoggers();
builder.Services.AddHttpClient<ISupplementaryProvider, SupplementaryProvider>(http =>
{
    http.Timeout = TimeSpan.FromSeconds(12);
    http.DefaultRequestHeaders.UserAgent.ParseAdd("PulletCryptoRadar/2.0");
}).ConfigurePrimaryHttpMessageHandler(() => HttpTransport.Create(false))
    .RemoveAllLoggers();
builder.Services.AddHostedService<BinanceCollectionWorker>();
// GoPlus direct TLS is unavailable on this host; use the configured system proxy for this source only.
builder.Services.AddHttpClient<GoPlusHolderProvider>(http => { http.Timeout=TimeSpan.FromSeconds(15); })
    .ConfigurePrimaryHttpMessageHandler(()=>HttpTransport.Create(true)).RemoveAllLoggers();
builder.Services.AddHostedService<HolderCollectionWorker>();
builder.Services.AddHostedService<LocalControlWorker>();
builder.Services.AddHostedService<ObservationWorker>();
var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/health", () => new
{
    application = "PulletCryptoRadar", version = "1.0.0-preview", processId = Environment.ProcessId,
    instanceId = LocalControlWorker.InstanceId,
    status = "Ready", automaticTrading = false, mode = "ResearchOnly",
    consoleInput = ConsoleInputPolicy.Status
});
app.MapGet("/api/collection/budget", (FoundationStore database,TimeProvider clock) => database.BudgetUsage(clock.GetUtcNow()));
app.MapGet("/api/collection/diagnostics",(FoundationStore database,TimeProvider clock)=>database.CollectionDiagnostics(clock.GetUtcNow()));
app.MapGet("/api/observation", (FoundationStore database,TimeProvider clock) => database.Observation(clock.GetUtcNow()));
app.MapGet("/api/research/history",()=> new {Report=ResearchReports.History(dataDirectory)});
app.MapGet("/api/assets", (FoundationStore database) => database.Assets());
app.MapGet("/api/research/identities",(FoundationStore database)=>database.ResearchProofs().Select(item=>new
{
    item.AssetId,item.VerifiedAt,item.EvidenceId,item.ReferenceUrl,identity=item.After,
    limitations="Reviewed samples only; identity verification is not a trading recommendation."
}));
app.MapGet("/api/quick-check/search", (string query, ChainId? chain, TokenSearchService search) =>
{
    try { return Results.Ok(search.Search(query,chain)); }
    catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
});
app.MapGet("/api/direction/search", (string query, TokenSearchService search) =>
{
    try { return Results.Ok(search.Search(query, includeSavedCatalog: true)); }
    catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
});
app.MapPost("/api/direction/{symbol}", async (string symbol, ChaseDirection direction,
    DirectionAnalysisService analysis, CancellationToken token) =>
{
    symbol = symbol.Trim().ToUpperInvariant();
    if (!System.Text.RegularExpressions.Regex.IsMatch(symbol, @"\A[\p{L}\p{N}]{1,40}USDT\z") ||
        direction is not (ChaseDirection.Long or ChaseDirection.Short))
        return Results.BadRequest(new { error = "请选择 Binance USDT 永续交易对及方向。" });
    return Results.Ok(await analysis.AnalyzeAsync(symbol, direction, token));
});
app.MapGet("/api/quick-check/recent", (FoundationStore database) =>
{
    var assets = database.Assets();
    return database.QuickCheckHistory().Select(item => new
    {
        item.AssetId, item.LastViewedAt, asset = assets.Single(asset => asset.AssetId == item.AssetId)
    });
});
app.MapDelete("/api/quick-check/recent", (FoundationStore database) =>
{
    database.DeleteQuickCheckHistory(null);
    return Results.NoContent();
});
app.MapDelete("/api/quick-check/recent/{assetId:guid}", (Guid assetId, FoundationStore database) =>
{
    database.DeleteQuickCheckHistory(assetId);
    return Results.NoContent();
});
app.MapGet("/api/quick-check/{assetId:guid}", (Guid assetId, QuickCheckService checks) =>
    checks.Cached(assetId) is { } report ? Results.Ok(report) : Results.NotFound());
app.MapPost("/api/quick-check/{assetId:guid}", async (Guid assetId, QuickCheckService checks, CancellationToken token) =>
    await checks.AnalyzeAsync(assetId,token) is { } report ? Results.Ok(report) : Results.NotFound());
app.MapGet("/api/features/{assetId:guid}", (Guid assetId, FoundationStore database) =>
    database.Latest(assetId) is { } snapshot ? Results.Ok(snapshot) : Results.NotFound());
app.MapGet("/api/collection", (FoundationStore database) => database.CollectionStatus());
app.MapGet("/api/collection/depth", (FoundationStore database, TimeProvider clock) => database.DepthStatus(clock.GetUtcNow()));
app.MapGet("/api/collection/priority", (FoundationStore database, TimeProvider clock) => new
{
    asOf = clock.GetUtcNow(), candidates = database.Priority(clock.GetUtcNow()),
    purpose = "ResearchRefreshNotTradeAdmission", maximumCandidates = 4
});
app.MapGet("/api/radar", (FoundationStore database, TimeProvider clock) =>
{
    var engine = new PulletCryptoRadar.Features.RadarEngine();
    var assets = database.Assets();
    return database.Radar().Where(episode => episode.Assessment.Stage != RadarStage.Excluded).Select(episode => new
    {
        episode.EpisodeId, episode.StartedAt, lastCheckedAt = episode.UpdatedAt,
        symbol = assets.Single(asset => asset.AssetId == episode.AssetId).BinanceSymbol,
        assessment = engine.Calculate(assets.Single(asset => asset.AssetId == episode.AssetId),
            database.Latest(episode.AssetId),clock.GetUtcNow())
    });
});
app.MapGet("/api/radar/{assetId:guid}/events", (Guid assetId, FoundationStore database) => database.RadarEvents(assetId));
app.MapGet("/api/radar/board", (FoundationStore database,TimeProvider clock) =>
    new PulletCryptoRadar.Features.RadarBoardBuilder().Build(database.Assets(),database.LatestSnapshots(),
        database.Universe()?.Universe ?? [],clock.GetUtcNow(),database.LatestHolderSnapshots()));
app.MapGet("/api/radar/holders",(FoundationStore database,TimeProvider clock)=>database.HolderResearchBoard(clock.GetUtcNow()));
app.MapGet("/api/data-sources", (FoundationStore database, TimeProvider clock) => database.SourceHealth(clock.GetUtcNow()));
app.MapGet("/api/market-context", (FoundationStore database, TimeProvider clock) =>
{
    if (database.MarketContext() is not { } context) return Results.NotFound();
    var fresh = context.AsOf <= clock.GetUtcNow() && clock.GetUtcNow()-context.AsOf <= TimeSpan.FromMinutes(3);
    return Results.Ok(fresh ? context : context with
    {
        State = MarketState.Unknown, Reasons = context.Reasons.Concat(["ContextStale"]).ToArray()
    });
});
app.MapFallback("/api/{**path}", () => Results.NotFound());
app.MapFallbackToFile("index.html");
app.Run();
