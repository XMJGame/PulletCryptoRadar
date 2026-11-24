namespace PulletCryptoRadar.Core;

public enum TradeSide { Long, Short }
public enum EntryStrategy { TrendPullback, BreakoutRetest }
public enum SetupPhase { AwaitingConfirmation, PatternConfirmed, Invalidated, Expired }
public enum EntryConfirmationMode { RepeatedParticipation, ArmedParticipationThenPrice }
public sealed record EntryThresholds(string Version,decimal OiGrowthPercent=1m,decimal PullbackVolume=1.2m,
    decimal BreakoutVolume=1.5m,decimal DirectionalTaker=.52m)
{
    public static EntryThresholds Production { get; }=new("EntryThresholds-1.0");
    public void Validate()
    {
        if(string.IsNullOrWhiteSpace(Version)||OiGrowthPercent is <0 or >100||PullbackVolume is <0 or >10||
            BreakoutVolume is <0 or >10||DirectionalTaker is <.5m or >1)
            throw new ArgumentException("Invalid entry thresholds.");
    }
}
public sealed record EntrySetup(Guid SetupId, Guid AssetId, EntryStrategy Strategy, TradeSide Side,
    SetupPhase Phase, DateTimeOffset ArmedAt, DateTimeOffset ArmedBarClosedAt, DateTimeOffset ExpiresAt,
    decimal Level, decimal Trigger, decimal StructuralStop, decimal AtrAtArm,
    DateTimeOffset? ConfirmedAt = null, DateTimeOffset? ConfirmationBarClosedAt = null);
public sealed record EntryEvaluation(string Version, Guid AssetId, EntryStrategy Strategy, TradeSide Side,
    DateTimeOffset AsOf, string Status, bool ExecutionAllowed, EntrySetup? Setup, IReadOnlyList<string> Reasons,
    DateTimeOffset? FeatureAsOf, DateTimeOffset? MarketContextAsOf, Guid? CandleEvidenceId);

public interface IEntryStore
{
    ProviderRead? LatestCandles(Guid assetId);
    EntryEvaluation? LatestEntry(Guid assetId, EntryStrategy strategy, TradeSide side);
    void SaveEntry(EntryEvaluation evaluation);
    IReadOnlyList<EntryEvaluation> Entries(Guid? assetId = null, int maximum = 100);
}
