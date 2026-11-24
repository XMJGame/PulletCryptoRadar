namespace PulletCryptoRadar.Core;

public enum ChainId { Ethereum, Bsc, Solana, Base, Arbitrum, Polygon }
public enum ObservationStatus { Valid, Missing, Invalid, Unsupported }
public enum TimeQuality { Unknown, ReceivedOnly, HttpDate, ProviderTime, BlockTime }
public enum ResolutionStatus { SingleSource, Confirmed, Conflict, Stale, Missing, UnverifiedTime }
public enum MarketState { Unknown, StrongBull, Bull, Mixed, Bear, StrongBear, Pressure, Panic }
public enum Metric { Price, Volume24h, OpenInterestUsd, Funding8h, MarketCap, Fdv, CirculatingSupply, DexLiquidity, MarkPrice, FundingRaw, OpenInterestQuantity, Gini, Hhi, Nakamoto, Top10, TotalClusterPercent, Ema20H1, Ema60H1, Atr14H1, Return24hClosed, VolumeRatioH1, Ema20H4, Ema60H4, CloseH4, FundingIntervalHours, TakerBuyShareH1, OiQuantityChange1h, OiQuantityChange4h, OiQuantityChange24h, OiNotionalChange1h, OiNotionalChange4h, OiNotionalChange24h }

public sealed record OpenInterestPoint(Guid AssetId, DateTimeOffset SourceTime, DateTimeOffset ReceivedTime,
    decimal Quantity, decimal NotionalUsdt);

public sealed record HourCandle(Guid AssetId, DateTimeOffset OpenAt, DateTimeOffset ClosedAt,
    DateTimeOffset ReceivedAt, decimal Open, decimal High, decimal Low, decimal Close, decimal QuoteVolume,
    decimal? TakerBuyQuoteVolume = null);

public sealed record ChainToken(ChainId Chain, string Address, int Decimals);
public sealed record AssetIdentity(Guid AssetId, string Symbol, string Name, string BinanceSymbol,
    string? CoinGeckoId, int? CoinMarketCapId, IReadOnlyList<ChainToken> Chains, DateTimeOffset VerifiedAt);

public sealed record Observation(Guid AssetId, Metric Metric, decimal? Value, string Provider,
    string Methodology, DateTimeOffset? SourceTime, DateTimeOffset ReceivedTime,
    ObservationStatus Status, TimeQuality TimeQuality, ChainId? Chain = null, string? Contract = null);

public sealed record ResolvedMetric(Metric Metric, ResolutionStatus Status, decimal? Value,
    string? Provider, bool UsableForScoring, IReadOnlyList<Observation> Evidence);

public sealed record FeatureSnapshot(string Version, Guid AssetId, DateTimeOffset AsOf,
    MarketState MarketState, IReadOnlyList<ResolvedMetric> Metrics);

public sealed record PaperAccount(Guid AccountId, decimal InitialCash, decimal Cash, DateTimeOffset StartedAt);

public sealed record MarketInstrument(string Symbol, string BaseAsset, DateTimeOffset OnboardAt);
public sealed record MarketTicker(string Symbol, decimal Return24h, decimal QuoteVolume, DateTimeOffset SourceTime);
public sealed record MarketContext(string Version, DateTimeOffset AsOf, MarketState State, int EligibleContracts,
    int FreshQuotes, decimal Coverage, decimal? PositiveBreadth24h, decimal? MedianReturn24h,
    IReadOnlyList<string> Reasons);

public static class IdentityValidation
{
    public static void Validate(AssetIdentity identity)
    {
        if (identity.AssetId == Guid.Empty || string.IsNullOrWhiteSpace(identity.Symbol) ||
            string.IsNullOrWhiteSpace(identity.Name) || !identity.BinanceSymbol.EndsWith("USDT", StringComparison.Ordinal) ||
            identity.VerifiedAt == default || identity.CoinMarketCapId is <= 0)
            throw new ArgumentException("Invalid asset identity.");
        if (identity.Chains.GroupBy(token => token.Chain).Any(group => group.Count() > 1))
            throw new ArgumentException("A chain can have only one verified contract per asset.");
        foreach (var token in identity.Chains)
        {
            if (token.Decimals is < 0 or > 30 || !IsAddress(token.Chain, token.Address))
                throw new ArgumentException("Invalid chain contract.");
        }
    }

    public static bool IsAddress(ChainId chain, string address)
    {
        if (chain != ChainId.Solana)
            return System.Text.RegularExpressions.Regex.IsMatch(address, @"\A0x[0-9a-fA-F]{40}\z");
        if (address.Length is < 32 or > 44) return false;
        const string alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
        var value = System.Numerics.BigInteger.Zero;
        foreach (var character in address)
        {
            var digit = alphabet.IndexOf(character);
            if (digit < 0) return false;
            value = value * 58 + digit;
        }
        var leadingZeros = address.TakeWhile(character => character == '1').Count();
        return leadingZeros + (value.IsZero ? 0 : value.GetByteCount(isUnsigned: true)) == 32;
    }
}
