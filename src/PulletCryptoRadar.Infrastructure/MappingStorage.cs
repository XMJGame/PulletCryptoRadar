using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Infrastructure;

public sealed partial class SqliteDatabase
{
    private static void InitializeMapping(SqliteConnection business, SqliteConnection market)
    {
        business.Execute("""
            CREATE TABLE IF NOT EXISTS CoinCatalog (Id INTEGER PRIMARY KEY CHECK(Id=1), ReceivedTime INTEGER NOT NULL, Payload BLOB NOT NULL);
            CREATE TABLE IF NOT EXISTS AssetMapping (AssetId INTEGER PRIMARY KEY REFERENCES Asset(Id), Revision INTEGER NOT NULL, Payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS AssetMappingAudit (Id INTEGER PRIMARY KEY AUTOINCREMENT, AssetId INTEGER NOT NULL REFERENCES Asset(Id),
                Revision INTEGER NOT NULL, UpdatedAt INTEGER NOT NULL, Payload TEXT NOT NULL, UNIQUE(AssetId,Revision));
            CREATE TABLE IF NOT EXISTS ChainToken (AssetId INTEGER NOT NULL REFERENCES Asset(Id), Platform TEXT NOT NULL, ContractAddress TEXT NOT NULL,
                IsPrimary INTEGER NOT NULL DEFAULT 1, PRIMARY KEY(AssetId,Platform));
            """);
        market.Execute("""
            CREATE TABLE IF NOT EXISTS MarketCapSnapshot (CoinGeckoId TEXT NOT NULL, SourceTime INTEGER NOT NULL,
                ReceivedTime INTEGER NOT NULL, Payload TEXT NOT NULL, PRIMARY KEY(CoinGeckoId,SourceTime));
            CREATE INDEX IF NOT EXISTS IX_MarketCapSnapshot_Time ON MarketCapSnapshot(SourceTime);
            """);
        business.Execute("INSERT OR IGNORE INTO SchemaVersion VALUES(6,unixepoch());");
    }
    public void SaveCoinCatalog(CoinCatalog catalog)
    {
        if (catalog.Coins.Count == 0) throw new InvalidDataException("拒绝用空 CoinGecko 目录覆盖已保存目录");
        using var c = Open();
        c.Execute("""
            INSERT INTO CoinCatalog VALUES(1,@Time,@Payload) ON CONFLICT(Id) DO UPDATE SET ReceivedTime=excluded.ReceivedTime,Payload=excluded.Payload
            WHERE excluded.ReceivedTime>=CoinCatalog.ReceivedTime
            """, new { Time = catalog.ReceivedTime.ToUnixTimeMilliseconds(), Payload = Compress(catalog) });
    }
    public void SaveMappings(IReadOnlyList<MappingChange> changes)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        foreach (var change in changes)
        {
            var m = change.Mapping;
            var revision = c.QuerySingleOrDefault<long?>("SELECT Revision FROM AssetMapping WHERE AssetId=@AssetId", m, tx) ?? 0;
            if (revision != change.ExpectedRevision || m.Revision != change.ExpectedRevision + 1)
                throw new MappingConflictException("映射版本冲突，已取消整批写入，请刷新重试");
            var payload = JsonSerializer.Serialize(m);
            c.Execute("INSERT INTO AssetMapping VALUES(@AssetId,@Revision,@Payload) ON CONFLICT(AssetId) DO UPDATE SET Revision=excluded.Revision,Payload=excluded.Payload",
                new { m.AssetId, m.Revision, Payload = payload }, tx);
            if (c.Execute("UPDATE Asset SET CoinGeckoId=@CoinGeckoId,MappingStatus=@Status WHERE Id=@AssetId", new { m.AssetId, m.CoinGeckoId, Status = (int)m.Status }, tx) != 1)
                throw new InvalidDataException("资产不存在");
            c.Execute("INSERT INTO AssetMappingAudit(AssetId,Revision,UpdatedAt,Payload) VALUES(@AssetId,@Revision,@Time,@Payload)",
                new { m.AssetId, m.Revision, Time = m.UpdatedAt.ToUnixTimeMilliseconds(), Payload = payload }, tx);
            c.Execute("DELETE FROM ChainToken WHERE AssetId=@AssetId", m, tx);
            if (m.Platform is not null && m.ContractAddress is not null && m.Status is MappingStatus.ManualVerified or MappingStatus.ContractVerified)
                c.Execute("INSERT INTO ChainToken VALUES(@AssetId,@Platform,@ContractAddress,1)", m, tx);
        }
        tx.Commit();
    }
    public void SaveMarketCapital(IReadOnlyList<MarketCapitalSnapshot> markets)
    {
        using var c = Open(true); using var tx = c.BeginTransaction();
        foreach (var m in markets)
            c.Execute("""
                INSERT INTO MarketCapSnapshot VALUES(@Id,@Source,@Received,@Payload)
                ON CONFLICT(CoinGeckoId,SourceTime) DO UPDATE SET ReceivedTime=excluded.ReceivedTime,Payload=excluded.Payload
                WHERE excluded.ReceivedTime>=MarketCapSnapshot.ReceivedTime
                """, new { Id = m.CoinGeckoId, Source = m.SourceTime.ToUnixTimeMilliseconds(), Received = m.ReceivedTime.ToUnixTimeMilliseconds(), Payload = JsonSerializer.Serialize(m) }, tx);
        tx.Commit();
    }
}
public sealed class MappingRepository(SqliteDatabase db) : IMappingRepository
{
    public MappingState ReadState()
    {
        using var c = db.Open();
        var bytes = c.QuerySingleOrDefault<byte[]>("SELECT Payload FROM CoinCatalog WHERE Id=1");
        var catalog = bytes is null ? null : SqliteDatabase.Decompress<CoinCatalog>(bytes);
        var mappings = c.Query<string>("SELECT Payload FROM AssetMapping").Select(p => JsonSerializer.Deserialize<AssetMapping>(p)!).ToArray();
        using var market = db.Open(true);
        var markets = market.Query<string>("""
            SELECT s.Payload FROM MarketCapSnapshot s JOIN (SELECT CoinGeckoId,MAX(SourceTime) AS Time FROM MarketCapSnapshot GROUP BY CoinGeckoId) latest
            ON s.CoinGeckoId=latest.CoinGeckoId AND s.SourceTime=latest.Time
            """).Select(p => JsonSerializer.Deserialize<MarketCapitalSnapshot>(p)!).ToArray();
        return new(catalog, mappings, markets);
    }
    public IReadOnlyList<AssetMapping> ReadHistory(int assetId)
    {
        using var c = db.Open(); return c.Query<string>("SELECT Payload FROM AssetMappingAudit WHERE AssetId=@assetId ORDER BY Revision DESC LIMIT 50", new { assetId })
            .Select(p => JsonSerializer.Deserialize<AssetMapping>(p)!).ToArray();
    }
}
public sealed class MappingPersistence(PersistenceQueue queue) : IMappingPersistence
{
    public Task SaveCatalogAsync(CoinCatalog catalog, CancellationToken ct) => Save(completion => new SaveCoinCatalogCommand(catalog, completion), ct);
    public Task SaveMappingsAsync(IReadOnlyList<MappingChange> changes, CancellationToken ct) => Save(completion => new SaveMappingsCommand(changes, completion), ct);
    public Task SaveMarketsAsync(IReadOnlyList<MarketCapitalSnapshot> markets, CancellationToken ct) => Save(completion => new SaveMarketCapitalCommand(markets, completion), ct);
    private async Task Save(Func<TaskCompletionSource, PersistenceCommand> command, CancellationToken ct)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await queue.Channel.Writer.WriteAsync(command(completion), ct); await completion.Task.WaitAsync(ct);
    }
}
