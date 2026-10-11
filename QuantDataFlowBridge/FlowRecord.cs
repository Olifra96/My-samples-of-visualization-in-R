using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuantDataFlowBridge;

// Ein Optionsprint. Provider-Felder stammen unverändert aus der QuantData-Antwort (fehlend = null);
// Reader-Felder werden von dieser App berechnet und sind getrennt gehalten.
public sealed class FlowRecord
{
    // ---------- Provider (QuantData) ----------
    public required DateTimeOffset TradeTime { get; init; }
    public string? ProviderId { get; init; }
    public string? Ticker { get; init; }
    public string? Osi { get; init; }
    public string? PutCall { get; init; }
    public string? Expiration { get; init; }
    public decimal? Dte { get; init; }
    public decimal? Strike { get; init; }
    public decimal? Premium { get; init; }
    public decimal? Size { get; init; }
    public decimal? Volume { get; init; }
    public decimal? OpenInterest { get; init; }
    public decimal? Price { get; init; }
    public decimal? UnderlyingPrice { get; init; }
    public string? TradeSideCode { get; init; }
    public string? TradeType { get; init; }
    public string? TradeConsolidationType { get; init; }
    public string? Sentiment { get; init; }
    public bool? IsGoldenSweep { get; init; }

    // Vollständiger Rohdatensatz, wie vom Provider geliefert.
    public required JsonElement Raw { get; init; }

    // ---------- Reader-Ableitungen ----------
    public required string Key { get; init; }                 // Schlüssel für Duplikaterkennung
    public required string RequestedTicker { get; init; }     // Ticker, für den abgefragt wurde
    public required DateTimeOffset ReceivedAt { get; init; }  // Empfangszeit (nicht Trade-Zeit!)
    public required string Endpoint { get; init; }            // consolidated / unconsolidated / demo
    public required string Aggressor { get; init; }           // BUY / SELL / UNKNOWN (relativ zu Bid/Ask)
    public required string AggressorConfidence { get; init; }
    public int? ComprisingTradeCount { get; init; }

    [JsonIgnore]
    public string DisplayTicker => Ticker ?? RequestedTicker;

    public static FlowRecord? FromProvider(JsonElement e, FieldMapConfig map, ReaderConfig reader,
        string requestedTicker, string endpoint, DateTimeOffset receivedAt)
    {
        var time = GetTime(e, map.TradeTime);
        if (time == null) return null;

        var id = GetString(e, map.Id);
        var osi = GetString(e, map.Osi);
        var size = GetDecimal(e, map.Size);
        var price = GetDecimal(e, map.Price);
        var sideCode = GetString(e, map.TradeSideCode);

        SideMapping? side = null;
        if (sideCode != null) reader.TradeSideCodeMap.TryGetValue(sideCode, out side);

        int? comprising = e.TryGetProperty(map.ComprisingTrades, out var ct) && ct.ValueKind == JsonValueKind.Array
            ? ct.GetArrayLength() : null;

        return new FlowRecord
        {
            TradeTime = time.Value,
            ProviderId = id,
            Ticker = GetString(e, map.Ticker),
            Osi = osi,
            PutCall = GetString(e, map.PutCall),
            Expiration = GetString(e, map.Expiration),
            Dte = GetDecimal(e, map.Dte),
            Strike = GetDecimal(e, map.Strike),
            Premium = GetDecimal(e, map.Premium),
            Size = size,
            Volume = GetDecimal(e, map.Volume),
            OpenInterest = GetDecimal(e, map.OpenInterest),
            Price = price,
            UnderlyingPrice = GetDecimal(e, map.UnderlyingPrice),
            TradeSideCode = sideCode,
            TradeType = GetString(e, map.TradeType),
            TradeConsolidationType = GetString(e, map.TradeConsolidationType),
            Sentiment = GetString(e, map.Sentiment),
            IsGoldenSweep = GetBool(e, map.IsGoldenSweep),
            Raw = e.Clone(),

            // Ohne Provider-ID: stabiler Ersatzschlüssel aus Zeit, Kontrakt, Menge und Preis.
            Key = id ?? string.Join('|', endpoint, requestedTicker, time.Value.ToUnixTimeMilliseconds(),
                osi ?? "", size?.ToString(CultureInfo.InvariantCulture) ?? "", price?.ToString(CultureInfo.InvariantCulture) ?? ""),
            RequestedTicker = requestedTicker,
            ReceivedAt = receivedAt,
            Endpoint = endpoint,
            Aggressor = side?.Aggressor ?? "UNKNOWN",
            AggressorConfidence = side?.Confidence ?? "NONE",
            ComprisingTradeCount = comprising
        };
    }

    private static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.GetRawText(),
            _ => null
        } : null;

    private static decimal? GetDecimal(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String &&
            decimal.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out d)) return d;
        return null;
    }

    private static bool? GetBool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(v.GetString(), out var b) => b,
            _ => null
        } : null;

    // tradeTime ist laut Provider Unix-Millisekunden; ISO-Strings werden zusätzlich akzeptiert.
    private static DateTimeOffset? GetTime(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var ms))
            return DateTimeOffset.FromUnixTimeMilliseconds(ms);
        if (v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString();
            if (long.TryParse(s, out ms)) return DateTimeOffset.FromUnixTimeMilliseconds(ms);
            if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)) return t;
        }
        return null;
    }
}
