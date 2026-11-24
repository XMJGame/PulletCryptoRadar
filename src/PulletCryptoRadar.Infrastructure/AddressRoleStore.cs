using System.Text.Json;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Infrastructure;

public sealed partial class FoundationStore
{
    public void ImportReviewedAddressEvidence(IReadOnlyList<AddressRoleEvidence> proofs)
    {
        foreach(var proof in proofs)
            if(string.IsNullOrWhiteSpace(proof.Id)||!Enum.IsDefined(proof.Role)||proof.Role==AddressRole.Unknown||
                !Enum.IsDefined(proof.SourceKind)||!Enum.IsDefined(proof.Chain)||!IdentityValidation.IsAddress(proof.Chain,proof.Address)||
                proof.TokenContract is {} contract&&!IdentityValidation.IsAddress(proof.Chain,contract)||
                !Uri.TryCreate(proof.SourceUrl,UriKind.Absolute,out var url)||url.Scheme!="https"||
                string.IsNullOrWhiteSpace(proof.Statement)||string.IsNullOrWhiteSpace(proof.Entity)||
                proof.ReviewedAt==default||proof.ExpiresAt<=proof.ReviewedAt||proof.ExpiresAt-proof.ReviewedAt>TimeSpan.FromDays(30)||
                proof.SourcePublishedAt>proof.ReviewedAt)
                throw new InvalidDataException("Invalid reviewed address evidence.");
        lock(writer)
        {
            using var connection=Open();using var transaction=connection.BeginTransaction();using var command=connection.CreateCommand();
            command.Transaction=transaction;
            foreach(var proof in proofs)
            {
                command.Parameters.Clear();command.CommandText="SELECT Payload FROM AddressRoleEvidence WHERE Id=$id";
                command.Parameters.AddWithValue("$id",proof.Id);var payload=JsonSerializer.Serialize(proof);
                if(command.ExecuteScalar() is string existing)
                {
                    if(existing!=payload)throw new InvalidOperationException("Reviewed evidence is immutable; append a new review ID.");
                    continue;
                }
                command.CommandText="INSERT INTO AddressRoleEvidence VALUES($id,$payload)";command.Parameters.AddWithValue("$payload",payload);
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }
    public IReadOnlyList<AddressRoleEvidence> AddressRoleProofs()
    {
        using var connection=Open();using var command=connection.CreateCommand();command.CommandText="SELECT Payload FROM AddressRoleEvidence ORDER BY Id";
        using var reader=command.ExecuteReader();var rows=new List<AddressRoleEvidence>();
        while(reader.Read())rows.Add(JsonSerializer.Deserialize<AddressRoleEvidence>(reader.GetString(0))!);
        return rows;
    }
}
