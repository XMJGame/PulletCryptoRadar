using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.DataFoundation;

public sealed class TokenSearchService(IQuickCheckStore store, TimeProvider clock)
{
    public TokenSearch Search(string query, ChainId? chain = null, bool includeSavedCatalog = false)
    {
        query = query.Trim();
        if (query.Length is < 1 or > 128 || query.Any(char.IsControl))
            throw new ArgumentException("请输入 1～128 个字符的名称、代码或完整合约地址。");
        var now = clock.GetUtcNow();
        var catalog = store.Universe();
        var fresh = catalog?.Universe is not null && catalog.Evidence.ReceivedTime <= now &&
            now-catalog.Evidence.ReceivedTime <= TimeSpan.FromHours(8);
        var instruments = fresh || includeSavedCatalog ? catalog?.Universe ?? [] : [];
        var active = fresh ? instruments.Select(item => item.Symbol).ToHashSet(StringComparer.Ordinal) : [];
        var known = store.Assets();
        var candidates = known.Select(asset => new TokenCandidate(asset.AssetId,asset.Symbol,asset.Name,asset.BinanceSymbol,
            asset.Chains,false,active.Contains(asset.BinanceSymbol))).ToList();
        foreach (var instrument in instruments)
            if (candidates.All(candidate => candidate.BinanceSymbol != instrument.Symbol))
                candidates.Add(new(BinanceIdentity.AssetId(instrument.Symbol),instrument.BaseAsset,instrument.BaseAsset,
                    instrument.Symbol,[],false,fresh));
        var addressQuery = query.StartsWith("0x",StringComparison.OrdinalIgnoreCase) ||
            IdentityValidation.IsAddress(ChainId.Solana,query);
        bool Contract(TokenCandidate candidate) => candidate.Chains.Any(token =>
            (chain is null || token.Chain == chain) && string.Equals(token.Address,query,
                token.Chain == ChainId.Solana ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase));
        bool Exact(TokenCandidate candidate) => addressQuery ? Contract(candidate) :
            string.Equals(candidate.Symbol,query,StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate.Name,query,StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate.BinanceSymbol,query,StringComparison.OrdinalIgnoreCase);
        var allowed = candidates.Where(candidate => chain is null || candidate.Chains.Any(token => token.Chain == chain)).ToArray();
        var exact = allowed.Where(Exact).Select(candidate => candidate with { ExactMatch = true }).ToArray();
        var matches = exact.Length > 0 ? exact : addressQuery ? [] : allowed.Where(candidate =>
            candidate.Symbol.Contains(query,StringComparison.OrdinalIgnoreCase) || candidate.Name.Contains(query,StringComparison.OrdinalIgnoreCase) ||
            candidate.BinanceSymbol.Contains(query,StringComparison.OrdinalIgnoreCase)).OrderBy(candidate => candidate.Symbol,StringComparer.Ordinal).Take(20).ToArray();
        return new(query,exact.Length == 1 ? "Exact" : exact.Length > 1 ? "Ambiguous" : matches.Length > 0 ? "SelectCandidate" :
            fresh ? "NotFoundInVerifiedCatalog" : "CatalogUnavailable",fresh,matches);
    }
}
