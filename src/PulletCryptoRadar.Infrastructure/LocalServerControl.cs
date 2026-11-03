using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PulletCryptoRadar.Infrastructure;
public sealed class LocalServerControl(IHostApplicationLifetime lifetime,ILogger<LocalServerControl> logger):BackgroundService
{
    public static string PipeName(string directory)=>"PulletCryptoRadar-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar).ToUpperInvariant())))[..20];
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            try
            {
                await using var pipe=new NamedPipeServerStream(PipeName(AppContext.BaseDirectory),PipeDirection.InOut,1,PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(ct);using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(2));
                var command=new byte[5];await pipe.ReadExactlyAsync(command,timeout.Token);
                if(!command.AsSpan().SequenceEqual("stop\n"u8)){logger.LogWarning("Local control rejected an invalid command");continue;}
                await pipe.WriteAsync("ok\n"u8.ToArray(),timeout.Token);await pipe.FlushAsync(timeout.Token);
                lifetime.StopApplication();logger.LogInformation("Graceful shutdown requested by the current local user; accepted writes will drain");return;
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){return;}
            catch(OperationCanceledException){logger.LogWarning("Local shutdown request timed out; server kept running");}
            catch(IOException ex){logger.LogWarning("Local control IO error: {Type}",ex.GetType().Name);await Task.Delay(1000,ct);}
        }
    }
}
