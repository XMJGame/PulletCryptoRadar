namespace PulletCryptoRadar.Core;

public sealed record ResearchIdentitySample(string Symbol,string Name,string CoinGeckoId,ChainToken Token,string ReferenceUrl);
public sealed record ResearchIdentityProof(Guid AssetId,DateTimeOffset VerifiedAt,Guid EvidenceId,string ReferenceUrl,
    AssetIdentity Before,AssetIdentity After);
public static class ResearchNetworks
{
    public static ChainToken? Single(AssetIdentity asset) => asset.Chains.Count == 1 &&
        asset.Chains[0].Chain is ChainId.Bsc or ChainId.Ethereum or ChainId.Solana ? asset.Chains[0] : null;
    public static string Gecko(ChainId chain) => chain switch
    { ChainId.Bsc=>"binance-smart-chain",ChainId.Ethereum=>"ethereum",ChainId.Solana=>"solana",_=>throw new ArgumentException("Unsupported research chain.") };
    public static string Dex(ChainId chain) => chain switch
    { ChainId.Bsc=>"bsc",ChainId.Ethereum=>"ethereum",ChainId.Solana=>"solana",_=>throw new ArgumentException("Unsupported research chain.") };
    public static string Insight(ChainId chain) => chain switch
    { ChainId.Bsc=>"bsc",ChainId.Ethereum=>"eth",ChainId.Solana=>"sol",_=>throw new ArgumentException("Unsupported research chain.") };
    public static bool SameAddress(ChainId chain,string a,string b) => string.Equals(a,b,
        chain == ChainId.Solana ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
}
