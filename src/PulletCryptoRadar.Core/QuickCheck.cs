using System.Security.Cryptography;
using System.Text;

namespace PulletCryptoRadar.Core;

public static class BinanceIdentity
{
    public static Guid AssetId(string symbol) => new(SHA256.HashData(
        Encoding.UTF8.GetBytes("Binance:USDT:Perpetual:" + symbol)).AsSpan(0,16));
}

public sealed record TokenCandidate(Guid AssetId, string Symbol, string Name, string BinanceSymbol,
    IReadOnlyList<ChainToken> Chains, bool ExactMatch, bool ActiveContract);
public sealed record TokenSearch(string Query, string Status, bool CatalogFresh, IReadOnlyList<TokenCandidate> Candidates);
public sealed record QuickCheckHistory(Guid AssetId, DateTimeOffset LastViewedAt);
public sealed record QuickCheckReport(AssetIdentity Asset, DateTimeOffset EvaluatedAt, DateTimeOffset? SnapshotAsOf,
    string CollectionState, string TradeDecision, bool ActiveContract, RadarAssessment Research,
    IReadOnlyList<string> Findings, IReadOnlyList<ResolvedMetric> SnapshotMetrics);

public interface IQuickCheckStore
{
    IReadOnlyList<AssetIdentity> Assets();
    void Register(AssetIdentity identity);
    AssetIdentity EnsureRegistered(AssetIdentity identity);
    ProviderRead? Universe();
    bool ReserveJob(string name, DateTimeOffset now, TimeSpan interval);
    bool TryBeginQuickCheck(Guid assetId, DateTimeOffset now);
    void FinishQuickCheck(Guid assetId, DateTimeOffset startedAt, DateTimeOffset now);
    DateTimeOffset? QuickCheckCompleted(Guid assetId);
    void TouchQuickCheck(Guid assetId, DateTimeOffset now);
    IReadOnlyList<QuickCheckHistory> QuickCheckHistory();
    void DeleteQuickCheckHistory(Guid? assetId);
}
