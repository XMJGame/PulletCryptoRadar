using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Scanner;

public static class SignalGrader
{
    public static int FamilyCount(MoveAssessment a) => a.ConfirmedFamilies.Select(x => x == "OI" ? "OpenInterest" : x)
        .Where(x => x is "Price" or "Volume" or "OpenInterest" or "Flow" or "Trader" or "Structure" or "Liquidation")
        .Distinct(StringComparer.Ordinal).Count();
    public static SignalGrade Grade(MoveAssessment a)
    {
        if (a.Score is not { } score || !double.IsFinite(score) || score is < 0 or > 100 || a.Status != DataStatus.Valid) return SignalGrade.Unknown;
        var families = FamilyCount(a);
        if (score >= 92 && a.DataConfidence >= 80 && families >= 4 && a.Risk == SignalRisk.Low) return SignalGrade.S;
        if (score >= (a.Direction == MoveDirection.Up ? 82 : 75) && a.DataConfidence >= 70 && families >= 3) return SignalGrade.A;
        return score >= 70 ? SignalGrade.B : SignalGrade.C;
    }
}
