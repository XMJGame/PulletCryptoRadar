namespace PulletCryptoRadar.Core;

public sealed record MarketMark(Guid AssetId,decimal Price,decimal FundingRate,DateTimeOffset NextFundingAt,
    DateTimeOffset SourceTime,DateTimeOffset ReceivedTime);
public sealed record PaperRuntimePolicy
{
    public bool Enabled { get; init; }
    public string[] ObserveSymbols { get; init; } = [];
    public decimal MaintenanceFraction { get; init; } = .05m;
    public decimal LiquidationBufferFraction { get; init; } = .02m;
    public string Version { get; init; } = "ConservativeIsolatedPaper-2.0.0";
    public void Validate()
    {
        if (MaintenanceFraction is <= 0 or > .05m || LiquidationBufferFraction is < .01m or > .1m ||
            string.IsNullOrWhiteSpace(Version) || ObserveSymbols.Length > 6)
            throw new ArgumentException("Invalid paper margin policy.");
    }
}
public sealed record PaperRuntimeState(DateTimeOffset CheckedAt,string State,int Watching,int FreshBooks,int FreshMarks,
    bool AdmissionEnabled,string? Detail);
public interface IPositionMarketStream
{
    IAsyncEnumerable<ProviderRead> ReadAsync(IReadOnlyList<AssetIdentity> assets,CancellationToken token);
}
public interface IPaperMarketStore
{
    void SavePaperMarket(ProviderRead read);
}
public sealed record PaperAdmissionResult(string Status,PaperTransition? Transition);
