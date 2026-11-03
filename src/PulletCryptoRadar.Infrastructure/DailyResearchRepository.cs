using System.Text.Json;
using Microsoft.Extensions.Options;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Infrastructure;
public sealed class DailyResearchRepository(IOptions<StorageOptions> options):IDailyResearchRepository
{
    private readonly object gate=new();private string Root=>Path.GetFullPath(Path.Combine(options.Value.Directory,"DailyResearch"));
    public void Save(DailyResearchReview review)
    {
        lock(gate)
        {
            var folder=Path.Combine(Root,review.Date.ToString("yyyy-MM-dd"));Directory.CreateDirectory(folder);
            var bucket=review.AsOf.ToUnixTimeSeconds()/900;var path=Path.Combine(folder,bucket+".json");if(File.Exists(path))return;
            using(var file=new FileStream(path+".tmp",FileMode.Create,FileAccess.Write,FileShare.None)){JsonSerializer.Serialize(file,review);file.Flush(true);}
            File.Move(path+".tmp",path,false);
        }
    }
    public DailyResearchReview? Latest(DateOnly date)
    {
        lock(gate){var folder=Path.Combine(Root,date.ToString("yyyy-MM-dd"));var file=Directory.Exists(folder)?new DirectoryInfo(folder).EnumerateFiles("*.json").OrderByDescending(f=>f.Name,StringComparer.Ordinal).FirstOrDefault():null;
            return file is not null?JsonSerializer.Deserialize<DailyResearchReview>(File.ReadAllText(file.FullName)):null;}
    }
}
