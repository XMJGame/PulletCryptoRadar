using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace PulletCryptoRadar.Host;

public sealed class LocalControlWorker(IHostApplicationLifetime lifetime) : BackgroundService
{
    public static string InstanceId => "PulletCryptoRadar-"+Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant())))[..20];
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) return;
        while (!token.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(InstanceId,PipeDirection.InOut,1,PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(token);
                using var request = CancellationTokenSource.CreateLinkedTokenSource(token);
                request.CancelAfter(TimeSpan.FromSeconds(5));
                using var reader = new StreamReader(pipe,Encoding.UTF8,false,1024,true);
                await using var writer = new StreamWriter(pipe,new UTF8Encoding(false),1024,true) { AutoFlush = true };
                if (await reader.ReadLineAsync(request.Token) == "stop")
                {
                    await writer.WriteLineAsync("ok");
                    lifetime.StopApplication(); return;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception error) when (error is IOException or OperationCanceledException) { }
        }
    }
}
