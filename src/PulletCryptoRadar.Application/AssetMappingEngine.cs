using System.Text.RegularExpressions;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Application;

public sealed class AssetMappingEngine
{
    public AssetMapping Suggest(Instrument instrument, CoinCatalog catalog, AssetMapping? previous, DateTimeOffset now)
    {
        if (previous?.Status is MappingStatus.ManualVerified or MappingStatus.ContractVerified) return previous;
        var symbol = instrument.BaseAsset.ToLowerInvariant(); var multiplier = 1m;
        var exact = catalog.Coins.Where(c => c.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (exact.Length == 0)
        {
            var match = Regex.Match(symbol, @"^(1000000|100000|10000|1000|100|10)([a-z][a-z0-9]*)$");
            if (match.Success)
            {
                symbol = match.Groups[2].Value;
                multiplier = decimal.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                exact = catalog.Coins.Where(c => c.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase)).ToArray();
            }
        }
        var ids = exact.Select(c => c.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var status = ids.Length == 0 ? MappingStatus.Unknown : ids.Length == 1 ? MappingStatus.SymbolMatched : MappingStatus.Ambiguous;
        var id = ids.Length == 1 ? ids[0] : null;
        var reason = ids.Length == 0 ? "未找到匹配资产，请按 CoinGecko ID 人工核对" : ids.Length == 1
            ? "仅符号匹配，尚未核对身份；市值不参与可信比率" : $"发现 {ids.Length} 个同名候选，禁止按市值或排名自动选择";
        if (previous is not null && previous.CoinGeckoId == id && previous.Status == status && previous.ContractMultiplier == multiplier
            && previous.CandidateIds.SequenceEqual(ids)) return previous;
        return new(instrument.AssetId, id, status, ids, multiplier, null, null, (previous?.Revision ?? 0) + 1, now, reason);
    }
    public AssetMapping Manual(Instrument instrument, CoinCatalog catalog, AssetMapping? previous, ManualMappingRequest request, DateTimeOffset now)
    {
        if (request.ExpectedRevision != (previous?.Revision ?? 0)) throw new MappingConflictException("映射已被其他操作更新，请刷新后重试");
        if (string.IsNullOrWhiteSpace(request.CoinGeckoId))
            return new(instrument.AssetId, null, MappingStatus.Unknown, [], 1, null, null, request.ExpectedRevision + 1, now, "用户清除人工映射；等待重新匹配");
        if (!request.ConfirmIdentity) throw new ArgumentException("请核对资产身份后勾选人工确认");
        var coin = catalog.Coins.SingleOrDefault(c => c.Id == request.CoinGeckoId) ?? throw new ArgumentException("CoinGecko ID 不在已加载目录中，请先搜索核对");
        var platform = string.IsNullOrWhiteSpace(request.Platform) ? null : request.Platform.Trim();
        var contract = string.IsNullOrWhiteSpace(request.ContractAddress) ? null : request.ContractAddress.Trim();
        if ((platform is null) != (contract is null)) throw new ArgumentException("链平台和合约地址必须一起填写或一起留空");
        if (platform is not null && (!coin.Platforms.TryGetValue(platform, out var expected) || !AddressEquals(platform, expected, contract!)))
            throw new ArgumentException("该链和合约地址与所选 CoinGecko 资产目录不一致");
        var suggested = Suggest(instrument, catalog, null, now);
        var multiplier = suggested.CandidateIds.Contains(coin.Id) ? suggested.ContractMultiplier : 1;
        return new(instrument.AssetId, coin.Id, MappingStatus.ManualVerified, [coin.Id], multiplier, platform, contract,
            request.ExpectedRevision + 1, now, string.IsNullOrWhiteSpace(request.Reason) ? "用户核对 CoinGecko ID 后人工确认" : request.Reason.Trim()[..Math.Min(request.Reason.Trim().Length, 500)]);
    }
    private static bool AddressEquals(string platform, string expected, string actual)
        => expected.StartsWith("0x", StringComparison.Ordinal) && actual.StartsWith("0x", StringComparison.Ordinal)
            && expected.Length == 42 && actual.Length == 42 ? expected.Equals(actual, StringComparison.OrdinalIgnoreCase)
            : expected.Equals(actual, StringComparison.Ordinal); // Solana and other case-sensitive chains retain exact addresses.
}
