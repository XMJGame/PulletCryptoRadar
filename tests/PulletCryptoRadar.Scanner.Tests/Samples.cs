using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Scanner.Tests;

internal static class Samples
{
    public static readonly DateTimeOffset Now = new(2026, 10, 29, 4, 0, 0, TimeSpan.Zero);
    public static FeatureValue<T> Value<T>(T value, DateTimeOffset? time = null) where T : struct
        => new(value, DataStatus.Valid, time ?? Now, "Test");
    public static MarketView Market(decimal r5 = 3, decimal r15 = 5, double z = 3, double volumeZ = 3,
        decimal highDistance = -1, decimal lowDistance = 10, int id = 1, string symbol = "TESTUSDT")
        => new(new(id, symbol, "TEST", Now.AddDays(-30), true), 105, 10000000, null, null, null, Now, Now, null, 2880, 2016, 2880,
            new AssetFeatures { Returns = new() { [1] = Value(0m), [5] = Value(r5), [15] = Value(r15), [60] = Value(r15), [240] = Value(r15) },
                PriceRobustZ5m = Value(z), VolumeRobustZ = Value(volumeZ),
                DistanceToHigh = new() { [60] = Value(highDistance) }, DistanceToLow = new() { [60] = Value(lowDistance) } });
    public static MoveAssessment Assessment(MoveDirection direction = MoveDirection.Up, string stage = "Ignition", double? score = 82,
        bool candidate = true, DataStatus status = DataStatus.Valid)
        => new(direction, direction == MoveDirection.Up ? UpMoveDetector.Version : DownMoveDetector.Version, score, 60, stage, status, candidate,
            [], ["Price", "Volume", "Structure"], ["OpenInterest"], ["test evidence"]);
    public static RadarObservation Observation(MoveAssessment? up = null, MoveAssessment? down = null, decimal price = 105)
        => new(Market() with { Price = price }, up ?? Assessment(), down ?? Assessment(MoveDirection.Down, "Normal", 0, false));
}
