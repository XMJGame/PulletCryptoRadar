using System.Text.Json;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Infrastructure;

public sealed partial class FoundationStore
{
    public void SaveHolderRead(ProviderRead read)
    {
        if(read.Evidence.Provider!="GoPlus")throw new InvalidDataException("Unexpected holder source.");
        lock(writer)
        {
            using var connection=Open();using var transaction=connection.BeginTransaction();using var command=connection.CreateCommand();
            command.Transaction=transaction;command.CommandText="SELECT Payload FROM V2Identity WHERE AssetId=$id";
            command.Parameters.AddWithValue("$id",BinanceIdentity.AssetId(read.Evidence.Symbol).ToString());
            if(command.ExecuteScalar() is not string identity)throw new InvalidDataException("Unknown holder identity.");
            var asset=JsonSerializer.Deserialize<AssetIdentity>(identity)!;
            HolderSnapshot? snapshot=null;
            if(read.Evidence.Failure==CollectionFailure.None)
                snapshot=GoPlusHolderProvider.Parse(asset,read.Evidence.Body??throw new InvalidDataException("Missing holder evidence."),
                    read.Evidence.ReceivedTime,read.Evidence.Id,read.Evidence.Endpoint,read.Evidence.ResponseDate).Snapshot;
            SaveReads(connection,transaction,[read]);
            if(snapshot is not null)
            {
                command.Parameters.Clear();command.CommandText="INSERT INTO HolderSnapshot VALUES($evidence,$asset,$at,$payload)";
                command.Parameters.AddWithValue("$evidence",snapshot.EvidenceId.ToString());command.Parameters.AddWithValue("$asset",snapshot.AssetId.ToString());
                command.Parameters.AddWithValue("$at",snapshot.ReceivedAt.ToUnixTimeMilliseconds());command.Parameters.AddWithValue("$payload",JsonSerializer.Serialize(snapshot));
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }

    public IReadOnlyList<HolderSnapshot> HolderHistory(Guid assetId,int limit=32)
    {
        using var connection=Open();using var command=connection.CreateCommand();
        command.CommandText="SELECT Payload FROM HolderSnapshot WHERE AssetId=$id ORDER BY ReceivedAt DESC LIMIT $limit";
        command.Parameters.AddWithValue("$id",assetId.ToString());command.Parameters.AddWithValue("$limit",Math.Clamp(limit,1,128));
        using var reader=command.ExecuteReader();var rows=new List<HolderSnapshot>();
        while(reader.Read())rows.Add(JsonSerializer.Deserialize<HolderSnapshot>(reader.GetString(0))!);
        return rows;
    }

    public object HolderResearchBoard(DateTimeOffset now)
    {
        var health=SourceHealth(now).Where(row=>row.Provider=="GoPlus").ToArray();
        var roleEvidence=AddressRoleProofs();
        var marketSchedules=ResearchMarketSchedules().ToDictionary(item=>item.AssetId);
        return Assets().Where(asset=>ResearchNetworks.Single(asset) is not null).Select(asset=>
        {
            var history=HolderHistory(asset.AssetId);var latest=history.FirstOrDefault();
            var baseline=latest is null?null:history.FirstOrDefault(row=>latest.ReceivedAt-row.ReceivedAt>=TimeSpan.FromHours(5));
            var source=health.FirstOrDefault(row=>row.Symbol==asset.BinanceSymbol);
            var market=Latest(asset.AssetId);
            var activityMetrics=new[]{Metric.Return24hClosed,Metric.VolumeRatioH1,Metric.TakerBuyShareH1,Metric.OiQuantityChange1h,Metric.OiQuantityChange4h};
            return new {asset.AssetId,Symbol=asset.BinanceSymbol,Snapshot=latest,SampleCount=history.Count,
                Fresh=latest is not null && latest.ReceivedAt<=now && now-latest.ReceivedAt<=TimeSpan.FromHours(8),
                Comparison=latest is null?null:HolderResearch.Compare(latest,baseline),Source=source,
                Roles=latest is null?null:AddressRoles.Analyze(latest,roleEvidence,now),
                RoleChanges=latest is null?[]:AddressRoles.Compare(latest,baseline,roleEvidence,now),
                MarketSchedule=marketSchedules.GetValueOrDefault(asset.AssetId),
                MarketEvidence=activityMetrics.Select(metric=>
                {
                    var saved=market?.Metrics.FirstOrDefault(row=>row.Metric==metric);
                    var observation=saved?.Evidence.Where(row=>row.Provider==saved.Provider&&row.Status==ObservationStatus.Valid)
                        .OrderByDescending(row=>row.SourceTime).FirstOrDefault();
                    var time=observation?.SourceTime;
                    var age=metric is Metric.OiQuantityChange1h or Metric.OiQuantityChange4h?TimeSpan.FromMinutes(10):TimeSpan.FromMinutes(75);
                    var fresh=saved?.UsableForScoring==true&&time is not null&&time<=now&&now-time<=age;
                    return new {Metric=metric,Value=fresh?saved?.Value:null,LastSavedValue=saved?.Value,Fresh=fresh,SourceTime=time,Provider=saved?.Provider};
                }).ToArray(),
                Limitation="接收快照，无原始区块时间。用途分类依据列出的公开来源，并非实际受益人证明；未知部分不等于庄家持仓。所有占比仍以总供应为分母，未重排扣除后的全网前十。前十名单移出不代表卖光，余额变化不代表买卖。Solana 账户归并尚未独立核实，DEX 权限地址不等于某一个池。"};
        }).ToArray();
    }

    public IReadOnlyList<HolderSnapshot> LatestHolderSnapshots()
    {
        using var connection=Open();using var command=connection.CreateCommand();
        command.CommandText="""
            SELECT Payload FROM (
                SELECT Payload,ROW_NUMBER() OVER(PARTITION BY AssetId ORDER BY ReceivedAt DESC,EvidenceId) AS Rank
                FROM HolderSnapshot) WHERE Rank=1
            """;
        using var reader=command.ExecuteReader();var rows=new List<HolderSnapshot>();
        while(reader.Read())rows.Add(JsonSerializer.Deserialize<HolderSnapshot>(reader.GetString(0))!);
        return rows;
    }
}
