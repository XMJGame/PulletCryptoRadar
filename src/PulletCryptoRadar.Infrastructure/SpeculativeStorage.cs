using System.Collections.Concurrent;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Infrastructure;

public sealed class AlternativeMarketStore:IAlternativeMarketStore
{
    private readonly ConcurrentDictionary<(string,string),VenueQuote> _quotes=new();
    public void Set(IReadOnlyList<VenueQuote> quotes){foreach(var q in quotes)_quotes.AddOrUpdate((q.Exchange,q.Symbol),q,(_,old)=>q.SourceTime>=old.SourceTime?q:old);}
    public IReadOnlyList<VenueQuote> GetAll()=>_quotes.Values.ToArray();
}
public sealed partial class SqliteDatabase
{
    public DateTimeOffset? ReadBinanceCooldown()
    {
        using var c=Open();var json=c.QuerySingleOrDefault<string>("SELECT Json FROM ProviderState WHERE Name='Binance Cooldown'");
        if(json is null)return null;
        var health=JsonSerializer.Deserialize<ProviderHealth>(json);
        if(health?.Error is null||health.LastSuccess>=health.LastFailure)return null;
        var match=System.Text.RegularExpressions.Regex.Match(health.Error,@"UTC\s+(\d{4}-\d{2}-\d{2}T[0-9:.+Z-]+)");
        return match.Success&&DateTimeOffset.TryParse(match.Groups[1].Value,System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.AssumeUniversal,out var until)?until:null;
    }
    private static void InitializeSpeculative(SqliteConnection business,SqliteConnection market)
    {
        business.Execute("""
            CREATE TABLE IF NOT EXISTS VenueBinding(AssetId INTEGER NOT NULL REFERENCES Asset(Id),Exchange TEXT NOT NULL,Symbol TEXT,
                Revision INTEGER NOT NULL,Payload TEXT NOT NULL,PRIMARY KEY(AssetId,Exchange),UNIQUE(Exchange,Symbol));
            CREATE TABLE IF NOT EXISTS VenueBindingAudit(AssetId INTEGER NOT NULL,Exchange TEXT NOT NULL,Revision INTEGER NOT NULL,Payload TEXT NOT NULL,
                PRIMARY KEY(AssetId,Exchange,Revision));
            INSERT OR IGNORE INTO SchemaVersion VALUES(7,unixepoch());
            """);
        market.Execute("CREATE TABLE IF NOT EXISTS SpeculativeScan(Id TEXT PRIMARY KEY,AsOf INTEGER NOT NULL,Payload BLOB NOT NULL); CREATE INDEX IF NOT EXISTS IX_SpeculativeScan_Time ON SpeculativeScan(AsOf);");
    }
    public void SaveSpeculative(SpeculativeSnapshot snapshot)
    {
        using var c=Open(true);using var tx=c.BeginTransaction();
        c.Execute("INSERT OR IGNORE INTO SpeculativeScan VALUES(@Id,@Time,@Payload)",new{Id=snapshot.Id.ToString(),Time=snapshot.AsOf.ToUnixTimeMilliseconds(),Payload=Compress(snapshot)},tx);
        c.Execute("DELETE FROM SpeculativeScan WHERE AsOf<@Time",new{Time=snapshot.AsOf.AddDays(-7).ToUnixTimeMilliseconds()},tx);tx.Commit();
    }
    public void SaveVenueBinding(VenueBinding binding,long expectedRevision)
    {
        using var c=Open();using var tx=c.BeginTransaction();
        var revision=c.QuerySingleOrDefault<long?>("SELECT Revision FROM VenueBinding WHERE AssetId=@AssetId AND Exchange=@Exchange",binding,tx)??0;
        if(revision!=expectedRevision||binding.Revision!=revision+1)throw new MappingConflictException("跨交易所映射版本冲突，请刷新");
        if(c.QuerySingle<int>("SELECT COUNT(*) FROM VenueBinding WHERE Exchange=@Exchange AND Symbol=@Symbol AND AssetId<>@AssetId",binding,tx)>0)
            throw new MappingConflictException("该原生合约已绑定其他本地资产，禁止重复使用市值");
        var payload=JsonSerializer.Serialize(binding);
        c.Execute("INSERT INTO VenueBinding VALUES(@AssetId,@Exchange,@Symbol,@Revision,@Payload) ON CONFLICT(AssetId,Exchange) DO UPDATE SET Symbol=excluded.Symbol,Revision=excluded.Revision,Payload=excluded.Payload",
            new{binding.AssetId,binding.Exchange,binding.Symbol,binding.Revision,Payload=payload},tx);
        c.Execute("INSERT INTO VenueBindingAudit VALUES(@AssetId,@Exchange,@Revision,@Payload)",new{binding.AssetId,binding.Exchange,binding.Revision,Payload=payload},tx);tx.Commit();
    }
}
public sealed class SpeculativeRepository(SqliteDatabase db):ISpeculativeRepository
{
    public IReadOnlyList<VenueBinding> Bindings(){using var c=db.Open();return c.Query<string>("SELECT Payload FROM VenueBinding").Select(x=>JsonSerializer.Deserialize<VenueBinding>(x)!).ToArray();}
    public IReadOnlyList<SpeculativeSnapshot> History(int limit){using var c=db.Open(true);return c.Query<byte[]>("SELECT Payload FROM SpeculativeScan ORDER BY AsOf DESC LIMIT @Limit",new{Limit=Math.Clamp(limit,1,12)}).Select(SqliteDatabase.Decompress<SpeculativeSnapshot>).ToArray();}
}
public sealed class SpeculativePersistence(PersistenceQueue queue):ISpeculativePersistence
{
    public async Task SaveAsync(SpeculativeSnapshot snapshot,CancellationToken ct){var ack=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);await queue.Channel.Writer.WriteAsync(new SaveSpeculativeCommand(snapshot,ack),ct);await ack.Task.WaitAsync(ct);}
    public async Task BindAsync(VenueBinding binding,long expectedRevision,CancellationToken ct){var ack=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);await queue.Channel.Writer.WriteAsync(new SaveVenueBindingCommand(binding,expectedRevision,ack),ct);await ack.Task.WaitAsync(ct);}
}
