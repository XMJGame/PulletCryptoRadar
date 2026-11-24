using System.Globalization;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Infrastructure;

public sealed class PositionMarketStream(IRequestLedger ledger,TimeProvider clock) : IPositionMarketStream
{
    public async IAsyncEnumerable<ProviderRead> ReadAsync(IReadOnlyList<AssetIdentity> assets,
        [EnumeratorCancellation] CancellationToken token)
    {
        if (assets.Count is < 1 or > 6 || assets.Select(item => item.BinanceSymbol).Distinct().Count() != assets.Count)
            throw new ArgumentException("Invalid position stream subscription.");
        using var session = CancellationTokenSource.CreateLinkedTokenSource(token);
        session.CancelAfter(TimeSpan.FromMinutes(1));
        var channel = Channel.CreateBounded<ProviderRead>(new BoundedChannelOptions(128) { FullMode = BoundedChannelFullMode.DropOldest });
        var pumps = new[] { PumpAsync("public","depth5@500ms"),PumpAsync("market","markPrice@1s") };
        var completion = CompleteAsync();
        try
        {
            await foreach (var read in channel.Reader.ReadAllAsync(token)) yield return read;
        }
        finally
        {
            await session.CancelAsync();
            await completion;
        }

        async Task CompleteAsync()
        {
            try { await Task.WhenAll(pumps); channel.Writer.TryComplete(); }
            catch (OperationCanceledException) when (session.IsCancellationRequested && !token.IsCancellationRequested) { channel.Writer.TryComplete(); }
            catch (Exception error) { channel.Writer.TryComplete(error); }
        }
        async Task PumpAsync(string route,string stream)
        {
            var id = Guid.NewGuid();
            const int capacity = 2_000_000;
            if (!ledger.Reserve(id,"BinanceStream",clock.GetUtcNow(),capacity))
            {
                await session.CancelAsync();
                throw new IOException("Streaming response budget unavailable.");
            }
            var bytes = 0;
            try
            {
                using var socket = new ClientWebSocket();
                socket.Options.Proxy = null;
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
                var names = string.Join('/',assets.Select(asset => Uri.EscapeDataString(asset.BinanceSymbol.ToLowerInvariant())+"@"+stream));
                using var connect = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
                connect.CancelAfter(TimeSpan.FromSeconds(12));
                await socket.ConnectAsync(new Uri($"wss://fstream.binance.com/{route}/stream?streams={names}"),connect.Token);
                while (!session.IsCancellationRequested && bytes < capacity)
                {
                    using var message = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        var buffer = new byte[Math.Min(4096,capacity-bytes)];
                        if (buffer.Length == 0) throw new IOException("Streaming segment exhausted.");
                        using var receive = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
                        receive.CancelAfter(TimeSpan.FromSeconds(5));
                        try { result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer),receive.Token); }
                        catch (OperationCanceledException) when (!session.IsCancellationRequested)
                        { throw new IOException("Position stream receive timeout."); }
                        bytes += result.Count;
                        if (result.MessageType != WebSocketMessageType.Text || message.Length+result.Count > 65536)
                            throw new InvalidDataException("Invalid stream frame.");
                        message.Write(buffer,0,result.Count);
                    } while (!result.EndOfMessage);
                    var body = Encoding.UTF8.GetString(message.GetBuffer(),0,(int)message.Length);
                    var read = Parse(assets,body,clock.GetUtcNow(),route);
                    await channel.Writer.WriteAsync(read,session.Token);
                }
            }
            catch
            {
                await session.CancelAsync();
                throw;
            }
            finally { ledger.Complete(id,bytes); }
        }
    }

    public static ProviderRead Parse(IReadOnlyList<AssetIdentity> assets,string body,DateTimeOffset received,string route)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var row = root.GetProperty("data");
        var symbol = row.GetProperty("s").GetString();
        var asset = assets.SingleOrDefault(item => item.BinanceSymbol == symbol) ?? throw new InvalidDataException("Unexpected stream asset.");
        if (row.TryGetProperty("st",out var type) && type.GetInt32() != 1) throw new InvalidDataException("Not a USD-M stream.");
        var kind = row.GetProperty("e").GetString();
        var expected = asset.BinanceSymbol.ToLowerInvariant()+"@"+(route == "public" ? "depth5@500ms" : "markPrice@1s");
        if (root.GetProperty("stream").GetString() != expected) throw new InvalidDataException("Stream route mismatch.");
        var evidence = new RawEvidence(Guid.NewGuid(),"Binance",asset.BinanceSymbol,"wss:/"+route+"/"+expected,received,
            null,CollectionFailure.None,body);
        if (route == "public" && kind == "depthUpdate")
        {
            var normalized = JsonSerializer.Serialize(new { symbol,lastUpdateId = row.GetProperty("u").GetInt64(),
                T = row.GetProperty("T").GetInt64(), E = row.GetProperty("E").GetInt64(),
                bids = row.GetProperty("b"), asks = row.GetProperty("a") });
            return new(evidence,[],OrderBook:BinanceMarketProvider.ParseOrderBook(asset,normalized,received));
        }
        if (route != "market" || kind != "markPriceUpdate") throw new InvalidDataException("Unexpected stream event.");
        decimal Number(string name) => decimal.Parse(row.GetProperty(name).GetString()!,NumberStyles.Float,CultureInfo.InvariantCulture);
        var at = DateTimeOffset.FromUnixTimeMilliseconds(row.GetProperty("E").GetInt64());
        var next = DateTimeOffset.FromUnixTimeMilliseconds(row.GetProperty("T").GetInt64());
        var price = Number("p"); var rate = Number("r");
        if (at > received || received-at > TimeSpan.FromSeconds(5) || next <= at || next-at > TimeSpan.FromHours(24) || price <= 0 || Math.Abs(rate) > 1)
            throw new InvalidDataException("Invalid mark stream time or value.");
        return new(evidence,[],MarketMark:new(asset.AssetId,price,rate,next,at,received));
    }
}

public sealed partial class FoundationStore
{
    public void SavePaperMarket(ProviderRead read)
    {
        lock (writer)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT COUNT(*) FROM V2RawEvidence WHERE Id=$id";
            command.Parameters.AddWithValue("$id",read.Evidence.Id.ToString());
            if ((long)command.ExecuteScalar()! != 0) return;
            SaveReads(connection,transaction,[read]);
            transaction.Commit();
        }
    }
}
