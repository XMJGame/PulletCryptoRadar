namespace PulletCryptoRadar.Core;

public sealed record PaperEquityMark(Guid PositionId,string Symbol,string Cohort,string Direction,decimal Quantity,
    decimal OriginalMargin,decimal RemainingMargin,decimal OpenFee,decimal FundingCost,decimal Mark,
    DateTimeOffset MarkAt,DateTimeOffset QuoteAt,bool Fresh,decimal? Gross,decimal? Adjustment,decimal? Value);
public sealed record PaperContribution(string Cohort,int OpenPositions,int SettlementLegs,decimal RealizedNet,
    decimal? OpenNet,decimal? TotalNet,decimal Fees,decimal Funding);
public sealed record PaperEquityPoint(DateTimeOffset CapturedAt,long Revision,decimal? Equity);
public sealed record PaperEquitySample(string Version,DateTimeOffset CapturedAt,long Revision,decimal InitialCash,decimal Cash,
    decimal OriginalMargin,decimal RemainingMargin,decimal? Gross,decimal? Adjustment,decimal? Equity,
    bool MarksFresh,bool FundingObserved,bool Reconciled,decimal? LedgerExpected,decimal? Difference,
    IReadOnlyList<PaperEquityMark> Positions,IReadOnlyList<PaperContribution> Contributions,IReadOnlyList<string> Issues)
{public PaperEquityPoint Point()=>new(CapturedAt,Revision,Equity);}
public sealed record PaperPerformanceDay(string Date,DateTimeOffset From,DateTimeOffset Through,bool Ended,string Status,
    PaperEquityPoint? Opening,PaperEquityPoint? Closing,decimal? Net,decimal? ReturnPercent,int Samples,int InvalidSamples,
    int GapIntervals,decimal LongestGapSeconds);
public sealed record PaperDrawdown(DateTimeOffset From,DateTimeOffset Through,DateTimeOffset? FirstValidAt,int ValidSamples,
    int InvalidSamples,int GapIntervals,decimal LongestGapSeconds,decimal? PeakEquity,DateTimeOffset? PeakAt,
    decimal? CurrentPercent,decimal? MaximumPercent,DateTimeOffset? MaximumPeakAt,DateTimeOffset? MaximumTroughAt);
public sealed record PaperPerformance(string Version,DateTimeOffset GeneratedAt,DateTimeOffset? RecordingStartedAt,
    PaperEquitySample Current,decimal? CumulativeNet,PaperPerformanceDay Day,PaperDrawdown Drawdown,IReadOnlyList<string> Notices);
