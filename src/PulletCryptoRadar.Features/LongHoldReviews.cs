using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Features;

public sealed record ReviewReference(string Title, string Url);

public sealed record LongHoldReview(string Verdict, DateOnly ReviewedOn, string Product, string TokenUtility,
    string Risk, string Recheck, IReadOnlyList<ReviewReference> Sources);

internal static class LongHoldReviews
{
    private static readonly DateOnly ReviewDate = new(2026, 11, 23);

    private static readonly IReadOnlyDictionary<string, LongHoldReview> BscReviews =
        new Dictionary<string, LongHoldReview>(StringComparer.OrdinalIgnoreCase)
        {
            ["0x81a7da4074b8e0ed51bea40f9dcbdf4d9d4832b4"] = new(
                "安全历史需复核", ReviewDate,
                "OlaXBT 有可查阅的 AI 交易研究代码，公开仓库描述了测试网执行。",
                "官方描述了数据和工具访问用途。尚缺独立的生产付费使用证据。",
                "2025 年安全事件后曾换约。团队与投资者分配合计 29%，需核对新合约的实际释放。",
                "核实安全整改、未来 90 天释放及接收钱包去向，并检查现货退出深度。",
                [new("项目代码", "https://github.com/olaxbt/ai-market-maker"),
                 new("官方代币经济", "https://olaxbt-docs.gitbook.io/olaxbt-doc/project-info/tokenomic"),
                 new("Gate 换约公告", "https://www.gate.com/announcements/article/47034")]),
            ["0xE3225e11Cab122F1a126A28997788E5230838ab9"] = new(
                "代币用途待落地", ReviewDate,
                "XnY 有公开的协议文档和部署地址，可追踪数据贡献与版税相关产品。",
                "官方说明当时已部署协议未要求持有或支付 XNY。",
                "官方披露六条归属释放计划最初覆盖 75% 供应，其中五条可由发送方取消。",
                "复核生产协议是否已使用 XNY、释放计划是否变化，以及领取地址有无大额转出。",
                [new("协议部署", "https://docs.xny.ai/concepts/deployments"),
                 new("代币实际用途", "https://docs.xny.ai/concepts/protocol-token/token-utility"),
                 new("锁仓与地址", "https://docs.xny.ai/concepts/protocol-token/token-info")])
        };

    public static LongHoldReview? Find(AssetIdentity asset)
    {
        var matches = asset.Chains.Where(token => token.Chain == ChainId.Bsc)
            .Select(token => BscReviews.GetValueOrDefault(token.Address))
            .OfType<LongHoldReview>().Distinct().ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
}
