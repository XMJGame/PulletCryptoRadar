using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Providers.Binance;

public sealed class BinanceWebSocketClient(IOptions<BinanceOptions> options, IMarketEventBus bus,
    IProviderHealthStore health, TimeProvider time, ILogger<BinanceWebSocketClient> logger, INetworkTraffic? traffic = null) : IMarketStreamProvider
{
    public Task RunAsync(IReadOnlyCollection<string> symbols, CancellationToken ct)
    {
        var groups = symbols.Order().Chunk(options.Value.StreamSymbolsPerConnection).ToArray();
        var tasks = groups.Select((chunk, i) => PumpAsync(chunk.SelectMany(s => new[] { $"{s.ToLowerInvariant()}@kline_1m", $"{s.ToLowerInvariant()}@kline_5m", $"{s.ToLowerInvariant()}@kline_15m" }).ToArray(), $"Binance Kline {i + 1}", ct)).ToList();
        tasks.Add(PumpAsync(["!ticker@arr", "!markPrice@arr@1s", "!forceOrder@arr"], "Binance WebSocket", ct));
        return Task.WhenAll(tasks);
    }
    private async Task PumpAsync(string[] streams, string name, CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            var connectedAt = time.GetUtcNow();
            try
            {
                traffic?.BeforeRequest();
                using var socket = new ClientWebSocket();
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(20);
                using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
                lifetime.CancelAfter(TimeSpan.FromHours(23)); // Binance connections have a finite lifetime.
                await socket.ConnectAsync(new Uri(options.Value.WebSocketUrl), lifetime.Token);
                var subscription = JsonSerializer.SerializeToUtf8Bytes(new { method = "SUBSCRIBE", @params = streams, id = Guid.NewGuid().ToString("N") });
                traffic?.Record(subscription.Length);
                await socket.SendAsync(subscription, WebSocketMessageType.Text, true, lifetime.Token);
                // Closed candles missed during reconnect are repaired through REST by the coordinator.
                while (socket.State == WebSocketState.Open && !lifetime.IsCancellationRequested)
                {
                    using var message = new MemoryStream();
                    var buffer = new byte[65536];
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.ReceiveTimeoutSeconds));
                    ValueWebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
                        traffic?.Record(result.Count);
                        if (result.MessageType == WebSocketMessageType.Close) throw new WebSocketException("Remote closed stream");
                        message.Write(buffer, 0, result.Count);
                        if (message.Length > 8 * 1024 * 1024) throw new InvalidDataException("Oversized market message");
                    } while (!result.EndOfMessage);
                    using var json = JsonDocument.Parse(message.ToArray());
                    var events = BinanceParser.Stream(json.RootElement, time.GetUtcNow());
                    if (name == "Binance WebSocket") await bus.PublishAsync(new LiquidationCoverageEvent(true, connectedAt, time.GetUtcNow()), lifetime.Token);
                    foreach (var e in events) await bus.PublishAsync(e, lifetime.Token);
                    if (events.Count > 0) health.Success(name);
                    if (time.GetUtcNow() - connectedAt > TimeSpan.FromMinutes(1)) attempt = 0;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or JsonException or InvalidDataException or HttpRequestException or NetworkBudgetExceededException)
            {
                health.Failure(name, ex.Message); logger.LogWarning("{Provider} disconnected: {Reason}", name, ex.Message);
            }
            finally
            {
                if (name == "Binance WebSocket" && !ct.IsCancellationRequested)
                    await bus.PublishAsync(new LiquidationCoverageEvent(false, connectedAt, time.GetUtcNow()), ct);
            }
            var seconds = Math.Min(30, Math.Pow(2, Math.Min(attempt++, 5))) + Random.Shared.NextDouble();
            await Task.Delay(TimeSpan.FromSeconds(seconds), time, ct);
        }
    }
}
