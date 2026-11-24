using System.Text.Json;
using Microsoft.Data.Sqlite;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Infrastructure;

public sealed partial class FoundationStore : IRequestLedger, IFoundationEvidenceStore, IQuickCheckStore, IEntryStore, IExecutionStore, IPaperPortfolioStore, IPaperMarketStore
{
    private readonly object writer = new();
    private readonly string connectionString;
    public FoundationStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(Path.GetFullPath(dataDirectory), "PulletCryptoRadar.db"),
            Pooling = false
        }.ToString();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=5000; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;";
        command.ExecuteNonQuery();
        return connection;
    }

    public void Initialize(DateTimeOffset now)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS V2Identity (AssetId TEXT PRIMARY KEY, Payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS ResearchIdentityProof(EvidenceId TEXT PRIMARY KEY,At INTEGER NOT NULL,Payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS V2Contract (
                Chain INTEGER NOT NULL, Address TEXT NOT NULL, AssetId TEXT NOT NULL REFERENCES V2Identity(AssetId),
                PRIMARY KEY(Chain, Address));
            CREATE TABLE IF NOT EXISTS V2PaperAccount (Id INTEGER PRIMARY KEY CHECK(Id=1), Payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS V2RawEvidence (
                Id TEXT PRIMARY KEY, Provider TEXT NOT NULL, ReceivedAt INTEGER NOT NULL, Payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS HolderSnapshot (
                EvidenceId TEXT PRIMARY KEY REFERENCES V2RawEvidence(Id), AssetId TEXT NOT NULL REFERENCES V2Identity(AssetId),
                ReceivedAt INTEGER NOT NULL, Payload TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS HolderSnapshotAssetTime ON HolderSnapshot(AssetId,ReceivedAt DESC);
            CREATE TABLE IF NOT EXISTS AddressRoleEvidence(Id TEXT PRIMARY KEY,Payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS ResearchMarketSchedule(AssetId TEXT PRIMARY KEY REFERENCES V2Identity(AssetId),
                LastAttemptAt INTEGER NOT NULL,NextOiAt INTEGER NOT NULL,NextCandlesAt INTEGER NOT NULL,LeaseUntil INTEGER NOT NULL,
                LeaseId TEXT,LastOiSuccessAt INTEGER NOT NULL,LastCandlesSuccessAt INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS V2Feature (
                AssetId TEXT NOT NULL REFERENCES V2Identity(AssetId), AsOf INTEGER NOT NULL,
                Payload TEXT NOT NULL, PRIMARY KEY(AssetId, AsOf));
            CREATE TABLE IF NOT EXISTS V2Request (
                Id TEXT PRIMARY KEY, Provider TEXT NOT NULL, At INTEGER NOT NULL,
                Bytes INTEGER NOT NULL CHECK(Bytes>=0), Completed INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS V2Cooldown (Provider TEXT PRIMARY KEY, Until INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS RequestDenial(Id TEXT PRIMARY KEY,Provider TEXT NOT NULL,At INTEGER NOT NULL,
                Reason TEXT NOT NULL,RetryAt INTEGER,Priority INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS RequestDenialTime ON RequestDenial(At);
            CREATE INDEX IF NOT EXISTS RequestProviderTime ON V2Request(Provider,At);
            CREATE INDEX IF NOT EXISTS RequestTime ON V2Request(At);
            CREATE TABLE IF NOT EXISTS CooldownDetail(Provider TEXT PRIMARY KEY,Until INTEGER NOT NULL,Reason TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS SourceHealthDetail(Provider TEXT NOT NULL,Symbol TEXT NOT NULL,Endpoint TEXT NOT NULL,
                At INTEGER NOT NULL,Reason TEXT,RetryAt INTEGER,Priority INTEGER NOT NULL,PRIMARY KEY(Provider,Symbol,Endpoint));
            CREATE TABLE IF NOT EXISTS V2Universe (Id INTEGER PRIMARY KEY CHECK(Id=1), Payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS V2MarketContext (AsOf INTEGER PRIMARY KEY, Payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS V2Job (Name TEXT PRIMARY KEY, NextAt INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS V2DepthSchedule (Symbol TEXT PRIMARY KEY, AttemptAt INTEGER NOT NULL, NextAt INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS V2Priority (Symbol TEXT PRIMARY KEY, Payload TEXT NOT NULL, SelectedAt INTEGER NOT NULL, ReservedAt INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS V2RadarEpisode (EpisodeId TEXT PRIMARY KEY, AssetId TEXT NOT NULL, UpdatedAt INTEGER NOT NULL, Payload TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS V2RadarAsset ON V2RadarEpisode(AssetId,UpdatedAt);
            CREATE TABLE IF NOT EXISTS V2RadarEvent (EpisodeId TEXT NOT NULL REFERENCES V2RadarEpisode(EpisodeId), At INTEGER NOT NULL, Payload TEXT NOT NULL, PRIMARY KEY(EpisodeId,At));
            CREATE TABLE IF NOT EXISTS V2QuickHistory (AssetId TEXT PRIMARY KEY REFERENCES V2Identity(AssetId), ViewedAt INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS V2QuickJob (AssetId TEXT PRIMARY KEY REFERENCES V2Identity(AssetId), LeaseUntil INTEGER NOT NULL, LastStarted INTEGER NOT NULL, CompletedAt INTEGER);
            CREATE TABLE IF NOT EXISTS V2ClosedCandles (AssetId TEXT PRIMARY KEY REFERENCES V2Identity(AssetId), ReceivedAt INTEGER NOT NULL, Payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS V2EntryEvaluation (AssetId TEXT NOT NULL REFERENCES V2Identity(AssetId), Strategy INTEGER NOT NULL, Side INTEGER NOT NULL, At INTEGER NOT NULL, Payload TEXT NOT NULL, PRIMARY KEY(AssetId,Strategy,Side,At));
            CREATE TABLE IF NOT EXISTS V2ExecutionEvidence (AssetId TEXT NOT NULL REFERENCES V2Identity(AssetId), At INTEGER NOT NULL, Payload TEXT NOT NULL, PRIMARY KEY(AssetId,At));
            CREATE TABLE IF NOT EXISTS V2Sizing (AssetId TEXT NOT NULL REFERENCES V2Identity(AssetId), Side INTEGER NOT NULL, At INTEGER NOT NULL, Payload TEXT NOT NULL, PRIMARY KEY(AssetId,Side,At));
            CREATE TABLE IF NOT EXISTS V2PaperPortfolio (Id INTEGER PRIMARY KEY CHECK(Id=1), Payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS V2PaperCommand (CommandId TEXT PRIMARY KEY, Payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS V2PaperLedger (EventId TEXT PRIMARY KEY, PositionId TEXT NOT NULL, At INTEGER NOT NULL, Payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS V2PaperIssue (IssueKey TEXT PRIMARY KEY,Detail TEXT NOT NULL,At INTEGER NOT NULL,Active INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS V2PaperFundingCursor (AssetId TEXT PRIMARY KEY,AfterAt INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS V2PaperAdmission (SetupId TEXT NOT NULL,EvidenceId TEXT NOT NULL,AssetId TEXT NOT NULL,At INTEGER NOT NULL,Status TEXT NOT NULL,Policy TEXT NOT NULL,PRIMARY KEY(SetupId,EvidenceId));
            CREATE TABLE IF NOT EXISTS V2ProviderHealth (
                Provider TEXT NOT NULL, Symbol TEXT NOT NULL, Endpoint TEXT NOT NULL,
                CheckedAt INTEGER NOT NULL, LastSuccess INTEGER, Failure INTEGER NOT NULL,
                ConsecutiveFailures INTEGER NOT NULL,
                PRIMARY KEY(Provider, Symbol, Endpoint));
            CREATE TABLE IF NOT EXISTS ObservationRun (
                RunId TEXT PRIMARY KEY, StartedAt INTEGER NOT NULL, LastHeartbeat INTEGER NOT NULL,
                ContinuousSince INTEGER NOT NULL, ObservedSeconds INTEGER NOT NULL, Interruptions INTEGER NOT NULL,
                Stopped INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS ObservationSample (
                RunId TEXT NOT NULL REFERENCES ObservationRun(RunId), At INTEGER NOT NULL, Problem INTEGER NOT NULL,
                Payload TEXT NOT NULL, PRIMARY KEY(RunId,At));
            CREATE INDEX IF NOT EXISTS ObservationSampleTime ON ObservationSample(At);
            CREATE INDEX IF NOT EXISTS EntryEvaluationTime ON V2EntryEvaluation(At);
            CREATE INDEX IF NOT EXISTS PaperAdmissionTime ON V2PaperAdmission(At);
            CREATE INDEX IF NOT EXISTS PaperAdmissionAssetTime ON V2PaperAdmission(AssetId,At);
            CREATE INDEX IF NOT EXISTS PaperLedgerTime ON V2PaperLedger(At);
            CREATE INDEX IF NOT EXISTS SharedFundingRead ON V2RawEvidence(Provider,
                json_extract(Payload,'$.Evidence.Endpoint'),json_extract(Payload,'$.Evidence.Symbol'),ReceivedAt DESC);
            """;
        command.ExecuteNonQuery();
        command.CommandText = "INSERT OR IGNORE INTO V2PaperAccount VALUES(1, $payload);";
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(
            new PaperAccount(Guid.NewGuid(), 1000m, 1000m, now)));
        command.ExecuteNonQuery();
        command.Parameters.Clear();
        command.CommandText = "SELECT Payload FROM V2PaperAccount WHERE Id=1";
        var account = JsonSerializer.Deserialize<PaperAccount>((string)command.ExecuteScalar()!)!;
        command.CommandText = "INSERT OR IGNORE INTO V2PaperPortfolio VALUES(1,$payload)";
        command.Parameters.AddWithValue("$payload",JsonSerializer.Serialize(new PaperPortfolio(account.AccountId,
            account.InitialCash,account.Cash,0,now,[])));
        command.ExecuteNonQuery();
    }

    public void Register(AssetIdentity identity)
    {
        IdentityValidation.Validate(identity);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Payload FROM V2Identity WHERE AssetId=$id";
        command.Parameters.AddWithValue("$id", identity.AssetId.ToString());
        var payload = JsonSerializer.Serialize(identity);
        if (command.ExecuteScalar() is string existing)
        {
            if (existing != payload) throw new InvalidOperationException("Identity amendment requires explicit re-verification.");
            return;
        }
        command.CommandText = "INSERT INTO V2Identity VALUES($id, $payload)";
        command.Parameters.AddWithValue("$payload", payload);
        command.ExecuteNonQuery();
        foreach (var token in identity.Chains)
        {
            command.Parameters.Clear();
            command.CommandText = "INSERT INTO V2Contract VALUES($chain, $address, $id)";
            command.Parameters.AddWithValue("$chain", (int)token.Chain);
            command.Parameters.AddWithValue("$address", token.Chain == ChainId.Solana ? token.Address : token.Address.ToLowerInvariant());
            command.Parameters.AddWithValue("$id", identity.AssetId.ToString());
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public IReadOnlyList<AssetIdentity> Assets()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Payload FROM V2Identity ORDER BY AssetId";
        using var reader = command.ExecuteReader();
        var result = new List<AssetIdentity>();
        while (reader.Read())
            result.Add(JsonSerializer.Deserialize<AssetIdentity>(reader.GetString(0)) ?? throw new InvalidDataException());
        return result;
    }

    public PaperAccount Account()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Payload FROM V2PaperAccount WHERE Id=1";
        return JsonSerializer.Deserialize<PaperAccount>((string)command.ExecuteScalar()!) ?? throw new InvalidDataException();
    }
}
