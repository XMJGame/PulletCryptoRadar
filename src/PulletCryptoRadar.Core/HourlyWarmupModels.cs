namespace PulletCryptoRadar.Core;

public sealed record HourlyWarmupRecord(string Version,string Symbol,DateTimeOffset Hour,DateTimeOffset StartedAt,DateTimeOffset UpdatedAt,
    DateTimeOffset NextAttemptAt,string Status,string Phase,int MissingHours,int RejectedRows,int FetchLimit,int ReturnedRows,int AcceptedRows,string Reason);
public sealed record HourlyWarmupStatus(DateTimeOffset AsOf,DateTimeOffset Hour,IReadOnlyList<HourlyWarmupRecord> Records,string Meaning);
public interface IHourlyWarmupRepository
{IReadOnlyList<HourlyWarmupRecord> Read();void Save(HourlyWarmupRecord record);}
