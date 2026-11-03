using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace PulletCryptoRadar.Infrastructure;
public static class StorageRecordCodec
{
    public const int MaximumExpandedBytes=64*1024*1024;
    public static T Decode<T>(byte[] value,int maximumBytes=MaximumExpandedBytes)
    {
        if(maximumBytes<=0||maximumBytes>MaximumExpandedBytes)throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        try
        {
            if(value.Length<18||value[0]!=0x1f||value[1]!=0x8b||value[2]!=8)throw new InvalidDataException("invalid gzip header");
            using var input=new MemoryStream(value,false);using var zip=new GZipStream(input,CompressionMode.Decompress);using var decoded=new MemoryStream();
            var buffer=new byte[16*1024];int count;
            while((count=zip.Read(buffer))>0)
            {
                if(decoded.Length+count>maximumBytes)throw new InvalidDataException("expanded record limit exceeded");
                decoded.Write(buffer,0,count);
            }
            if(BinaryPrimitives.ReadUInt32LittleEndian(value.AsSpan(value.Length-4))!=decoded.Length)throw new InvalidDataException("gzip length trailer mismatch");
            // Separate decompression from JSON conversion. A corrupt row now has a stable type/hash
            // and does not use the streaming JSON rethrow path observed in the crash event.
            return JsonSerializer.Deserialize<T>(decoded.GetBuffer().AsSpan(0,(int)decoded.Length))??throw new InvalidDataException("null JSON record");
        }
        catch(Exception ex)when(ex is JsonException or InvalidDataException or IOException or ArgumentException)
        {
            var hash=Convert.ToHexString(SHA256.HashData(value));
            throw new InvalidDataException($"存储记录读取失败：类型 {typeof(T).Name}；压缩字节 {value.Length}；SHA256 {hash}；{ex.GetType().Name}",ex);
        }
    }
}
