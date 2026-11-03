using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Hosting;

namespace PulletCryptoRadar.Infrastructure;
public sealed class RuntimeSessionJournal:IDisposable
{
    private readonly object gate=new();private readonly string path;private readonly UnhandledExceptionEventHandler unhandled;
    private readonly string sessionId=Guid.NewGuid().ToString("N");private readonly Timer heartbeat;
    private readonly List<CancellationTokenRegistration> registrations=[];
    public RuntimeSessionJournal(string directory,IHostApplicationLifetime lifetime)
    {
        Directory.CreateDirectory(directory);path=Path.Combine(directory,"runtime-sessions.jsonl");
        heartbeat=new Timer(_=>Write("Heartbeat"),null,Timeout.InfiniteTimeSpan,Timeout.InfiniteTimeSpan);
        unhandled=(_,e)=>Write("UnhandledException",e.ExceptionObject as Exception);
        AppDomain.CurrentDomain.UnhandledException+=unhandled;
        registrations.Add(lifetime.ApplicationStarted.Register(()=>{Write("Started");heartbeat.Change(TimeSpan.FromSeconds(30),TimeSpan.FromSeconds(30));}));
        registrations.Add(lifetime.ApplicationStopping.Register(()=>{heartbeat.Change(Timeout.InfiniteTimeSpan,Timeout.InfiniteTimeSpan);Write("Stopping");}));
        registrations.Add(lifetime.ApplicationStopped.Register(()=>Write("Stopped")));
        Write("Starting");
    }
    private void Write(string phase,Exception? error=null)
    {
        try
        {
            lock(gate)
            {
                var stack=error?.StackTrace;var text=JsonSerializer.Serialize(new{At=DateTimeOffset.UtcNow,ProcessId=Environment.ProcessId,SessionId=sessionId,Phase=phase,
                    Runtime=RuntimeInformation.FrameworkDescription,ErrorType=error?.GetType().FullName,Stack=stack?[..Math.Min(stack.Length,8000)]});
                using var file=new FileStream(path,FileMode.Append,FileAccess.Write,FileShare.ReadWrite,4096,FileOptions.WriteThrough);
                using var writer=new StreamWriter(file);writer.WriteLine(text);writer.Flush();
            }
        }
        catch(IOException){}catch(UnauthorizedAccessException){}
    }
    public void Dispose(){heartbeat.Dispose();AppDomain.CurrentDomain.UnhandledException-=unhandled;foreach(var r in registrations)r.Dispose();}
}
