using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Infrastructure;

// Reviewed public sources, not labels inferred from size, transaction proximity or provider tags.
// Keep historical disclosure separate from the currently reviewed explorer label.
public static class ReviewedAddressCatalog
{
    private static readonly DateTimeOffset Reviewed=new(2026,11,4,7,9,0,TimeSpan.Zero);
    private static AddressRoleEvidence Proof(string id,ChainId chain,string address,AddressRole role,string entity,
        string url,AddressSourceKind kind,string statement,DateTimeOffset? published=null)=>
        new(id,chain,address,null,role,entity,url,kind,statement,Reviewed,Reviewed.AddDays(30),published);
    public static IReadOnlyList<AddressRoleEvidence> Items {get;} =
    [
        Proof("eth-binance-20-explorer-20261104",ChainId.Ethereum,"0xf977814e90da44bfa03b6295a0616a897441acec",AddressRole.Exchange,"Binance",
            "https://etherscan.io/address/0xf977814e90da44bfa03b6295a0616a897441acec",AddressSourceKind.ExplorerLabel,
            "Binance: Cold Wallet 20（本机读取页面标题）"),
        Proof("eth-binance-28-explorer-20261104",ChainId.Ethereum,"0x5a52e96bacdabb82fd05763e25335261b270efcb",AddressRole.Exchange,"Binance",
            "https://etherscan.io/address/0x5a52e96bacdabb82fd05763e25335261b270efcb",AddressSourceKind.ExplorerLabel,
            "Binance: Cold Wallet 28（本机读取页面标题）"),
        Proof("eth-binance-20-disclosure-20261104",ChainId.Ethereum,"0xf977814e90da44bfa03b6295a0616a897441acec",AddressRole.Exchange,"Binance",
            "https://www.binance.com/en/square/post/97671",AddressSourceKind.HistoricalIssuerDisclosure,
            "Binance 官方历史钱包披露列有此 ETH 地址；历史快照 2022-11-10，不单独证明当前用途",new(2022,11,10,0,0,0,TimeSpan.Zero)),
        Proof("eth-binance-28-disclosure-20261104",ChainId.Ethereum,"0x5a52e96bacdabb82fd05763e25335261b270efcb",AddressRole.Exchange,"Binance",
            "https://www.binance.com/en/square/post/97671",AddressSourceKind.HistoricalIssuerDisclosure,
            "Binance 官方历史钱包披露列有此 ETH 地址；历史快照 2022-11-10，不单独证明当前用途",new(2022,11,10,0,0,0,TimeSpan.Zero)),
        Proof("sol-binance-3-explorer-20261104",ChainId.Solana,"9WzDXwBbmkg8ZTbNMqUxvQRAyrZzDsGYdLVL9zYtAWWM",AddressRole.Exchange,"Binance",
            "https://solscan.io/account/9WzDXwBbmkg8ZTbNMqUxvQRAyrZzDsGYdLVL9zYtAWWM",AddressSourceKind.ExplorerLabel,
            "Solscan Public name: Binance 3；Tags: Binance Exchange / Exchange Wallet"),
        Proof("sol-raydium-authority-explorer-20261104",ChainId.Solana,"5Q544fKrFoe6tsEbD7S8EmxGTJYAKtTVhAW5Q5pge4j1",AddressRole.DexAuthority,"Raydium",
            "https://solscan.io/account/5Q544fKrFoe6tsEbD7S8EmxGTJYAKtTVhAW5Q5pge4j1",AddressSourceKind.ExplorerLabel,
            "Solscan Public name: Raydium Authority V4；Tags: Raydium / Dex Wallet"),
        Proof("sol-raydium-authority-protocol-20261104",ChainId.Solana,"5Q544fKrFoe6tsEbD7S8EmxGTJYAKtTVhAW5Q5pge4j1",AddressRole.DexAuthority,"Raydium",
            "https://github.com/raydium-io/raydium-ui/blob/master/src/utils/pools.ts",AddressSourceKind.ProtocolReference,
            "Raydium 官方已归档 UI 源码将此地址列为 ammAuthority；以当前 Solscan 标签交叉核对，不认定为某个具体池")
    ];
}
