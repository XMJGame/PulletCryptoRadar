using System.Globalization;
using System.Text.Json;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Providers.Binance;

public sealed partial class BinanceRestClient
{
    public async Task<DerivativeSample> GetOpenInterestAsync(string symbol, CancellationToken ct)
    {
        using var json = await GetAsync($"/fapi/v1/openInterest?symbol={Uri.EscapeDataString(symbol)}", 1, ct);
        return DerivativeParser.Current(json.RootElement, symbol, time.GetUtcNow());
    }
    public async Task<IReadOnlyList<DerivativeSample>> GetSeriesAsync(string symbol, DerivativeKind kind, int limit, CancellationToken ct)
    {
        var route = kind switch
        {
            DerivativeKind.OpenInterest5m => "openInterestHist", DerivativeKind.GlobalLongShort5m => "globalLongShortAccountRatio",
            DerivativeKind.TopAccount5m => "topLongShortAccountRatio", DerivativeKind.TopPosition5m => "topLongShortPositionRatio",
            DerivativeKind.Taker5m => "takerlongshortRatio", _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        using var json = await GetAsync($"/futures/data/{route}?symbol={Uri.EscapeDataString(symbol)}&period=5m&limit={Math.Clamp(limit, 1, 500)}", 1, ct);
        return DerivativeParser.Series(json.RootElement, symbol, kind, time.GetUtcNow());
    }
}

public static class DerivativeParser
{
    private static decimal? Optional(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && decimal.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;
    public static DerivativeSample Current(JsonElement root, string symbol, DateTimeOffset received)
    {
        if (root.GetProperty("symbol").GetString() != symbol) throw new InvalidDataException("OI symbol mismatch");
        var quantity = Optional(root, "openInterest");
        if (quantity is null or < 0) throw new InvalidDataException("Invalid OI quantity");
        return new(symbol, DerivativeKind.CurrentOpenInterest, BinanceParser.Time(root.GetProperty("time")), received, Quantity: quantity);
    }
    public static IReadOnlyList<DerivativeSample> Series(JsonElement root, string symbol, DerivativeKind kind, DateTimeOffset received)
    {
        var result = new List<DerivativeSample>();
        foreach (var item in root.EnumerateArray())
        {
            if (item.TryGetProperty("symbol", out var s) && s.GetString() != symbol) throw new InvalidDataException("Derivative symbol mismatch");
            var timestamp = BinanceParser.Time(item.GetProperty("timestamp"));
            // Taker timestamps are period START; all feature sources use period END.
            var end = kind == DerivativeKind.Taker5m ? timestamp.AddMinutes(5) : timestamp;
            if (end > received) continue;
            DerivativeSample point;
            if (kind == DerivativeKind.OpenInterest5m)
            {
                var qty = Optional(item, "sumOpenInterest"); var value = Optional(item, "sumOpenInterestValue");
                if (qty is null or < 0 || value is < 0) continue;
                point = new(symbol, kind, end, received, qty, value);
            }
            else if (kind == DerivativeKind.Taker5m)
            {
                var buy = Optional(item, "buyVol"); var sell = Optional(item, "sellVol");
                if (buy is null or < 0 || sell is null or < 0) continue;
                point = new(symbol, kind, end, received, Ratio: sell > 0 ? buy / sell : null, BuyQuantity: buy, SellQuantity: sell);
            }
            else
            {
                var longShare = Optional(item, "longAccount"); var shortShare = Optional(item, "shortAccount");
                if (longShare is null or < 0 or > 1 || shortShare is null or < 0 or > 1 || Math.Abs((longShare + shortShare)!.Value - 1) > 0.02m) continue;
                point = new(symbol, kind, end, received, Ratio: Optional(item, "longShortRatio"), LongShare: longShare, ShortShare: shortShare);
            }
            result.Add(point);
        }
        return result.GroupBy(x => x.SourceTime).Select(g => g.Last()).OrderBy(x => x.SourceTime).ToArray();
    }
    public static LiquidationObservation? Liquidation(JsonElement root, DateTimeOffset received)
    {
        var o = root.GetProperty("o"); var side = o.GetProperty("S").GetString();
        var qty = Optional(o, "z"); var avg = Optional(o, "ap");
        if (side is not ("SELL" or "BUY") || qty is null or <= 0 || avg is null or <= 0) return null;
        return new(o.GetProperty("s").GetString()!, side, qty.Value, avg.Value, BinanceParser.Time(o.GetProperty("T")), received);
    }
}
