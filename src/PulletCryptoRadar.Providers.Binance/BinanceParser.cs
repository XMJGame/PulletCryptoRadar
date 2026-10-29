using System.Globalization;
using System.Text.Json;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Providers.Binance;

public static class BinanceParser
{
    public static decimal Decimal(JsonElement e) => e.ValueKind == JsonValueKind.String
        ? decimal.Parse(e.GetString()!, CultureInfo.InvariantCulture) : e.GetDecimal();
    public static DateTimeOffset Time(JsonElement e) => DateTimeOffset.FromUnixTimeMilliseconds(e.GetInt64());
    public static IReadOnlyList<SymbolDefinition> Symbols(JsonElement root)
        => root.GetProperty("symbols").EnumerateArray()
            .Where(x => x.GetProperty("quoteAsset").GetString() == "USDT" && x.GetProperty("contractType").GetString() == "PERPETUAL"
                && x.GetProperty("status").GetString() == "TRADING")
            .Select(x => new SymbolDefinition(x.GetProperty("symbol").GetString()!, x.GetProperty("baseAsset").GetString()!, Time(x.GetProperty("onboardDate")))).ToArray();

    public static IReadOnlyList<MarketEvent> Stream(JsonElement root, DateTimeOffset received)
    {
        if (root.TryGetPropertySafe("data", out var data)) root = data;
        var events = new List<MarketEvent>();
        if (root.ValueKind == JsonValueKind.Array) foreach (var entry in root.EnumerateArray()) Parse(entry, events, received);
        else Parse(root, events, received);
        return events;
    }
    private static void Parse(JsonElement e, List<MarketEvent> output, DateTimeOffset received)
    {
        if (e.TryGetProperty("code", out var code)) throw new InvalidDataException($"Binance subscription rejected: {code}");
        if (!e.TryGetProperty("e", out var type)) return; // Subscription acknowledgment.
        if (e.TryGetProperty("st", out var st) && st.GetInt32() != 1) return; // Ignore merged COIN-M instruments.
        if (type.GetString() == "forceOrder")
        {
            var observation = DerivativeParser.Liquidation(e, received);
            if (observation is not null) output.Add(new LiquidationEvent(observation));
            return;
        }
        var symbol = e.GetProperty("s").GetString()!;
        var time = Time(e.GetProperty("E"));
        switch (type.GetString())
        {
            case "24hrTicker": output.Add(new TickerEvent(symbol, Decimal(e.GetProperty("c")), Decimal(e.GetProperty("q")), Decimal(e.GetProperty("P")), time, received)); break;
            case "markPriceUpdate":
                output.Add(new MarkPriceEvent(symbol, Decimal(e.GetProperty("p")), Decimal(e.GetProperty("r")), Time(e.GetProperty("T")), time, received)); break;
            case "kline":
                var k = e.GetProperty("k");
                if (!k.GetProperty("x").GetBoolean()) return; // Features use completed bars only.
                var interval = k.GetProperty("i").GetString() switch { "1m" => 1, "5m" => 5, "15m" => 15, _ => 0 };
                if (interval == 0) return;
                var bar = new Candle(symbol, interval, Time(k.GetProperty("t")), Decimal(k.GetProperty("o")), Decimal(k.GetProperty("h")),
                    Decimal(k.GetProperty("l")), Decimal(k.GetProperty("c")), Decimal(k.GetProperty("q")), Time(k.GetProperty("T")), received);
                if (bar.IsValid) output.Add(new CandleEvent(bar));
                break;
        }
    }
    private static bool TryGetPropertySafe(this JsonElement e, string name, out JsonElement value)
    {
        value = default; return e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out value);
    }
    public static IReadOnlyList<Candle> Candles(JsonElement root, string symbol, int interval, DateTimeOffset received)
        => root.EnumerateArray().Where(x => Time(x[6]) < received).Select(x => new Candle(symbol, interval,
            Time(x[0]), Decimal(x[1]), Decimal(x[2]), Decimal(x[3]), Decimal(x[4]), Decimal(x[7]), Time(x[6]), received))
            .Where(x => x.IsValid).OrderBy(x => x.OpenTime).ToArray();
}
