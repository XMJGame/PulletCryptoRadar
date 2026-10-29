using System.Globalization;
using System.Threading.Channels;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Infrastructure;

public sealed class StorageOptions
{
    public string Directory { get; set; } = "Data";
    public int Snapshot5mRetentionDays { get; set; } = 90;
    public string BackupDirectory { get; set; } = "Backup";
    public int BackupRetentionCount { get; set; } = 14;
}

public sealed partial class SqliteDatabase(IOptions<StorageOptions> options)
{
    public string BusinessPath => Path.GetFullPath(Path.Combine(options.Value.Directory, "PulletCryptoRadar.db"));
    public string MarketPath => Path.GetFullPath(Path.Combine(options.Value.Directory, "PulletCryptoRadar.Market.db"));
    public SqliteConnection Open(bool market = false)
    {
        var path = market ? MarketPath : BusinessPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString());
        connection.Open();
        connection.Execute("PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY;");
        return connection;
    }
    public void Initialize()
    {
        using var business = Open();
        business.Execute("PRAGMA journal_mode=WAL;");
        business.Execute("""
            CREATE TABLE IF NOT EXISTS SchemaVersion (Version INTEGER PRIMARY KEY, AppliedAt INTEGER NOT NULL);
            INSERT OR IGNORE INTO SchemaVersion VALUES (1, unixepoch());
            CREATE TABLE IF NOT EXISTS Asset (Id INTEGER PRIMARY KEY AUTOINCREMENT, Symbol TEXT NOT NULL,
                Name TEXT NOT NULL, CoinGeckoId TEXT, MappingStatus INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS ExchangeInstrument (Symbol TEXT PRIMARY KEY, AssetId INTEGER NOT NULL REFERENCES Asset(Id),
                BaseAsset TEXT NOT NULL, OnboardTime INTEGER NOT NULL, Active INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS ProviderState (Name TEXT PRIMARY KEY, Json TEXT NOT NULL, SavedAt INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS Settings (Key TEXT PRIMARY KEY, Json TEXT NOT NULL);
            """);
        InitializeRadar(business);
        InitializeAlerts(business);
        InitializeEvaluation(business);
        business.Execute("INSERT OR IGNORE INTO SchemaVersion VALUES(4, unixepoch());");
        using var market = Open(true);
        market.Execute("PRAGMA journal_mode=WAL;");
        foreach (var table in new[] { "MarketSnapshot5m", "MarketSnapshot1h" })
            market.Execute($"""
                CREATE TABLE IF NOT EXISTS {table} (AssetId INTEGER NOT NULL, Symbol TEXT NOT NULL,
                    Timestamp INTEGER NOT NULL, Open TEXT NOT NULL, High TEXT NOT NULL, Low TEXT NOT NULL, Close TEXT NOT NULL,
                    QuoteVolume TEXT NOT NULL, Volume24hUsd TEXT, FundingRate TEXT,
                    SourceTime INTEGER NOT NULL, ReceivedTime INTEGER NOT NULL, PRIMARY KEY(AssetId, Timestamp));
                CREATE INDEX IF NOT EXISTS IX_{table}_Timestamp ON {table}(Timestamp);
                """);
        InitializeDerivatives(market);
        InitializeMapping(business, market);
        InitializeSpeculative(business, market);
        InitializeWatch(business, market);
        InitializeOnChain(business, market);
        InitializeFlows(business, market);
        InitializePaper(business);
    }
    public IReadOnlyList<Instrument> Synchronize(IReadOnlyList<SymbolDefinition> symbols)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        c.Execute("UPDATE ExchangeInstrument SET Active=0", transaction: tx);
        foreach (var s in symbols)
        {
            var id = c.QuerySingleOrDefault<int?>("SELECT AssetId FROM ExchangeInstrument WHERE Symbol=@Symbol", s, tx);
            if (id is null)
                id = c.QuerySingle<int>("INSERT INTO Asset(Symbol,Name) VALUES(@BaseAsset,@BaseAsset) RETURNING Id", s, tx);
            c.Execute("""
                INSERT INTO ExchangeInstrument(Symbol,AssetId,BaseAsset,OnboardTime,Active) VALUES(@Symbol,@Id,@BaseAsset,@Onboard,1)
                ON CONFLICT(Symbol) DO UPDATE SET Active=1,OnboardTime=excluded.OnboardTime
                """, new { s.Symbol, Id = id.Value, s.BaseAsset, Onboard = s.OnboardTime.ToUnixTimeMilliseconds() }, tx);
        }
        tx.Commit(); return ReadInstruments(c);
    }
    internal static IReadOnlyList<Instrument> ReadInstruments(SqliteConnection c)
        => c.Query("SELECT * FROM ExchangeInstrument").Select(x => new Instrument((int)x.AssetId, (string)x.Symbol,
            (string)x.BaseAsset, DateTimeOffset.FromUnixTimeMilliseconds((long)x.OnboardTime), (long)x.Active == 1)).ToArray();

    public void Save(IReadOnlyList<(int AssetId, Candle Candle, decimal? Volume24h, decimal? Funding)> rows, IReadOnlyList<ProviderHealth> health)
    {
        using (var c = Open(true))
        using (var tx = c.BeginTransaction())
        {
            foreach (var r in rows)
            {
                var table = r.Candle.IntervalMinutes == 60 ? "MarketSnapshot1h" : "MarketSnapshot5m";
                c.Execute($"""
                    INSERT INTO {table} VALUES(@AssetId,@Symbol,@Timestamp,@Open,@High,@Low,@Close,@QuoteVolume,@Volume24h,@Funding,@SourceTime,@ReceivedTime)
                    ON CONFLICT(AssetId,Timestamp) DO UPDATE SET Open=excluded.Open,High=excluded.High,Low=excluded.Low,Close=excluded.Close,
                      QuoteVolume=excluded.QuoteVolume,Volume24hUsd=COALESCE(excluded.Volume24hUsd,{table}.Volume24hUsd),
                      FundingRate=COALESCE(excluded.FundingRate,{table}.FundingRate),
                      SourceTime=excluded.SourceTime,ReceivedTime=excluded.ReceivedTime
                    WHERE excluded.SourceTime >= {table}.SourceTime
                    """, new { r.AssetId, r.Candle.Symbol, Timestamp = r.Candle.OpenTime.ToUnixTimeMilliseconds(),
                    Open = Text(r.Candle.Open), High = Text(r.Candle.High), Low = Text(r.Candle.Low), Close = Text(r.Candle.Close),
                    QuoteVolume = Text(r.Candle.QuoteVolume), Volume24h = Text(r.Volume24h), Funding = Text(r.Funding),
                    SourceTime = r.Candle.SourceTime.ToUnixTimeMilliseconds(), ReceivedTime = r.Candle.ReceivedTime.ToUnixTimeMilliseconds() }, tx);
            }
            tx.Commit();
        }
        using var business = Open(); using var btx = business.BeginTransaction();
        foreach (var h in health) business.Execute("INSERT OR REPLACE INTO ProviderState VALUES(@Name,@Json,@SavedAt)",
            new { h.Name, Json = System.Text.Json.JsonSerializer.Serialize(h), SavedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, btx);
        btx.Commit();
    }
    public void Maintain(DateTimeOffset now)
    {
        using (var c = Open(true)) c.Execute("DELETE FROM MarketSnapshot5m WHERE Timestamp < @Cutoff",
            new { Cutoff = now.AddDays(-options.Value.Snapshot5mRetentionDays).ToUnixTimeMilliseconds() });
        using (var c = Open(true)) c.Execute("DELETE FROM DerivativeSnapshot WHERE SourceTime<@Cutoff; DELETE FROM LiquidationObservation WHERE SourceTime<@LiquidationCutoff;",
            new { Cutoff = now.AddDays(-90).ToUnixTimeMilliseconds(), LiquidationCutoff = now.AddDays(-14).ToUnixTimeMilliseconds() });
        using (var c = Open(true)) c.Execute("DELETE FROM MarketCapSnapshot WHERE SourceTime<@Cutoff", new { Cutoff = now.AddDays(-90).ToUnixTimeMilliseconds() });
        Directory.CreateDirectory(options.Value.BackupDirectory);
        foreach (var market in new[] { false, true })
        {
            var name = market ? "PulletCryptoRadar.Market" : "PulletCryptoRadar";
            var destination = Path.Combine(options.Value.BackupDirectory, $"{name}-{now:yyyyMMdd}.db");
            using var source = Open(market);
            using var backup = new SqliteConnection($"Data Source={destination};Pooling=False");
            backup.Open(); source.BackupDatabase(backup);
            foreach (var old in Directory.GetFiles(options.Value.BackupDirectory, $"{name}-*.db").OrderDescending().Skip(options.Value.BackupRetentionCount)) File.Delete(old);
        }
    }
    private static string? Text(decimal? value) => value?.ToString(CultureInfo.InvariantCulture);
}

public sealed class AssetRepository(SqliteDatabase db) : IAssetRepository
{
    public Task<IReadOnlyList<Instrument>> ReadInstrumentsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); using var c = db.Open(); return Task.FromResult(SqliteDatabase.ReadInstruments(c));
    }
    public Task<IReadOnlyList<Candle>> ReadHourlyHistoryAsync(int assetId, int limit, CancellationToken ct)
        => ReadCandlesAsync(assetId, 60, limit, ct);
    public Task<IReadOnlyList<Candle>> ReadCandlesAsync(int assetId, int interval, int limit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); using var c = db.Open(true);
        var table = interval == 60 ? "MarketSnapshot1h" : "MarketSnapshot5m";
        var bars = c.Query($"SELECT * FROM {table} WHERE AssetId=@assetId ORDER BY Timestamp DESC LIMIT @limit", new { assetId, limit })
            .Select(x => new Candle((string)x.Symbol, interval, DateTimeOffset.FromUnixTimeMilliseconds((long)x.Timestamp),
                decimal.Parse((string)x.Open, CultureInfo.InvariantCulture), decimal.Parse((string)x.High, CultureInfo.InvariantCulture),
                decimal.Parse((string)x.Low, CultureInfo.InvariantCulture), decimal.Parse((string)x.Close, CultureInfo.InvariantCulture),
                decimal.Parse((string)x.QuoteVolume, CultureInfo.InvariantCulture), DateTimeOffset.FromUnixTimeMilliseconds((long)x.SourceTime),
                DateTimeOffset.FromUnixTimeMilliseconds((long)x.ReceivedTime))).Reverse().ToArray();
        return Task.FromResult<IReadOnlyList<Candle>>(bars);
    }
}

public abstract record PersistenceCommand;
public sealed record SaveChainBindingCommand(ChainBinding Binding,long ExpectedRevision,TaskCompletionSource Completion):PersistenceCommand;
public sealed record SaveAddressLabelCommand(AddressLabel Label,long ExpectedRevision,TaskCompletionSource Completion):PersistenceCommand;
public sealed record ReserveOnChainCommand(ChainReservation Reservation,TaskCompletionSource<bool> Completion):PersistenceCommand;
public sealed record SaveOnChainCommand(OnChainWrite Write,TaskCompletionSource Completion):PersistenceCommand;
public sealed record SaveWatchConfigCommand(WatchAsset Config,long ExpectedRevision,TaskCompletionSource Completion):PersistenceCommand;
public sealed record SaveWatchRunCommand(WatchRun Run,TaskCompletionSource Completion):PersistenceCommand;
public sealed record SaveSpeculativeCommand(SpeculativeSnapshot Snapshot, TaskCompletionSource Completion) : PersistenceCommand;
public sealed record SaveVenueBindingCommand(VenueBinding Binding, long ExpectedRevision, TaskCompletionSource Completion) : PersistenceCommand;
public sealed record SyncSymbolsCommand(IReadOnlyList<SymbolDefinition> Symbols, TaskCompletionSource<IReadOnlyList<Instrument>> Completion) : PersistenceCommand;
public sealed record SaveSnapshotsCommand(IReadOnlyList<(int AssetId, Candle Candle, decimal? Volume24h, decimal? Funding)> Rows,
    IReadOnlyList<ProviderHealth> Health, TaskCompletionSource Completion) : PersistenceCommand;
public sealed record MaintenanceCommand(DateTimeOffset Now) : PersistenceCommand;
public sealed record SaveRadarCommand(RadarPersistenceBatch Batch, TaskCompletionSource Completion) : PersistenceCommand;
public sealed record SaveDerivativesCommand(DerivativeWriteBatch Batch, TaskCompletionSource Completion) : PersistenceCommand;
public sealed record SaveEvaluationCommand(EvaluationReport Report, TaskCompletionSource Completion) : PersistenceCommand;
public sealed record SaveCoinCatalogCommand(CoinCatalog Catalog, TaskCompletionSource Completion) : PersistenceCommand;
public sealed record SaveMappingsCommand(IReadOnlyList<MappingChange> Changes, TaskCompletionSource Completion) : PersistenceCommand;
public sealed record SaveMarketCapitalCommand(IReadOnlyList<MarketCapitalSnapshot> Markets, TaskCompletionSource Completion) : PersistenceCommand;
public sealed record SaveAlertRunCommand(AlertRun Run, TaskCompletionSource Completion) : PersistenceCommand;
public sealed record TransitionAlertCommand(Guid Id, NotificationStatus Expected, NotificationStatus Next, string? Error,
    DateTimeOffset Now, TaskCompletionSource<bool> Completion) : PersistenceCommand;
public sealed class PersistenceQueue
{
    public Channel<PersistenceCommand> Channel { get; } = System.Threading.Channels.Channel.CreateBounded<PersistenceCommand>(new BoundedChannelOptions(32) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task<IReadOnlyList<Instrument>> SyncAsync(IReadOnlyList<SymbolDefinition> symbols, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<IReadOnlyList<Instrument>>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Channel.Writer.WriteAsync(new SyncSymbolsCommand(symbols, completion), ct);
        return await completion.Task.WaitAsync(ct);
    }
}

public sealed class PersistenceWorker(SqliteDatabase database, PersistenceQueue queue, IProviderHealthStore health,
    ILogger<PersistenceWorker> logger, IRequestLimiter? limiter = null, TimeProvider? time = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            database.Initialize();
            var now=(time??TimeProvider.System).GetUtcNow();
            if(database.ReadBinanceCooldown() is {} until && until>now)limiter?.Pause("Binance",until-now);
            queue.Ready.TrySetResult(); health.Success("SQLite", freshnessSeconds: 600);
        }
        catch (Exception ex) { queue.Ready.TrySetException(ex); throw; }
        // Shutdown completes the writer, then drains accepted commands before disposing the database connections.
        using var registration = stoppingToken.Register(() => queue.Channel.Writer.TryComplete());
        await foreach (var command in queue.Channel.Reader.ReadAllAsync())
        {
            try
            {
                switch (command)
                {
                    case SavePaperCommand paper: database.SavePaper(paper.Batch); paper.Completion.TrySetResult(); break;
                    case ReserveFlowCommand flowReserve: flowReserve.Completion.TrySetResult(database.ReserveFlow(flowReserve.Attempt,flowReserve.Now,flowReserve.Requests,flowReserve.HourLimit,flowReserve.DayLimit)); break;
                    case RetryFlowCommand flowRetry: flowRetry.Completion.TrySetResult(database.RetryFlowTransport(flowRetry.Chain,flowRetry.Contract,flowRetry.Now)); break;
                    case SaveFlowCommand flowSave: database.SaveFlow(flowSave.Batch,flowSave.Attempt); flowSave.Completion.TrySetResult(); break;
                    case FailFlowCommand flowFail: database.FailFlow(flowFail.Attempt); flowFail.Completion.TrySetResult(); break;
                    case SyncSymbolsCommand sync: sync.Completion.TrySetResult(database.Synchronize(sync.Symbols)); break;
                    case SaveSnapshotsCommand save: database.Save(save.Rows, save.Health); save.Completion.TrySetResult(); break;
                    case MaintenanceCommand maintenance: database.Maintain(maintenance.Now); break;
                    case SaveRadarCommand radar: database.SaveRadar(radar.Batch); radar.Completion.TrySetResult(); break;
                    case SaveDerivativesCommand derivatives: database.SaveDerivatives(derivatives.Batch); derivatives.Completion.TrySetResult(); break;
                    case SaveEvaluationCommand evaluation: database.SaveEvaluation(evaluation.Report); evaluation.Completion.TrySetResult(); break;
                    case SaveCoinCatalogCommand catalog: database.SaveCoinCatalog(catalog.Catalog); catalog.Completion.TrySetResult(); break;
                    case SaveMappingsCommand mappings: database.SaveMappings(mappings.Changes); mappings.Completion.TrySetResult(); break;
                    case SaveMarketCapitalCommand capital: database.SaveMarketCapital(capital.Markets); capital.Completion.TrySetResult(); break;
                    case SaveSpeculativeCommand speculative: database.SaveSpeculative(speculative.Snapshot); speculative.Completion.TrySetResult(); break;
                    case SaveWatchConfigCommand watch: database.SaveWatchConfig(watch.Config,watch.ExpectedRevision); watch.Completion.TrySetResult(); break;
                    case SaveWatchRunCommand watchRun: database.SaveWatchRun(watchRun.Run); watchRun.Completion.TrySetResult(); break;
                    case SaveChainBindingCommand chainBinding: database.SaveChainBinding(chainBinding.Binding,chainBinding.ExpectedRevision); chainBinding.Completion.TrySetResult(); break;
                    case SaveAddressLabelCommand addressLabel: database.SaveAddressLabel(addressLabel.Label,addressLabel.ExpectedRevision); addressLabel.Completion.TrySetResult(); break;
                    case ReserveOnChainCommand reservation: reservation.Completion.TrySetResult(database.ReserveOnChain(reservation.Reservation)); break;
                    case SaveOnChainCommand chainWrite: database.SaveOnChain(chainWrite.Write); chainWrite.Completion.TrySetResult(); break;
                    case SaveVenueBindingCommand binding: database.SaveVenueBinding(binding.Binding,binding.ExpectedRevision); binding.Completion.TrySetResult(); break;
                    case SaveAlertRunCommand alert: database.SaveAlertRun(alert.Run); alert.Completion.TrySetResult(); break;
                    case TransitionAlertCommand transition: transition.Completion.TrySetResult(database.TransitionNotification(transition.Id,
                        transition.Expected, transition.Next, transition.Error, transition.Now)); break;
                }
                health.Success("SQLite", freshnessSeconds: 600);
            }
            catch (Exception ex)
            {
                if (command is SavePaperCommand paper) paper.Completion.TrySetException(ex);
                if (command is ReserveFlowCommand flowReserve) flowReserve.Completion.TrySetException(ex);
                if (command is RetryFlowCommand flowRetry) flowRetry.Completion.TrySetException(ex);
                if (command is SaveFlowCommand flowSave) flowSave.Completion.TrySetException(ex);
                if (command is FailFlowCommand flowFail) flowFail.Completion.TrySetException(ex);
                if (command is SyncSymbolsCommand sync) sync.Completion.TrySetException(ex);
                if (command is SaveSnapshotsCommand save) save.Completion.TrySetException(ex);
                if (command is SaveRadarCommand radar) radar.Completion.TrySetException(ex);
                if (command is SaveDerivativesCommand derivatives) derivatives.Completion.TrySetException(ex);
                if (command is SaveEvaluationCommand evaluation) evaluation.Completion.TrySetException(ex);
                if (command is SaveCoinCatalogCommand catalog) catalog.Completion.TrySetException(ex);
                if (command is SaveMappingsCommand mappings) mappings.Completion.TrySetException(ex);
                if (command is SaveMarketCapitalCommand capital) capital.Completion.TrySetException(ex);
                if (command is SaveSpeculativeCommand speculative) speculative.Completion.TrySetException(ex);
                if (command is SaveWatchConfigCommand watch) watch.Completion.TrySetException(ex);
                if (command is SaveWatchRunCommand watchRun) watchRun.Completion.TrySetException(ex);
                if (command is SaveChainBindingCommand chainBinding) chainBinding.Completion.TrySetException(ex);
                if (command is SaveAddressLabelCommand addressLabel) addressLabel.Completion.TrySetException(ex);
                if (command is ReserveOnChainCommand reservation) reservation.Completion.TrySetException(ex);
                if (command is SaveOnChainCommand chainWrite) chainWrite.Completion.TrySetException(ex);
                if (command is SaveVenueBindingCommand binding) binding.Completion.TrySetException(ex);
                if (command is SaveAlertRunCommand alert) alert.Completion.TrySetException(ex);
                if (command is TransitionAlertCommand transition) transition.Completion.TrySetException(ex);
                health.Failure("SQLite", ex.Message); logger.LogError(ex, "Persistence command {Command} failed", command.GetType().Name);
            }
        }
    }
}
