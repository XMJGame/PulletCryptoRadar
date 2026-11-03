using System.Text.Json;

namespace PulletCryptoRadar.Infrastructure;
public sealed class ServerInstanceLease:IDisposable
{
    private readonly FileStream file;
    public ServerInstanceLease(string directory)
    {
        var root=Path.GetFullPath(directory);Directory.CreateDirectory(root);
        try{file=new FileStream(Path.Combine(root,"server-instance.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
        catch(IOException ex){throw new InvalidOperationException("同一数据目录已有服务器运行；禁止双进程同时管理模拟账户",ex);}
        file.SetLength(0);JsonSerializer.Serialize(file,new{ProcessId=Environment.ProcessId,StartedAt=DateTimeOffset.UtcNow});file.Flush(true);
    }
    public void Dispose()=>file.Dispose();
}
