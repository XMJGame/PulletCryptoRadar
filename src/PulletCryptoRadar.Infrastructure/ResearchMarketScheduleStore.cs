using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Infrastructure;

public sealed partial class FoundationStore
{
    public ResearchMarketWork? ReserveResearchMarket(IReadOnlyList<Guid> assets,DateTimeOffset now,int capacity)
    {
        if(assets.Count>4||assets.Distinct().Count()!=assets.Count||assets.Any(id=>id==Guid.Empty))
            throw new ArgumentException("Research market lane is limited to four reviewed samples.");
        if(capacity<=0||assets.Count==0)return null;
        var registered=Assets().ToDictionary(asset=>asset.AssetId);
        if(assets.Any(id=>!registered.TryGetValue(id,out var asset)||ResearchNetworks.Single(asset) is null||
            asset.BinanceSymbol is "BTCUSDT" or "ETHUSDT"||asset.CoinGeckoId is null&&asset.CoinMarketCapId is null))
            throw new InvalidDataException("Research market collection requires reviewed identities.");
        lock(writer)
        {
            using var connection=Open();using var transaction=connection.BeginTransaction();using var command=connection.CreateCommand();
            command.Transaction=transaction;
            foreach(var asset in assets)
            {
                command.Parameters.Clear();command.CommandText="INSERT OR IGNORE INTO ResearchMarketSchedule VALUES($asset,0,0,0,0,NULL,0,0)";
                command.Parameters.AddWithValue("$asset",asset.ToString());command.ExecuteNonQuery();
            }
            command.Parameters.Clear();command.CommandText="SELECT AssetId,LastAttemptAt,NextOiAt,NextCandlesAt,LeaseUntil FROM ResearchMarketSchedule";
            var rows=new List<(Guid Id,long Attempt,long Oi,long Candles,long Lease)>();
            using(var reader=command.ExecuteReader())while(reader.Read())
                rows.Add((Guid.Parse(reader.GetString(0)),reader.GetInt64(1),reader.GetInt64(2),reader.GetInt64(3),reader.GetInt64(4)));
            var at=now.ToUnixTimeMilliseconds();
            var selected=rows.Where(row=>assets.Contains(row.Id)&&row.Lease<=at&&(row.Oi<=at||row.Candles<=at))
                .OrderBy(row=>Math.Min(row.Oi,row.Candles)).ThenBy(row=>row.Attempt).ThenBy(row=>row.Id).FirstOrDefault();
            if(selected.Id==Guid.Empty){transaction.Commit();return null;}
            var oi=selected.Oi<=at;var candles=selected.Candles<=at&&capacity>(oi?1:0);var lease=Guid.NewGuid();
            command.CommandText="UPDATE ResearchMarketSchedule SET LastAttemptAt=$at,LeaseUntil=$until,LeaseId=$lease WHERE AssetId=$asset";
            command.Parameters.AddWithValue("$at",at);command.Parameters.AddWithValue("$until",now.AddMinutes(2).ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$lease",lease.ToString());command.Parameters.AddWithValue("$asset",selected.Id.ToString());command.ExecuteNonQuery();
            transaction.Commit();return new(lease,selected.Id,oi,candles);
        }
    }

    public void CompleteResearchMarket(ResearchMarketWork work,bool openInterest,bool success,DateTimeOffset now,DateTimeOffset? retryAt=null,DateTimeOffset? sourceTime=null)
    {
        if(openInterest?!work.OpenInterest:!work.Candles)throw new ArgumentException("Capability was not leased.");
        now=now.ToUniversalTime();
        var next=success?(openInterest?now.AddMinutes(5):new DateTimeOffset(now.Year,now.Month,now.Day,now.Hour,0,0,TimeSpan.Zero).AddHours(1).AddMinutes(1)):
            retryAt>now?retryAt.Value:now.AddMinutes(1);
        // Binance history may lag its receipt by a whole bucket. Leave headroom before the 10-minute freshness limit.
        if(success&&openInterest&&sourceTime is {} observed&&observed<=now)
        {
            if(observed.AddMinutes(8)<next)next=observed.AddMinutes(8);
            if(next<now.AddMinutes(1))next=now.AddMinutes(1);
        }
        lock(writer)
        {
            using var connection=Open();using var command=connection.CreateCommand();
            var due=openInterest?"NextOiAt":"NextCandlesAt";var saved=openInterest?"LastOiSuccessAt":"LastCandlesSuccessAt";
            command.CommandText=$"UPDATE ResearchMarketSchedule SET {due}=$next,{saved}=CASE WHEN $success=1 THEN $at ELSE {saved} END WHERE AssetId=$asset AND LeaseId=$lease AND LeaseUntil>=$at";
            command.Parameters.AddWithValue("$asset",work.AssetId.ToString());command.Parameters.AddWithValue("$lease",work.LeaseId.ToString());
            command.Parameters.AddWithValue("$next",next.ToUnixTimeMilliseconds());command.Parameters.AddWithValue("$at",now.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$success",success?1:0);command.ExecuteNonQuery();
        }
    }
    public void ReleaseResearchMarket(ResearchMarketWork work)
    {
        lock(writer)
        {
            using var connection=Open();using var command=connection.CreateCommand();
            command.CommandText="UPDATE ResearchMarketSchedule SET LeaseUntil=0,LeaseId=NULL WHERE AssetId=$asset AND LeaseId=$lease";
            command.Parameters.AddWithValue("$asset",work.AssetId.ToString());command.Parameters.AddWithValue("$lease",work.LeaseId.ToString());command.ExecuteNonQuery();
        }
    }
    public IReadOnlyList<ResearchMarketSchedule> ResearchMarketSchedules()
    {
        using var connection=Open();using var command=connection.CreateCommand();
        command.CommandText="SELECT AssetId,LastAttemptAt,NextOiAt,NextCandlesAt,LeaseUntil,LastOiSuccessAt,LastCandlesSuccessAt FROM ResearchMarketSchedule";
        using var reader=command.ExecuteReader();var rows=new List<ResearchMarketSchedule>();
        DateTimeOffset? Optional(long at)=>at==0?null:DateTimeOffset.FromUnixTimeMilliseconds(at);
        while(reader.Read())rows.Add(new(Guid.Parse(reader.GetString(0)),Optional(reader.GetInt64(1)),
            Optional(reader.GetInt64(2)),Optional(reader.GetInt64(3)),
            Optional(reader.GetInt64(4)),Optional(reader.GetInt64(5)),Optional(reader.GetInt64(6))));
        return rows;
    }
}
