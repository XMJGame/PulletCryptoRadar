using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;
using PulletCryptoRadar.Trading;

namespace PulletCryptoRadar.Host;

public sealed class PaperRuntimeCoordinator(FoundationStore store,PaperRuntimePolicy policy,TradingLimits limits,TimeProvider clock)
{
    private readonly ConcurrentDictionary<Guid,ProviderRead> books = new();
    private readonly ConcurrentDictionary<Guid,ProviderRead> marks = new();
    private readonly ConcurrentDictionary<Guid,DateTimeOffset> verifiedFunding = new();
    private readonly ConcurrentDictionary<Guid,DateTimeOffset> savedMarks = new();
    private readonly ConcurrentDictionary<Guid,DateTimeOffset> savedBooks = new();
    private readonly ConcurrentDictionary<Guid,DateTimeOffset> observedFundingTimes = new();
    private readonly PaperPortfolioEngine engine = new();
    private Guid[] watching = [];
    private string state = "Idle";
    private string? detail;
    public static Guid CommandId(string text) => new(SHA256.HashData(Encoding.UTF8.GetBytes(text)).AsSpan(0,16));
    public void Session(IReadOnlyList<AssetIdentity> assets)
    {
        watching = assets.Select(item => item.AssetId).ToArray(); books.Clear(); marks.Clear(); state = assets.Count == 0 ? "Idle" : "Connecting"; detail = null;
    }
    public void Disconnected(string reason)
    {
        books.Clear(); marks.Clear(); state = "Disconnected"; detail = reason;
    }
    public void FundingVerified(Guid assetId,DateTimeOffset at) => verifiedFunding[assetId] = at;
    private bool FreshBook(Guid assetId,DateTimeOffset now) => books.TryGetValue(assetId,out var read) &&
        read.OrderBook is { } book && book.SourceTime <= now && now-book.SourceTime <= TimeSpan.FromSeconds(2);
    private bool FreshMark(Guid assetId,DateTimeOffset now) => marks.TryGetValue(assetId,out var read) &&
        read.MarketMark is { } mark && mark.SourceTime <= now && now-mark.SourceTime <= TimeSpan.FromSeconds(3);
    public PaperRuntimeState Snapshot()
    {
        var now = clock.GetUtcNow(); var watched = watching;
        var freshBooks = watched.Count(id => FreshBook(id,now)); var freshMarks = watched.Count(id => FreshMark(id,now));
        var active = store.Portfolio().Positions.Where(item => item.RemainingQuantity > 0).ToArray();
        var fundingReady = active.All(item => verifiedFunding.TryGetValue(item.AssetId,out var at) && now-at <= TimeSpan.FromMinutes(10));
        var ready = watched.Length > 0 && freshBooks == watched.Length && freshMarks == watched.Length;
        return new(now,ready ? "Streaming" : state == "Connecting" && books.Count+marks.Count > 0 ? "WaitingOrStaleQuotes" : state,
            watched.Length,freshBooks,freshMarks,
            policy.Enabled && ready && fundingReady && store.PaperIssues().Count == 0,
            !fundingReady ? "FundingReconciliationPending" : detail);
    }
    public void Ingest(ProviderRead read)
    {
        if (read.Evidence.Provider != "Binance" || read.Evidence.Failure != CollectionFailure.None) return;
        var now = clock.GetUtcNow();
        var assetId = read.OrderBook?.AssetId ?? read.MarketMark?.AssetId;
        if (assetId is not { } id || !watching.Contains(id)) return;
        if (read.OrderBook is { } book)
        {
            books.AddOrUpdate(id,read,(_,old) => old.OrderBook!.SourceTime <= book.SourceTime && old.OrderBook.UpdateId < book.UpdateId ? read : old);
            if (book.SourceTime <= now && (!savedBooks.TryGetValue(id,out var saved) || now-saved >= TimeSpan.FromSeconds(10)))
            {
                store.SavePaperMarket(read); savedBooks[id] = now;
            }
        }
        if (read.MarketMark is { } mark)
        {
            if (marks.TryGetValue(id,out var previousMark) && previousMark.MarketMark!.SourceTime >= mark.SourceTime) return;
            if (observedFundingTimes.TryGetValue(id,out var priorFunding) && priorFunding <= mark.SourceTime && priorFunding != mark.NextFundingAt)
                verifiedFunding.TryRemove(id,out _);
            observedFundingTimes[id] = mark.NextFundingAt;
            marks.AddOrUpdate(id,read,(_,old) => old.MarketMark!.SourceTime < mark.SourceTime ? read : old);
            if (mark.SourceTime <= now && (!savedMarks.TryGetValue(id,out var saved) || now-saved >= TimeSpan.FromSeconds(10)))
            {
                store.SavePaperMarket(read); savedMarks[id] = now;
            }
        }
        Manage(id,now);
        if (Snapshot().AdmissionEnabled) Admit(id,now);
    }
    private void Manage(Guid assetId,DateTimeOffset now)
    {
        if (!FreshBook(assetId,now)) return;
        var read = books[assetId]; var book = read.OrderBook!;
        foreach (var position in store.Portfolio().Positions.Where(item => item.AssetId == assetId && item.RemainingQuantity > 0))
        {
            PaperTransition Evaluate(PaperPortfolio portfolio)
            {
                var at = clock.GetUtcNow();
                var current = portfolio.Positions.Single(item => item.PositionId == position.PositionId);
                var safety = FreshMark(assetId,at) && PaperMarginModel.NeedsSafetyExit(current,marks[assetId].MarketMark!,policy);
                var working = portfolio;
                if (safety)
                {
                    var exit = current.Side == TradeSide.Long ? book.Bids[0].Price : book.Asks[0].Price;
                    current = current with { StopPrice = current.Side == TradeSide.Long ? Math.Max(current.StopPrice,exit) : Math.Min(current.StopPrice,exit) };
                    working = portfolio with { Positions = portfolio.Positions.Select(item => item.PositionId == current.PositionId ? current : item).ToArray() };
                }
                var result = engine.Observe(working,current.PositionId,book,read.Evidence.Id,at);
                return safety ? result with { Events = result.Events.Select(item => item.Type == "Reduce" ? item with { Reason = "PaperMarginSafetyExit" } : item).ToArray() } : result;
            }
            var preview = Evaluate(store.Portfolio());
            if (preview.Events.Any(item => item.Type != "Mark") || now-position.LastObservedAt >= TimeSpan.FromSeconds(5))
            {
                store.SavePaperMarket(read);
                if (FreshMark(assetId,now)) store.SavePaperMarket(marks[assetId]);
                store.CommitPaper(CommandId($"observe:{position.PositionId}:{read.Evidence.Id}"),Evaluate);
            }
        }
    }
    private void Admit(Guid assetId,DateTimeOffset now)
    {
        if (store.Portfolio().Positions.Any(item => item.AssetId == assetId && item.RemainingQuantity > 0)) return;
        var asset = store.Assets().Single(item => item.AssetId == assetId);
        var universe = store.Universe();
        if (universe?.Universe?.Any(item => item.Symbol == asset.BinanceSymbol) != true ||
            universe.Evidence.ReceivedTime > now || now-universe.Evidence.ReceivedTime > TimeSpan.FromHours(8)) return;
        var context = store.MarketContext();
        if (context is null || context.AsOf > now || now-context.AsOf > TimeSpan.FromMinutes(3) ||
            context.Coverage is < .8m or > 1 || context.FreshQuotes < 30 ||
            context.State is MarketState.Unknown or MarketState.Panic or MarketState.Pressure) return;
        var snapshot = store.Latest(assetId);
        foreach (var metric in new[] { Metric.Ema20H1,Metric.Ema60H1,Metric.Atr14H1,Metric.VolumeRatioH1,Metric.TakerBuyShareH1,
            Metric.OiQuantityChange1h,Metric.Ema20H4,Metric.Ema60H4 })
        {
            var age = metric == Metric.OiQuantityChange1h ? TimeSpan.FromMinutes(10) :
                metric is Metric.Ema20H4 or Metric.Ema60H4 ? TimeSpan.FromHours(4.25) : TimeSpan.FromMinutes(75);
            var value = snapshot?.Metrics.SingleOrDefault(item => item.Metric == metric);
            if (snapshot is null || snapshot.AsOf > now || value is not { UsableForScoring: true,Value: not null,Provider: "Binance" } ||
                !value.Evidence.Any(item => item.Provider == "Binance" && item.AssetId == assetId && item.Value == value.Value &&
                    item.TimeQuality == TimeQuality.ProviderTime && item.SourceTime <= item.ReceivedTime && item.ReceivedTime <= snapshot.AsOf &&
                    item.SourceTime <= now && now-item.SourceTime <= age)) return;
        }
        var markRead = marks[assetId]; var mark = markRead.MarketMark!;
        if (mark.NextFundingAt-now <= TimeSpan.FromMinutes(1) || Math.Abs(mark.FundingRate) > .003m) return;
        var funding = store.LatestExecution(assetId)?.Funding;
        if (funding is null) return;
        var setups = Enum.GetValues<EntryStrategy>().SelectMany(strategy => Enum.GetValues<TradeSide>()
            .Select(side => store.LatestEntry(assetId,strategy,side))).Where(item => item?.Version == EntryEngine.Version &&
                item.Status == "PatternConfirmed" && item.AsOf <= now && item.Setup?.ConfirmedAt <= now &&
                now-item.Setup.ConfirmedAt <= TimeSpan.FromMinutes(1)).ToArray();
        foreach (var evaluation in setups)
        {
            var setup = evaluation!.Setup!;
            if (store.Portfolio().Positions.Any(item => item.SetupId == setup.SetupId)) continue;
            if (setup.Side == TradeSide.Long && context.State is MarketState.Bear or MarketState.StrongBear ||
                setup.Side == TradeSide.Short && context.State is MarketState.Bull or MarketState.StrongBull) continue;
            var book = books[assetId];
            var midpoint = (book.OrderBook!.Bids[0].Price+book.OrderBook.Asks[0].Price)/2;
            if (Math.Abs(midpoint/mark.Price-1) > .005m) continue;
            store.SavePaperMarket(book); store.SavePaperMarket(markRead);
            var evidence = new ExecutionEvidence(assetId,now,book,funding);
            var outcome = store.CommitPaper(CommandId($"open:{setup.SetupId}:{book.Evidence.Id}"),portfolio =>
            {
                var at = clock.GetUtcNow();
                if (!FreshBook(assetId,at) || !FreshMark(assetId,at) || at-context.AsOf > TimeSpan.FromMinutes(3))
                    return new(portfolio,[],"QuotesOrContextExpiredInsideTransaction");
                if (portfolio.ConservativeEquity <= 0 || portfolio.AvailableBalance <= 0)
                    return new(portfolio,[],"InsufficientEquityOrCash");
                var proposal = new SizingEngine().Evaluate(asset,setup,evidence,
                    universe.ContractRules?.SingleOrDefault(item => item.Symbol == asset.BinanceSymbol),
                    portfolio.ConservativeEquity,portfolio.AvailableBalance,limits,new("ConservativeCosts-2.0.0"),at);
                if (proposal.Quantity is null) return new(portfolio,[],proposal.Reasons[0]);
                if (!PaperMarginModel.StopHasBuffer(proposal,policy)) return new(portfolio,[],"PaperLiquidationBufferInsufficient");
                return engine.OpenReviewed(portfolio,proposal,"Unclassified",limits,at);
            });
            store.SavePaperAdmission(assetId,setup.SetupId,book.Evidence.Id,clock.GetUtcNow(),outcome.Status,policy.Version);
            detail = outcome.Status;
        }
    }
}
