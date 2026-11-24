namespace PulletCryptoRadar.Core;

public enum RequestPriority { Background, Core, Critical }
public sealed record RequestDecision(bool Allowed,string Reason,DateTimeOffset? RetryAt,RequestPriority Priority);
public static class CollectionRequestScope
{
    private static readonly AsyncLocal<RequestPriority?> current=new();
    public static RequestPriority Current=>current.Value??RequestPriority.Core;
    public static IDisposable Enter(RequestPriority priority)
    {
        var previous=current.Value;current.Value=priority;return new Scope(previous);
    }
    private sealed class Scope(RequestPriority? previous):IDisposable
    {
        public void Dispose()=>current.Value=previous;
    }
}
