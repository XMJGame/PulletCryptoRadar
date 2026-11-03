using System.Text.Json;
var root=AppContext.BaseDirectory;var logs=Path.Combine(root,"Logs");Directory.CreateDirectory(logs);
File.AppendAllText(Path.Combine(root,"fixture-starts.txt"),Environment.ProcessId+Environment.NewLine);
var clean=File.Exists(Path.Combine(root,"fixture-clean"));
File.AppendAllText(Path.Combine(logs,"runtime-sessions.jsonl"),JsonSerializer.Serialize(new{At=DateTimeOffset.UtcNow,ProcessId=Environment.ProcessId,Phase="Starting"})+Environment.NewLine);
await Task.Delay(500);
if(clean)File.AppendAllText(Path.Combine(logs,"runtime-sessions.jsonl"),JsonSerializer.Serialize(new{At=DateTimeOffset.UtcNow,ProcessId=Environment.ProcessId,Phase="Stopped"})+Environment.NewLine);
return clean?0:17;
