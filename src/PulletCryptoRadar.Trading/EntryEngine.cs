using System.Security.Cryptography;
using System.Text;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Trading;

public sealed class EntryEngine
{
    public const string Version = "EntryPatterns-2.0.0";
    private readonly EntryThresholds thresholds;
    private readonly EntryConfirmationMode confirmationMode;
    public EntryEngine(EntryThresholds? thresholds=null,EntryConfirmationMode confirmationMode=EntryConfirmationMode.RepeatedParticipation)
    {
        this.thresholds=thresholds??EntryThresholds.Production;
        this.thresholds.Validate();
        if(!Enum.IsDefined(confirmationMode))throw new ArgumentException("Invalid confirmation mode.");
        this.confirmationMode=confirmationMode;
    }

    public EntryEvaluation Evaluate(Guid assetId, EntryStrategy strategy, TradeSide side, FeatureSnapshot? snapshot,
        ProviderRead? candleRead, MarketContext? context, EntrySetup? previous, DateTimeOffset now)
    {
        if (previous is not null && (previous.AssetId != assetId || previous.Side != side || previous.Strategy != strategy ||
            previous.ArmedAt > now)) throw new ArgumentException("Invalid previous setup.");
        if (previous?.Phase == SetupPhase.Expired && candleRead?.Candles?.LastOrDefault()?.ClosedAt >= previous.ExpiresAt)
            previous = null;
        var reasons = new List<string>();
        var version=thresholds==EntryThresholds.Production?Version:Version+":"+thresholds.Version;
        if(confirmationMode==EntryConfirmationMode.ArmedParticipationThenPrice)version+=":ArmedParticipationThenPrice-1.0";
        EntryEvaluation Result(string status,EntrySetup? setup = null) => new(version,assetId,strategy,side,now,status,false,
            setup,reasons.Concat(["ExecutionAdmissionPending","UpcomingFundingScheduleUnverified","PortfolioRiskNotConnected"]).ToArray(),
            snapshot?.AsOf,context?.AsOf,candleRead?.Evidence.Id);
        if (previous is not null && now >= previous.ExpiresAt)
        {
            reasons.Add(previous.Phase==SetupPhase.AwaitingConfirmation?"SetupTimedOut":"TerminalPatternRearmWindowReached");
            return Result("Expired",previous with { Phase = SetupPhase.Expired });
        }
        if (previous?.Phase is SetupPhase.Invalidated or SetupPhase.PatternConfirmed or SetupPhase.Expired)
        {
            reasons.Add("StoredTerminalPatternNotNewAdmission");
            return Result(previous.Phase.ToString(),previous);
        }
        var candles = candleRead?.Candles;
        if (candleRead is null || candleRead.Evidence.Provider != "Binance" || candleRead.Evidence.Failure != CollectionFailure.None ||
            candleRead.Evidence.ReceivedTime > now || candles is null || candles.Count < 61 ||
            candles.Any(candle => candle.AssetId != assetId || candle.ReceivedAt > now || candle.ClosedAt > candle.ReceivedAt ||
                candle.ReceivedAt > candleRead.Evidence.ReceivedTime || candle.OpenAt.ToUnixTimeMilliseconds()%3600000 != 0 ||
                candle.ClosedAt-candle.OpenAt != TimeSpan.FromHours(1) || candle.Low <= 0 || candle.High < candle.Low ||
                candle.Open < candle.Low || candle.Open > candle.High || candle.Close < candle.Low || candle.Close > candle.High ||
                candle.QuoteVolume < 0) ||
            candles.Zip(candles.Skip(1),(a,b) => a.ClosedAt == b.OpenAt).Any(valid => !valid) ||
            now-candles[^1].ClosedAt > TimeSpan.FromMinutes(75))
        {
            reasons.Add("ClosedBinanceCandlesUnavailable");
            return Result("Blocked",previous);
        }
        var last = candles[^1];
        var sign = side == TradeSide.Long ? 1m : -1m;
        if (previous is not null && last.ClosedAt > previous.ArmedBarClosedAt &&
            candles.Where(candle => candle.ClosedAt > previous.ArmedBarClosedAt).Any(candle =>
                side == TradeSide.Long ? candle.Low <= previous.StructuralStop : candle.High >= previous.StructuralStop))
        {
            reasons.Add("StructuralStopBreachedBeforeConfirmation");
            return Result("Invalidated",previous with { Phase = SetupPhase.Invalidated });
        }
        decimal? Value(Metric metric,TimeSpan age,bool sameBar = false)
        {
            var item = snapshot?.Metrics.SingleOrDefault(item => item.Metric == metric);
            if (snapshot is null || snapshot.AssetId != assetId || snapshot.AsOf > now ||
                item is not { UsableForScoring: true, Value: not null, Provider: "Binance" } ||
                item.Status is not (ResolutionStatus.SingleSource or ResolutionStatus.Confirmed) ||
                !item.Evidence.Any(source => source.AssetId == assetId && source.Metric == metric && source.Provider == "Binance" &&
                    source.Status == ObservationStatus.Valid && source.Value == item.Value && source.TimeQuality == TimeQuality.ProviderTime &&
                    source.SourceTime <= source.ReceivedTime && source.ReceivedTime <= snapshot.AsOf && source.SourceTime <= now &&
                    now-source.SourceTime <= age && (!sameBar || source.SourceTime == last.ClosedAt) &&
                    (metric is not (Metric.Ema20H4 or Metric.Ema60H4) || source.SourceTime ==
                        DateTimeOffset.FromUnixTimeMilliseconds(last.ClosedAt.ToUnixTimeMilliseconds()/14400000*14400000))))
            {
                reasons.Add("MissingOrStale:"+metric);
                return null;
            }
            return item.Value;
        }
        var ema20 = Value(Metric.Ema20H1,TimeSpan.FromMinutes(75),true);
        var ema60 = Value(Metric.Ema60H1,TimeSpan.FromMinutes(75),true);
        var atr = Value(Metric.Atr14H1,TimeSpan.FromMinutes(75),true);
        var volume = Value(Metric.VolumeRatioH1,TimeSpan.FromMinutes(75),true);
        var taker = Value(Metric.TakerBuyShareH1,TimeSpan.FromMinutes(75),true);
        var oi = Value(Metric.OiQuantityChange1h,TimeSpan.FromMinutes(10));
        var h4ema20 = Value(Metric.Ema20H4,TimeSpan.FromHours(4.25));
        var h4ema60 = Value(Metric.Ema60H4,TimeSpan.FromHours(4.25));
        if (reasons.Count > 0) return Result("Blocked",previous);
        var atrValue = atr!.Value;
        if (atr is <= 0 || ema20 is <= 0 || ema60 is <= 0 || h4ema20 is <= 0 || h4ema60 is <= 0 || taker is < 0 or > 1)
        {
            reasons.Add("InvalidFeatureDomain");
            return Result("Blocked",previous);
        }
        if (context is null || context.AsOf > now || now-context.AsOf > TimeSpan.FromMinutes(3) ||
            context.Coverage is < .8m or > 1 || context.FreshQuotes < 30 || context.FreshQuotes > context.EligibleContracts ||
            context.State is MarketState.Unknown or MarketState.Panic or MarketState.Pressure ||
            side == TradeSide.Long && context.State is MarketState.Bear or MarketState.StrongBear ||
            side == TradeSide.Short && context.State is MarketState.Bull or MarketState.StrongBull)
        {
            reasons.Add("MarketContextUnavailableOrOpposed");
            return Result("Blocked",previous);
        }
        if (sign*(ema20!.Value-ema60!.Value) <= 0 || sign*(h4ema20!.Value-h4ema60!.Value) <= 0)
        {
            reasons.Add("TrendNotAligned");
            if (sign*(ema20!.Value-ema60!.Value) <= 0) reasons.Add("H1TrendNotAligned");
            if (sign*(h4ema20!.Value-h4ema60!.Value) <= 0) reasons.Add("H4TrendNotAligned");
            return previous is null ? Result("NoSetup") : Result("Invalidated",previous with { Phase = SetupPhase.Invalidated });
        }
        var directionalTaker = side == TradeSide.Long ? taker : 1-taker;
        var volumeThreshold=strategy==EntryStrategy.BreakoutRetest&&previous is null?thresholds.BreakoutVolume:thresholds.PullbackVolume;
        var requireParticipation=previous is null||confirmationMode==EntryConfirmationMode.RepeatedParticipation;
        if (requireParticipation&&(volume < volumeThreshold || directionalTaker < thresholds.DirectionalTaker || oi < thresholds.OiGrowthPercent))
        {
            reasons.Add("ParticipationInsufficient");
            reasons.Add(previous is null?"ArmingParticipationInsufficient":"ConfirmationParticipationInsufficient");
            if (volume < volumeThreshold) reasons.Add("VolumeRatioBelowThreshold");
            if (directionalTaker < thresholds.DirectionalTaker) reasons.Add(thresholds.DirectionalTaker==.52m?"DirectionalTakerBelow52Percent":"DirectionalTakerBelowResearchThreshold");
            if (oi < thresholds.OiGrowthPercent) reasons.Add(thresholds.OiGrowthPercent==1?"OiGrowthBelow1Percent":"OiGrowthBelowResearchThreshold");
            return Result(previous is null ? "NoSetup" : "Waiting",previous);
        }
        if (previous is not null)
        {
            if(!requireParticipation)reasons.Add("ConfirmationUsesArmedParticipation");
            if (last.ClosedAt <= previous.ArmedBarClosedAt)
            {
                reasons.Add("AwaitNextClosedBar");
                return Result("Waiting",previous);
            }
            var confirms = strategy == EntryStrategy.TrendPullback
                ? sign*(last.Close-previous.Trigger) > 0
                : (side == TradeSide.Long ? last.Low <= previous.Level+.25m*previous.AtrAtArm : last.High >= previous.Level-.25m*previous.AtrAtArm) &&
                    sign*(last.Close-previous.Level) > 0 && sign*(last.Close-last.Open) > 0 &&
                    sign*(last.Close-previous.Level) <= previous.AtrAtArm;
            if (confirms)
            {
                reasons.Add("ClosedBarPatternConfirmed");
                return Result("PatternConfirmed",previous with { Phase = SetupPhase.PatternConfirmed,ConfirmedAt = now,
                    ConfirmationBarClosedAt = last.ClosedAt });
            }
            reasons.Add("ConfirmationNotReached");
            return Result("Waiting",previous);
        }
        var history = candles.Skip(candles.Count-21).Take(20).ToArray();
        var level = strategy == EntryStrategy.TrendPullback ? ema20.Value :
            side == TradeSide.Long ? history.Max(candle => candle.High) : history.Min(candle => candle.Low);
        var arms = strategy == EntryStrategy.TrendPullback
            ? (side == TradeSide.Long ? last.Low <= level+.25m*atrValue : last.High >= level-.25m*atrValue) &&
                sign*(last.Close-level) > 0 && sign*(last.Close-level) <= atrValue && sign*(last.Close-last.Open) > 0
            : sign*(last.Close-level) > 0 && sign*(last.Close-level) <= 2*atrValue;
        if (!arms)
        {
            reasons.Add("PatternNotPresentOrExtended");
            return Result("NoSetup");
        }
        var swing = candles.TakeLast(5).ToArray();
        var stop = side == TradeSide.Long ? swing.Min(candle => candle.Low)-.1m*atrValue : swing.Max(candle => candle.High)+.1m*atrValue;
        if (stop <= 0 || sign*(last.Close-stop) <= 0)
        {
            reasons.Add("InvalidStructuralStop");
            return Result("Blocked");
        }
        var setupId = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"{version}:{assetId}:{strategy}:{side}:{last.ClosedAt:O}")).AsSpan(0,16));
        var setup = new EntrySetup(setupId,assetId,strategy,side,SetupPhase.AwaitingConfirmation,now,last.ClosedAt,
            last.ClosedAt.AddHours(4),level,side == TradeSide.Long ? last.High : last.Low,stop,atrValue);
        reasons.Add(strategy == EntryStrategy.TrendPullback ? "PullbackArmed" : "BreakoutArmedAwaitRetest");
        return Result("Waiting",setup);
    }
}
