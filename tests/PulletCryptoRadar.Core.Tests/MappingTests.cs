using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Core.Tests;

public sealed class MappingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 4, 0, 0, TimeSpan.Zero);
    private readonly AssetMappingEngine _engine = new();
    private static Instrument Instrument(string symbol = "TEST") => new(1, symbol + "USDT", symbol, Now.AddDays(-30), true);
    private static CoinCatalog Catalog(params MarketCoin[] coins) => new(Now, coins);
    private static MarketCoin Coin(string id = "test-coin", string symbol = "test") => new(id, symbol, id, new Dictionary<string, string>());
    [Fact]
    public void SingleSymbolMatchIsOnlyCandidateAndNotVerifiedIdentity()
    {
        var mapping = _engine.Suggest(Instrument(), Catalog(Coin()), null, Now);
        Assert.Equal(MappingStatus.SymbolMatched, mapping.Status); Assert.Equal("test-coin", mapping.CoinGeckoId); Assert.Equal(1, mapping.Revision);
    }
    [Fact]
    public void SameSymbolCoinsCannotBeChosenByNameOrMarketPopularity()
    {
        var mapping = _engine.Suggest(Instrument(), Catalog(Coin("a"), Coin("b")), null, Now);
        Assert.Equal(MappingStatus.Ambiguous, mapping.Status); Assert.Null(mapping.CoinGeckoId); Assert.Equal(new[] { "a", "b" }, mapping.CandidateIds);
    }
    [Theory] [InlineData("1000PEPE", "pepe", 1000)] [InlineData("1000000MOG", "mog", 1000000)]
    public void NumberedContractsGetExplicitUnitHintWithoutChangingAssetIdentity(string baseAsset, string symbol, int multiplier)
    {
        var mapping = _engine.Suggest(Instrument(baseAsset), Catalog(Coin("token", symbol)), null, Now);
        Assert.Equal(multiplier, mapping.ContractMultiplier); Assert.Equal(1, mapping.AssetId); Assert.Equal("token", mapping.CoinGeckoId);
    }
    [Fact]
    public void ExactNumberedSymbolTakesPriorityOverPrefixStripping()
    {
        var mapping = _engine.Suggest(Instrument("1000SATS"), Catalog(Coin("1000sats", "1000sats"), Coin("sats", "sats")), null, Now);
        Assert.Equal("1000sats", mapping.CoinGeckoId); Assert.Equal(1, mapping.ContractMultiplier);
    }
    [Fact]
    public void UnknownAndRepeatedSuggestionsPreserveMissingStateAndRevision()
    {
        var catalog = Catalog(Coin()); var m = _engine.Suggest(Instrument(), catalog, null, Now);
        Assert.Same(m, _engine.Suggest(Instrument(), catalog, m, Now.AddMinutes(5)));
        Assert.Equal(MappingStatus.Unknown, _engine.Suggest(Instrument("OTHER"), catalog, null, Now).Status);
    }
    [Fact]
    public void ManualChoiceNeedsExplicitIdentityConfirmationAndKnownCatalogId()
    {
        Assert.Throws<ArgumentException>(() => _engine.Manual(Instrument(), Catalog(Coin()), null, new("test-coin", 0, false), Now));
        Assert.Throws<ArgumentException>(() => _engine.Manual(Instrument(), Catalog(Coin()), null, new("missing", 0, true), Now));
        var m = _engine.Manual(Instrument(), Catalog(Coin()), null, new("test-coin", 0, true), Now);
        Assert.Equal(MappingStatus.ManualVerified, m.Status);
        Assert.Same(m, _engine.Suggest(Instrument(), Catalog(), m, Now.AddMinutes(1)));
    }
    [Fact]
    public void EvmAddressCaseCanMatchButSolanaAddressCaseMustNotChange()
    {
        var coin = Coin() with { Platforms = new Dictionary<string, string> { ["ethereum"] = "0x" + new string('a', 40), ["solana"] = "AbCdSolana" } };
        var evm = _engine.Manual(Instrument(), Catalog(coin), null, new("test-coin", 0, true, "ethereum", "0x" + new string('A', 40)), Now);
        Assert.Equal("ethereum", evm.Platform);
        Assert.Throws<ArgumentException>(() => _engine.Manual(Instrument(), Catalog(coin), null, new("test-coin", 0, true, "solana", "abcdsolana"), Now));
    }
    [Fact]
    public void ChainAndAddressArePairedAndWrongChainIsRejected()
    {
        Assert.Throws<ArgumentException>(() => _engine.Manual(Instrument(), Catalog(Coin()), null, new("test-coin", 0, true, "ethereum", null), Now));
        Assert.Throws<ArgumentException>(() => _engine.Manual(Instrument(), Catalog(Coin()), null, new("test-coin", 0, true, "ethereum", "0x123"), Now));
    }
    [Fact]
    public void StaleManualEditorCannotOverwriteAndClearingIsVersioned()
    {
        var m = _engine.Suggest(Instrument(), Catalog(Coin()), null, Now);
        Assert.Throws<MappingConflictException>(() => _engine.Manual(Instrument(), Catalog(Coin()), m, new("test-coin", 0, true), Now));
        var cleared = _engine.Manual(Instrument(), Catalog(Coin()), m, new(null, 1, false), Now);
        Assert.Equal(2, cleared.Revision); Assert.Null(cleared.CoinGeckoId); Assert.Equal(MappingStatus.Unknown, cleared.Status);
    }
}
