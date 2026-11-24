using PulletCryptoRadar.Core;
using PulletCryptoRadar.Features;
using PulletCryptoRadar.Trading;

namespace PulletCryptoRadar.Host;

// Scenario replay of the real pattern/sizing/ledger kernels. Historical books and market-wide regime are unavailable here.
// Their assumptions are exposed; these results cannot be promoted to a production-equivalent backtest.
public sealed class QuantReplayEngine
{
    public const string Version="QuantKernelReplay-H1-Conservative-1.1";
    public QuantReplayResult Run(QuantResearchDataset data,MarketState regime,string costScenario="Baseline",EntryThresholds? thresholds=null,
        EntryConfirmationMode confirmationMode=EntryConfirmationMode.RepeatedParticipation,PaperRuntimePolicy? marginPolicy=null)
    {
        marginPolicy?.Validate();
        if(regime is not(MarketState.Mixed or MarketState.Bull or MarketState.Bear)||costScenario is not("Baseline" or "DoubleCosts"))
            throw new ArgumentException("Use declared regime/cost sensitivity scenarios.");
        var asset=new AssetIdentity(BinanceIdentity.AssetId(data.Symbol),data.Symbol[..^4],data.Symbol,data.Symbol,null,null,[],data.Start);
        var candles=data.Candles.OrderBy(row=>row.OpenAt).ToArray();var points=data.OpenInterest.OrderBy(row=>row.SourceTime).ToArray();
        if(candles.Any(row=>row.AssetId!=asset.AssetId)||points.Any(row=>row.AssetId!=asset.AssetId)||data.MarkCandles.Any(row=>row.AssetId!=asset.AssetId)||
            candles.Select(row=>row.OpenAt).Distinct().Count()!=candles.Length||points.Select(row=>row.SourceTime).Distinct().Count()!=points.Length||
            data.MarkCandles.Select(row=>row.OpenAt).Distinct().Count()!=data.MarkCandles.Count||data.End<=data.Start)
            throw new InvalidDataException("Replay input identity/chronology invalid.");
        var limits=new TradingLimits();var multiplier=costScenario=="DoubleCosts"?2m:1;
        var costs=new SizingAssumptions("ReplayAssumedCosts-"+costScenario,.0006m*multiplier,.001m*multiplier,.001m,.002m,.002m*multiplier);
        // These are explicit fixture rules, not asserted historical exchange filters.
        var rules=new ContractRules(data.Symbol,.000001m,1_000_000_000m,.000001m,5,.00000001m);
        var portfolio=new PaperPortfolio(Guid.NewGuid(),1000,1000,0,data.Start,[]);var kernel=new PaperPortfolioEngine();
        var entries=new EntryEngine(thresholds,confirmationMode);var setups=new Dictionary<(EntryStrategy,TradeSide),EntrySetup?>();
        var ledger=new List<PaperLedgerEvent>();var reasons=new Dictionary<string,int>();
        var entryDiagnostics=new Dictionary<(EntryStrategy Strategy,TradeSide Side,string Reason),int>();
        var decisionBars=0;var oiMissing=0;var candleMissing=0;var confirmed=0;var sizingRejected=0;
        decimal peakEquity=1000,drawdown=0;long sequence=0;DateTimeOffset? fundingCursor=null;
        void Count(string reason){reasons[reason]=reasons.GetValueOrDefault(reason)+1;}
        void Apply(PaperTransition transition){portfolio=transition.Portfolio;ledger.AddRange(transition.Events);}
        OrderBook Book(decimal price,DateTimeOffset now,TradeSide side,bool exit=true)
        {
            return AssumedBook(asset.AssetId,price,now,side,exit,multiplier,++sequence);
        }
        ProviderRead Read(string endpoint,DateTimeOffset now)=>new(new(Guid.NewGuid(),"Binance",data.Symbol,endpoint,now,200,
            CollectionFailure.None,"REPLAY: synthetic execution quote, not historical order book"),[]);
        for(var index=0;index<candles.Length;index++)
        {
            var bar=candles[index];if(bar.OpenAt<data.Start||bar.OpenAt>=data.End)continue;
            // No invented prices or exits across a missing hour. Keep the position open and report the gap.
            if(index>0&&candles[index-1].ClosedAt!=bar.OpenAt)Count("HistoricalCandleGap");
            var active=portfolio.Positions.FirstOrDefault(row=>row.RemainingQuantity>0);
            if(active is not null)
            {
                foreach(var funding in data.Funding.Where(row=>row.At>=bar.OpenAt&&row.At<bar.ClosedAt&&row.At>active.OpenedAt&&
                    (fundingCursor is null||row.At>fundingCursor)).OrderBy(row=>row.At))
                {
                    var mark=FundingMark(data.MarkCandles,funding.At);
                    if(mark is null){Count("FundingMarkMissing");continue;}
                    var fundingAt=funding.At.AddSeconds(20);
                    var settlement=new FundingSettlement(asset.AssetId,funding.At,fundingAt,funding.Rate,mark.Open,"Regular");
                    var transition=kernel.Funding(portfolio,active.PositionId,settlement,ledger.Where(row=>row.PositionId==active.PositionId).ToArray(),Guid.NewGuid(),fundingAt);
                    Apply(transition);if(transition.Status!="FundingApplied")Count(transition.Status);fundingCursor=funding.At;
                }
                var exitPrices=ConservativeExitPath(active,bar,multiplier);
                var at=bar.ClosedAt;
                foreach(var reference in exitPrices)
                {
                    if(portfolio.Positions.Single(row=>row.PositionId==active.PositionId).RemainingQuantity==0)break;
                    at=at.AddMilliseconds(1);Apply(kernel.Observe(portfolio,active.PositionId,Book(reference,at,active.Side),Guid.NewGuid(),at));
                }
                var current=portfolio.Positions.Single(row=>row.PositionId==active.PositionId);
                // After a target tightens the stop, assume a possible later intrabar adverse crossing.
                if(current.RemainingQuantity>0&&current.StopPrice!=active.StopPrice&&
                    (current.Side==TradeSide.Long?bar.Low<=current.StopPrice:bar.High>=current.StopPrice))
                {
                    at=at.AddMilliseconds(1);Apply(kernel.Observe(portfolio,current.PositionId,Book(current.StopPrice,at,current.Side),Guid.NewGuid(),at));
                    Count("IntrabarSequenceAssumedAdverse");
                }
            }
            var equity=portfolio.Balance+portfolio.Positions.Where(row=>row.RemainingQuantity>0).Sum(row=>
                (row.Side==TradeSide.Long?1:-1)*(bar.Close-row.EntryPrice)*row.RemainingQuantity);
            peakEquity=Math.Max(peakEquity,equity);drawdown=Math.Min(drawdown,(equity/peakEquity-1)*100);
            // Decisions at the next-hour boundary with an explicit 1-second virtual availability assumption.
            var now=bar.ClosedAt.AddSeconds(1);if(now>=data.End)continue;decisionBars++;
            var history=candles.Take(index+1).TakeLast(300).ToArray();
            if(history.Length<244||history.Zip(history.Skip(1)).Any(pair=>pair.First.ClosedAt!=pair.Second.OpenAt))
            {candleMissing++;Count("H1H4WarmupOrGap");continue;}
            var virtualHistory=history.Select(row=>row with {ReceivedAt=now}).ToArray();
            var observations=new HourFeatureEngine().Calculate(asset.AssetId,virtualHistory,now)
                .Concat(new FourHourFeatureEngine().Calculate(asset.AssetId,virtualHistory,now)).ToList();
            // An OI bucket is assumed available five minutes after its source time; never use a future bucket.
            var known=KnownOi(points,now).TakeLast(289).Select(row=>row with {ReceivedTime=now}).ToArray();
            observations.AddRange(new OpenInterestFeatureEngine().Calculate(asset.AssetId,known,now));
            var fields=observations.GroupBy(row=>row.Metric).Select(group=>
            {
                var row=group.Last();var maxAge=row.Metric is Metric.OiQuantityChange1h or Metric.OiQuantityChange4h or Metric.OiQuantityChange24h or
                    Metric.OiNotionalChange1h or Metric.OiNotionalChange4h or Metric.OiNotionalChange24h?TimeSpan.FromMinutes(10):
                    row.Metric is Metric.Ema20H4 or Metric.Ema60H4 or Metric.CloseH4?TimeSpan.FromHours(4.25):TimeSpan.FromMinutes(75);
                var valid=row.Value is not null&&row.Status==ObservationStatus.Valid&&row.SourceTime<=now&&now-row.SourceTime<=maxAge;
                return new ResolvedMetric(row.Metric,valid?ResolutionStatus.SingleSource:ResolutionStatus.Missing,valid?row.Value:null,"Binance",valid,[row]);
            }).ToArray();
            if(!fields.Any(row=>row.Metric==Metric.OiQuantityChange1h&&row.UsableForScoring))oiMissing++;
            var snapshot=new FeatureSnapshot("ReplayVirtualAvailability",asset.AssetId,now,regime,fields);
            var read=Read("replay-H1",now) with {Candles=virtualHistory};
            // Explicit scenario, not a fabricated reconstruction of historical all-market breadth.
            var context=new MarketContext("AssumedRegimeSensitivity",now,regime,30,30,1,null,null,["HistoricalRegimeNotReconstructed"]);
            foreach(var strategy in Enum.GetValues<EntryStrategy>())foreach(var side in Enum.GetValues<TradeSide>())
            {
                var key=(strategy,side);var prior=setups.GetValueOrDefault(key);
                var evaluation=entries.Evaluate(asset.AssetId,strategy,side,snapshot,read,context,prior,now);
                foreach(var reason in evaluation.Reasons.Where(row=>row is not("ExecutionAdmissionPending" or "UpcomingFundingScheduleUnverified" or "PortfolioRiskNotConnected")))
                {
                    Count(reason);var diagnostic=(strategy,side,reason);entryDiagnostics[diagnostic]=entryDiagnostics.GetValueOrDefault(diagnostic)+1;
                }
                setups[key]=evaluation.Setup;
                if(evaluation.Setup is not {Phase:SetupPhase.PatternConfirmed} setup||prior?.Phase==SetupPhase.PatternConfirmed)continue;
                confirmed++;
                if(portfolio.Positions.Any(row=>row.RemainingQuantity>0)){Count("ReplaySingleAssetAlreadyHeld");continue;}
                if(portfolio.ConservativeEquity<=0||portfolio.AvailableBalance<=0){Count("ReplayCapitalDepleted");continue;}
                var next=candles.ElementAtOrDefault(index+1);
                if(next?.OpenAt!=bar.ClosedAt){Count("NextOpeningReferenceMissing");continue;}
                var lastFunding=data.Funding.Where(row=>row.At.AddMinutes(5)<=now).MaxBy(row=>row.At);
                var lastMark=lastFunding is null?null:FundingMark(data.MarkCandles,lastFunding.At);
                if(lastFunding is null||lastMark is null){Count("PriorFundingOrMarkMissing");continue;}
                var execution=new ExecutionEvidence(asset.AssetId,now,Read("replay-opening-book",now) with {OrderBook=Book(next.Open,now,setup.Side,false)},
                    Read("replay-known-funding",now) with {FundingSettlements=[new(asset.AssetId,lastFunding.At,now,lastFunding.Rate,lastMark.Open,"Regular")]});
                var proposal=new SizingEngine().Evaluate(asset,setup,execution,rules,portfolio.ConservativeEquity,portfolio.AvailableBalance,limits,costs,now);
                if(proposal.Quantity is null){sizingRejected++;foreach(var reason in proposal.Reasons)Count(reason);continue;}
                if(marginPolicy is not null&&!PaperMarginModel.StopHasBuffer(proposal,marginPolicy))
                {sizingRejected++;Count("PaperLiquidationBufferInsufficient");continue;}
                var transition=kernel.OpenReviewed(portfolio,proposal,"ReplaySingleAsset",limits,now);
                Apply(transition);if(transition.Status!="Opened"){sizingRejected++;Count(transition.Status);}
            }
        }
        var trades=portfolio.Positions.Select(position=>new QuantReplayTrade(position.Strategy,position.Side,position.OpenedAt,position.ClosedAt,
            position.EntryPrice,position.InitialStop,position.InitialQuantity,position.RealizedPnl-position.FeesPaid+position.FundingPnl,
            position.FeesPaid,position.FundingPnl,position.MfeR,position.MaeR,position.RemainingQuantity==0?"Closed":"OpenAtWindowEnd")).ToArray();
        return new(data.Symbol,data.Partition,regime.ToString(),costScenario,decisionBars,oiMissing,candleMissing,confirmed,sizingRejected,
            1000,portfolio.Balance,drawdown,trades,reasons,
            ["假设市场状态情境，不是历史全市场广度重建；情境之间不能合并为真实交易样本",
             "历史盘口与逐期合约过滤器未取得；按显式点差/摩擦、无限深度及研究数量规则模拟，成交不等价真实市场",
             "K线于闭合后1秒、OI桶于源时间后5分钟视为可得，属于虚拟可用时间假设；不是原始发布时间证明",
             "OHLC内先止损、再不利顺序；回撤按收盘权益，不能覆盖盘中最差杠杆风险",
             "盘中目标与止损按OHLC推断并记在小时闭合后，真实触发时间未知；资金费持仓路径可能偏离实际，MFE/MAE仅为模拟观察点",
             "未复原交易所维护保证金档位与盘中强平路径，不能将本结果当作无穿仓保证",
             marginPolicy is null?"本情境未应用生产模拟的止损清算缓冲准入，不得把内核交易当作生产可执行成交":
                "已应用生产模拟止损清算缓冲准入，但没有逐笔历史标记价/盘中安全退出还原，不能当作完整清算验证",
             "单资产1000U独立情境，不是多资产6仓组合回测；窗口末未平仓单独标记，不伪造强制平仓",
             "历史资金费保留归档实际毫秒结算时间与费率，以结算时间1秒内的该小时标记价开盘参考估算；并非精确结算标记价证明，缺项显式计数"],
             entryDiagnostics.OrderBy(row=>row.Key.Strategy).ThenBy(row=>row.Key.Side).ThenBy(row=>row.Key.Reason,StringComparer.Ordinal)
                .Select(row=>new ReplayEntryDiagnostic(row.Key.Strategy,row.Key.Side,row.Key.Reason,row.Value)).ToArray());
    }
    public static IEnumerable<OpenInterestPoint> KnownOi(IReadOnlyList<OpenInterestPoint> points,DateTimeOffset now)=>
        points.Where(row=>row.SourceTime.AddMinutes(5)<=now);
    public static HourCandle? FundingMark(IReadOnlyList<HourCandle> marks,DateTimeOffset settledAt)=>
        marks.SingleOrDefault(row=>row.OpenAt<=settledAt&&settledAt-row.OpenAt<=TimeSpan.FromSeconds(1));
    public static OrderBook AssumedBook(Guid asset,decimal price,DateTimeOffset now,TradeSide side,bool exit,decimal costMultiplier,long sequence)
    {
        if(price<=0||costMultiplier is <1 or >2)throw new ArgumentException("Invalid replay quote assumption.");
        var direction=(side==TradeSide.Long?1m:-1m)*(exit?-1:1);
        var reference=price*(1+direction*.001m*costMultiplier);
        return new(asset,sequence,now,now,now,[new(reference*(1-.0001m),1_000_000_000m)],
            [new(reference*(1+.0001m),1_000_000_000m)]);
    }
    public static IReadOnlyList<decimal> ConservativeExitPath(PaperPosition position,HourCandle bar,decimal costMultiplier=1)
    {
        var sign=position.Side==TradeSide.Long?1:-1;var distance=Math.Abs(position.EntryPrice-position.InitialStop);
        if(position.Side==TradeSide.Long?bar.Low<=position.StopPrice:bar.High>=position.StopPrice)
            return [position.Side==TradeSide.Long?Math.Min(position.StopPrice,bar.Open):Math.Max(position.StopPrice,bar.Open)];
        var prices=new List<decimal>();var favorable=position.Side==TradeSide.Long?bar.High:bar.Low;
        foreach(var (done,r) in new[]{(position.FirstTargetDone,1.5m),(position.SecondTargetDone,2.5m)})
        {
            var target=position.EntryPrice+sign*r*distance;
            if(done||target<=0)continue;
            // Targets are tested against executable bid/ask, whereas OHLC is a raw price reference.
            // Invert the quote transform once; passing a target directly would apply friction twice
            // and miss a genuine intrabar scale-out when the close later returns below the target.
            var unit=AssumedBook(position.AssetId,1,bar.ClosedAt,position.Side,true,costMultiplier,1);
            var factor=position.Side==TradeSide.Long?unit.Bids[0].Price:unit.Asks[0].Price;
            var reference=target/factor;
            // Decimal division may round to the non-triggering side. Move only a negligible
            // reference amount toward the favorable side, and still require the bar to reach it.
            reference+=sign*Math.Max(Math.Abs(reference)*.000000000000000000000001m,.000000000000000000000001m);
            if(sign*(favorable-reference)>=0)prices.Add(reference);
        }
        prices.Add(bar.Close);return prices;
    }
}
