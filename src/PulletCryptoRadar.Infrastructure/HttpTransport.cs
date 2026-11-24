using System.Net;

namespace PulletCryptoRadar.Infrastructure;

public static class HttpTransport
{
    // Keep routing explicit. Payload limits still apply to the decoded stream, so
    // compression never bypasses parser/size checks or makes stale evidence fresh.
    public static HttpClientHandler Create(bool useProxy)=>new()
    {
        AllowAutoRedirect=false,UseProxy=useProxy,
        AutomaticDecompression=DecompressionMethods.GZip|DecompressionMethods.Deflate|DecompressionMethods.Brotli
    };
}
