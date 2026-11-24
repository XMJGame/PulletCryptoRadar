using System.Text.Json;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Infrastructure;

public sealed partial class FoundationStore
{
    public ProviderRead? SharedFundingInfo()
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Payload FROM V2RawEvidence WHERE Provider='Binance'
            AND json_extract(Payload,'$.Evidence.Endpoint')='/fapi/v1/fundingInfo'
            AND json_extract(Payload,'$.Evidence.Symbol')='*USDT-PERPETUAL*'
            AND json_extract(Payload,'$.Evidence.Failure')=0 ORDER BY ReceivedAt DESC LIMIT 1
            """;
        return command.ExecuteScalar() is string payload ? JsonSerializer.Deserialize<ProviderRead>(payload) : null;
    }
}
