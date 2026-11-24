using System.Text.Json;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Host;

public static class ResearchReports
{
    public static QuantSensitivityReport? Expansion(string directory,bool holdout)
    {
        var path=Path.Combine(directory,"Research",holdout?"quant-expanded-holdout.json":"quant-expanded-development.json");
        if(!File.Exists(path))return null;
        if(new FileInfo(path).Length>10_000_000)throw new InvalidDataException("Expansion report is too large.");
        return JsonSerializer.Deserialize<QuantSensitivityReport>(File.ReadAllText(path),HistoricalResearchCommand.Json);
    }
    public static QuantSensitivityReport? Sensitivity(string directory)
    {
        var path=Path.Combine(directory,"Research","quant-sensitivity-report.json");
        if(!File.Exists(path))return null;
        if(new FileInfo(path).Length>10_000_000)throw new InvalidDataException("Sensitivity report is too large.");
        return JsonSerializer.Deserialize<QuantSensitivityReport>(File.ReadAllText(path),HistoricalResearchCommand.Json);
    }
    public static HistoricalResearchReport? History(string directory)
    {
        var path=Path.Combine(directory,"Research","historical-report.json");
        if(!File.Exists(path))return null;
        if(new FileInfo(path).Length>2_000_000)throw new InvalidDataException("Historical report is too large.");
        return JsonSerializer.Deserialize<HistoricalResearchReport>(File.ReadAllText(path),HistoricalResearchCommand.Json);
    }
    public static LifecycleReport? Lifecycle(string directory)
    {
        var path=Path.Combine(directory,"Research","lifecycle-report.json");
        return File.Exists(path)?JsonSerializer.Deserialize<LifecycleReport>(File.ReadAllText(path),HistoricalResearchCommand.Json):null;
    }
    public static QuantReplayReport? Quant(string directory)
    {
        var path=Path.Combine(directory,"Research","quant-replay-report.json");
        if(!File.Exists(path))return null;
        if(new FileInfo(path).Length>5_000_000)throw new InvalidDataException("Quant replay report is too large.");
        return JsonSerializer.Deserialize<QuantReplayReport>(File.ReadAllText(path),HistoricalResearchCommand.Json);
    }
}
