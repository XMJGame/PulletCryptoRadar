using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PulletCryptoRadar.Application;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Infrastructure;

public sealed record SimulationBackupFile(string Path,long Bytes,string Sha256);
public sealed record SimulationTableProof(string Table,long Rows,string Hash);
public sealed record SimulationLedgerProof(long Revision,DateTimeOffset UpdatedAt,string StateHash,string? ControlHash,decimal Cash,int Positions,int Pending,IReadOnlyList<SimulationTableProof> Tables);
public sealed record SimulationBackupManifest(string Version,DateTimeOffset StartedAt,DateTimeOffset FinishedAt,string SourceDirectory,IReadOnlyList<SimulationBackupFile> Files,SimulationLedgerProof Ledger);
public sealed record SimulationRestoreProof(bool RestoredStateEqual,bool Repeatable,bool CommittedCopyEqual,bool DuplicateCommitRejected,bool CashPreserved,bool PositionQuantitiesPreserved,bool NoNewTradesOrFunding,string BeforeHash,string AfterHash,long Revision);

/// <summary>Operator tools only. No host, providers, source ledger writes, or in-place restore.</summary>
public static class SimulationBackup
{
    public const string Version="Simulation-Backup-1";
    private static readonly JsonSerializerOptions Json=new(){WriteIndented=true};
    public static string HashFile(string path){using var file=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(file));}
    public static SqliteConnection ReadOnly(string path)
    {
        var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.GetFullPath(path),Mode=SqliteOpenMode.ReadOnly,Pooling=false,DefaultTimeout=10}.ToString());c.Open();return c;
    }
    private static string Root(string path)=>Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
    private static bool Within(string child,string parent)=>child.Equals(parent,StringComparison.OrdinalIgnoreCase)||child.StartsWith(parent+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
    private static void NewDestination(string source,string destination)
    {
        if(Within(destination,source)||Within(source,destination)||Directory.Exists(destination)||File.Exists(destination))throw new IOException("备份/恢复目标必须是源目录外尚不存在的新目录，禁止覆盖原盘");
        var parent=new DirectoryInfo(Path.GetDirectoryName(destination)!);while(!parent.Exists)parent=parent.Parent??throw new IOException("目标父目录无效");NoLink(parent.FullName);
        Directory.CreateDirectory(destination);
    }
    private static string SafeFile(string root,string relative)
    {
        var path=Path.GetFullPath(Path.Combine(root,relative));if(!Within(path,root)||path==root||Path.IsPathRooted(relative))throw new InvalidDataException("备份清单路径越界");return path;
    }
    private static void NoLink(string path)
    {for(var current=new FileInfo(path) as FileSystemInfo;current is not null;current=current is DirectoryInfo d?d.Parent:(current as FileInfo)?.Directory)if((current.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("备份不跟随目录或文件链接");}
    private static void Copy(string source,string destination)
    {
        NoLink(source);Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        using var output=new FileStream(destination,FileMode.CreateNew,FileAccess.Write,FileShare.None);input.CopyTo(output);output.Flush(true);
    }
    public static SimulationLedgerProof Ledger(string database)
    {
        using var c=ReadOnly(database);using var tx=c.BeginTransaction();
        var bytes=c.QuerySingle<byte[]>("SELECT Payload FROM PaperAccount WHERE Id=1",transaction:tx);var state=StorageRecordCodec.Decode<PaperState>(bytes);
        var tables=new List<SimulationTableProof>();
        foreach(var (table,key) in new[]{("PaperAccount","Id"),("PaperTrade","Id"),("PaperFundingEvent","EventKey"),("PaperEntryReview","Id"),("PaperEquitySample","Revision"),("PaperHoldingSnapshot","PositionId,Time"),("PaperAudit","Revision")})
        {
            using var command=c.CreateCommand();command.Transaction=tx;command.CommandText=$"SELECT Payload FROM {table} ORDER BY {key}";
            using var reader=command.ExecuteReader();using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);long rows=0;var length=new byte[4];
            while(reader.Read()){var payload=(byte[])reader[0];BinaryPrimitives.WriteInt32LittleEndian(length,payload.Length);hash.AppendData(length);hash.AppendData(payload);rows++;}
            tables.Add(new(table,rows,Convert.ToHexString(hash.GetHashAndReset())));
        }
        return new(state.Revision,state.UpdatedAt,QuantRules.HashValue(state),state.QuantControl is null?null:QuantRules.HashValue(state.QuantControl),state.Cash,state.Positions.Count,state.Pending.Count,tables);
    }
    private static void CheckDatabase(string path)
    {
        using var c=ReadOnly(path);var checks=c.Query<string>("PRAGMA quick_check").ToArray();if(checks.Length!=1||checks[0]!="ok")throw new InvalidDataException("SQLite完整性检查失败："+Path.GetFileName(path));
    }
    private static void CopyDatabase(string source,string destination)
    {
        for(var attempt=0;;attempt++)
        {
            try
            {using var read=ReadOnly(source);using var write=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=destination,Pooling=false}.ToString());write.Open();read.BackupDatabase(write);return;}
            catch(SqliteException ex)when((ex.SqliteErrorCode is 5 or 6)&&attempt<5){Thread.Sleep(50*(1<<attempt));}
        }
    }
    public static SimulationBackupManifest Create(string serverDirectory,string destination)
    {
        var source=Root(serverDirectory);destination=Root(destination);var data=Path.Combine(source,"Data");var at=DateTimeOffset.UtcNow;
        if(!File.Exists(Path.Combine(data,"PulletCryptoRadar.db"))||!File.Exists(Path.Combine(source,"appsettings.json")))throw new FileNotFoundException("找不到服务器配置或业务库");
        NoLink(source);NewDestination(source,destination);File.WriteAllText(Path.Combine(destination,"INCOMPLETE"),"Only backup.json marks a completed backup.");
        var target=Path.Combine(destination,"Data");Directory.CreateDirectory(target);
        foreach(var db in Directory.EnumerateFiles(data,"*.db").Order(StringComparer.Ordinal))
        {
            NoLink(db);CopyDatabase(db,Path.Combine(target,Path.GetFileName(db)));
            CheckDatabase(Path.Combine(target,Path.GetFileName(db)));
        }
        Copy(Path.Combine(source,"appsettings.json"),Path.Combine(destination,"appsettings.json"));
        foreach(var file in Directory.EnumerateFiles(data,"*",SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative=Path.GetRelativePath(data,file);var name=Path.GetFileName(file);
            if(name.EndsWith(".db",StringComparison.OrdinalIgnoreCase)||name.EndsWith(".db-wal",StringComparison.OrdinalIgnoreCase)||name.EndsWith(".db-shm",StringComparison.OrdinalIgnoreCase)||name.EndsWith(".lock",StringComparison.OrdinalIgnoreCase)||name.Contains(".tmp",StringComparison.OrdinalIgnoreCase))continue;
            Copy(file,SafeFile(target,relative));
        }
        var ledger=Ledger(Path.Combine(target,"PulletCryptoRadar.db"));
        var files=Directory.EnumerateFiles(destination,"*",SearchOption.AllDirectories).Where(p=>Path.GetFileName(p)!="INCOMPLETE").Order(StringComparer.Ordinal)
            .Select(p=>new SimulationBackupFile(Path.GetRelativePath(destination,p),new FileInfo(p).Length,HashFile(p))).ToArray();
        var manifest=new SimulationBackupManifest(Version,at,DateTimeOffset.UtcNow,source,files,ledger);
        File.WriteAllText(Path.Combine(destination,"backup.json"),JsonSerializer.Serialize(manifest,Json));Verify(destination);File.Delete(Path.Combine(destination,"INCOMPLETE"));return manifest;
    }
    public static SimulationBackupManifest Verify(string directory)
    {
        directory=Root(directory);NoLink(directory);var manifest=JsonSerializer.Deserialize<SimulationBackupManifest>(File.ReadAllText(Path.Combine(directory,"backup.json")))??throw new InvalidDataException("缺备份清单");
        if(manifest.Version!=Version||manifest.Files.Count==0||manifest.Files.Select(f=>f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=manifest.Files.Count)throw new InvalidDataException("备份版本/文件清单无效");
        foreach(var f in manifest.Files)
        {var path=SafeFile(directory,f.Path);NoLink(path);if(new FileInfo(path).Length!=f.Bytes||HashFile(path)!=f.Sha256)throw new InvalidDataException("备份文件摘要不符："+f.Path);if(path.EndsWith(".db",StringComparison.OrdinalIgnoreCase))CheckDatabase(path);}
        if(QuantRules.HashValue(Ledger(Path.Combine(directory,"Data","PulletCryptoRadar.db")))!=QuantRules.HashValue(manifest.Ledger))throw new InvalidDataException("恢复账本与原备份摘要不符");
        var repo=new QuantPortfolioValidationRepository(Options.Create(new StorageOptions{Directory=Path.Combine(directory,"Data")}));repo.Latest();return manifest;
    }
    public static SimulationRestoreProof RestoreTest(string backup,string destination)
    {
        backup=Root(backup);destination=Root(destination);var manifest=Verify(backup);
        if(Within(destination,Root(manifest.SourceDirectory))||Within(Root(manifest.SourceDirectory),destination))throw new IOException("隔离恢复目标不能位于原服务器目录");
        NewDestination(backup,destination);
        foreach(var f in manifest.Files)Copy(SafeFile(backup,f.Path),SafeFile(destination,f.Path));
        Copy(Path.Combine(backup,"backup.json"),Path.Combine(destination,"backup.json"));var restored=Verify(destination);
        var db=new SqliteDatabase(Options.Create(new StorageOptions{Directory=Path.Combine(destination,"Data")}));var repo=new PaperRepository(db);
        var state=repo.Restore()??throw new InvalidDataException("恢复账户缺失");var trades=repo.Trades();var funding=repo.QuantFundingEvents();
        var archived=new Dictionary<string,QuantInputBundle>();var signals=new QuantShadowRepository(Options.Create(new StorageOptions{Directory=Path.Combine(destination,"Data")}));
        foreach(var pending in state.Pending.Where(p=>p.QuantIntent is not null))
        {var hash=pending.QuantIntent!.Decision.InputHash;var bundle=signals.ReadInput(state.QuantControl!.RulesHash,hash);if(bundle is not null)archived.TryAdd(hash,bundle);}
        // Use restored signal archives; missing market input must not invent fills. This is not a market replay.
        var input=new QuantPaperInput(state.UpdatedAt,[],null,new Dictionary<string,QuantFrame>(),new Dictionary<string,ExecutionPlanningInputs>(),new Dictionary<string,FundingRateWindow?>(),archived);
        var batch=QuantPaperEngine.Tick(state,trades,funding,input);var again=QuantPaperEngine.Tick(JsonSerializer.Deserialize<PaperState>(JsonSerializer.Serialize(state))!,trades,funding,input);
        db.SavePaper(batch);var duplicate=false;try{db.SavePaper(batch);}catch(MappingConflictException){duplicate=true;}
        var after=repo.Restore()!;var proof=new SimulationRestoreProof(restored.Ledger.StateHash==QuantRules.HashValue(state),QuantRules.HashValue(batch)==QuantRules.HashValue(again),QuantRules.HashValue(after)==QuantRules.HashValue(batch.State),duplicate,
            state.Cash==after.Cash,state.Positions.Count==after.Positions.Count&&state.Positions.All(p=>after.Positions.Any(q=>p.Id==q.Id&&p.Quantity==q.Quantity)),batch.Trades.Count==0&&batch.FundingEvents.Count==0&&trades.Count==repo.Trades().Count&&funding.Count==repo.QuantFundingEvents().Count,QuantRules.HashValue(state),QuantRules.HashValue(after),after.Revision);
        SqliteConnection.ClearAllPools();File.WriteAllText(Path.Combine(destination,"restore-test.json"),JsonSerializer.Serialize(proof,Json));
        if(!proof.RestoredStateEqual||!proof.Repeatable||!proof.CommittedCopyEqual||!proof.DuplicateCommitRejected||!proof.CashPreserved||!proof.PositionQuantitiesPreserved||!proof.NoNewTradesOrFunding)throw new InvalidDataException("隔离恢复/重复提交验证失败");return proof;
    }
}
