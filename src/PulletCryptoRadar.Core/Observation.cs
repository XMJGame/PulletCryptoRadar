namespace PulletCryptoRadar.Core;

public sealed record ObservationRun(Guid RunId,DateTimeOffset StartedAt,DateTimeOffset LastHeartbeat,
    DateTimeOffset ContinuousSince,long ObservedSeconds,int Interruptions,bool Stopped);
public sealed record ObservationSample(DateTimeOffset At,bool CashReconciled,bool MarketContextFresh,int OpenPositions,int StalePositions,
    int FailedSources,int ActiveIssues,decimal Balance,decimal Equity,string RuntimeState,bool HttpResponsive = true);
public sealed record ObservationCount(string Name,long Count);
public sealed record ObservationCoverage(int EligibleContracts,int EvaluatedContracts,int FreshEvaluatedContracts,
    int WaitingPatterns,int ConfirmedPatterns);
public sealed record ObservationReport(DateTimeOffset AsOf,ObservationRun? Run,bool HeartbeatFresh,
    long ContinuousSeconds,bool DurationTargetReached,ObservationSample? LatestSample,
    int Samples24h,int ProblemSamples24h,ObservationCoverage Coverage,
    IReadOnlyList<ObservationCount> EvaluationStatuses24h,IReadOnlyList<ObservationCount> Reasons24h,
    IReadOnlyList<ObservationCount> Admissions24h,IReadOnlyList<ObservationCount> LedgerActions24h,
    IReadOnlyList<ObservationRun> RecentRuns);
