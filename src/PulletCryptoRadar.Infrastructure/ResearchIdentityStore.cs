using System.Text.Json;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Infrastructure;

public sealed partial class FoundationStore
{
    public void SaveResearchProbe(ProviderRead read)
    {
        lock(writer)
        {
            using var connection=Open();using var transaction=connection.BeginTransaction();
            SaveReads(connection,transaction,[read]);transaction.Commit();
        }
    }
    public IReadOnlyList<ResearchIdentityProof> ResearchProofs()
    {
        using var connection=Open(); using var command=connection.CreateCommand();
        command.CommandText="SELECT Payload FROM ResearchIdentityProof ORDER BY At DESC";
        var proofs=new List<ResearchIdentityProof>(); using var reader=command.ExecuteReader();
        while(reader.Read()) proofs.Add(JsonSerializer.Deserialize<ResearchIdentityProof>(reader.GetString(0))!);
        return proofs;
    }
    public ResearchIdentityProof ApplyResearchIdentity(ResearchIdentitySample sample,ProviderRead read,DateTimeOffset now)
    {
        if(read.Evidence.Provider!="CoinGecko" || read.Evidence.Failure!=CollectionFailure.None ||
            read.Evidence.ReceivedTime>now || now-read.Evidence.ReceivedTime>TimeSpan.FromMinutes(10) ||
            read.Evidence.Symbol!=sample.Symbol || read.Evidence.Body is null ||
            !Uri.TryCreate(sample.ReferenceUrl,UriKind.Absolute,out var reference) || reference.Scheme!="https")
            throw new InvalidDataException("Identity evidence is invalid or expired.");
        using var document=JsonDocument.Parse(read.Evidence.Body); var root=document.RootElement;
        if(root.GetProperty("id").GetString()!=sample.CoinGeckoId ||
            !string.Equals(root.GetProperty("symbol").GetString(),sample.Symbol[..^4],StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Provider ID or symbol mismatch; no ticker-only identity mapping.");
        var platform=ResearchNetworks.Gecko(sample.Token.Chain);
        var address=root.GetProperty("platforms").GetProperty(platform).GetString();
        var detail=root.GetProperty("detail_platforms").GetProperty(platform);
        if(address is null || !ResearchNetworks.SameAddress(sample.Token.Chain,address,sample.Token.Address) ||
            !ResearchNetworks.SameAddress(sample.Token.Chain,detail.GetProperty("contract_address").GetString()!,sample.Token.Address) ||
            detail.GetProperty("decimal_place").GetInt32()!=sample.Token.Decimals)
            throw new InvalidDataException("Chain, contract or decimals do not match the reviewed reference.");
        lock(writer)
        {
            using var connection=Open(); using var transaction=connection.BeginTransaction();
            using var command=connection.CreateCommand(); command.Transaction=transaction;
            command.CommandText="SELECT Payload FROM V2Identity WHERE AssetId=$id";
            var id=BinanceIdentity.AssetId(sample.Symbol); command.Parameters.AddWithValue("$id",id.ToString());
            if(command.ExecuteScalar() is not string previous) throw new InvalidDataException("Register the exact contract instrument first.");
            var before=JsonSerializer.Deserialize<AssetIdentity>(previous)!;
            if(before.BinanceSymbol!=sample.Symbol || before.CoinGeckoId is not null && before.CoinGeckoId!=sample.CoinGeckoId ||
                before.Chains.Any(token=>token.Chain==sample.Token.Chain &&
                    (!ResearchNetworks.SameAddress(token.Chain,token.Address,sample.Token.Address)||token.Decimals!=sample.Token.Decimals)))
                throw new InvalidDataException("Existing identity conflicts; automatic replacement is forbidden.");
            var after=before with { Name=sample.Name,CoinGeckoId=sample.CoinGeckoId,VerifiedAt=now,
                Chains=before.Chains.Any(token=>token.Chain==sample.Token.Chain)?before.Chains:before.Chains.Concat([sample.Token]).ToArray() };
            IdentityValidation.Validate(after);
            var proof=new ResearchIdentityProof(id,now,read.Evidence.Id,sample.ReferenceUrl,before,after);
            SaveReads(connection,transaction,[read]);
            command.CommandText="INSERT OR IGNORE INTO V2Contract VALUES($chain,$address,$id)";
            command.Parameters.AddWithValue("$chain",(int)sample.Token.Chain);
            command.Parameters.AddWithValue("$address",sample.Token.Chain==ChainId.Solana?sample.Token.Address:sample.Token.Address.ToLowerInvariant());
            command.ExecuteNonQuery();
            command.CommandText="SELECT AssetId FROM V2Contract WHERE Chain=$chain AND Address=$address";
            if((string)command.ExecuteScalar()! != id.ToString()) throw new InvalidDataException("Contract is owned by another identity.");
            command.CommandText="UPDATE V2Identity SET Payload=$payload WHERE AssetId=$id";
            command.Parameters.AddWithValue("$payload",JsonSerializer.Serialize(after)); command.ExecuteNonQuery();
            command.Parameters.Clear(); command.CommandText="INSERT INTO ResearchIdentityProof VALUES($evidence,$at,$payload)";
            command.Parameters.AddWithValue("$evidence",read.Evidence.Id.ToString());
            command.Parameters.AddWithValue("$at",now.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$payload",JsonSerializer.Serialize(proof)); command.ExecuteNonQuery();
            transaction.Commit(); return proof;
        }
    }
}
