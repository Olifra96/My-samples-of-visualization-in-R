using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace QuantDataFlowBridge;

public interface IFlowSource
{
    Task<IReadOnlyList<FlowRecord>> FetchAsync(string ticker, CancellationToken ct);
}

// Holt Options-Flow über die Quant-Data-REST-API. Pfad, Header und Feldnamen kommen aus appsettings.json,
// damit Anpassungen an das echte API-Schema keinen Code-Umbau erfordern.
public sealed class QuantDataSource : IFlowSource, IDisposable
{
    private readonly HttpClient _http;
    private readonly ApiConfig _api;
    private readonly FieldMapConfig _map;

    public QuantDataSource(ApiConfig api, FieldMapConfig map, string apiKey)
    {
        _api = api;
        _map = map;
        _http = new HttpClient { BaseAddress = new Uri(api.BaseUrl), Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.TryAddWithoutValidation(api.ApiKeyHeader, api.ApiKeyPrefix + apiKey);
    }

    public async Task<IReadOnlyList<FlowRecord>> FetchAsync(string ticker, CancellationToken ct)
    {
        var url = $"{_api.FlowPath}?{_api.TickerQueryParam}={Uri.EscapeDataString(ticker)}";
        using var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {Truncate(body, 300)}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var array = doc.RootElement;
        if (array.ValueKind == JsonValueKind.Object && !string.IsNullOrEmpty(_api.DataArrayProperty)
            && array.TryGetProperty(_api.DataArrayProperty, out var inner))
            array = inner;

        if (array.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException(
                $"Unerwartetes Antwortformat (kein Array). Prüfe 'Api.DataArrayProperty'. Antwort: {Truncate(doc.RootElement.GetRawText(), 300)}");

        var list = new List<FlowRecord>();
        foreach (var item in array.EnumerateArray())
        {
            var rec = Map(item, ticker);
            if (rec != null) list.Add(rec);
        }
        return list;
    }

    private FlowRecord? Map(JsonElement e, string fallbackTicker)
    {
        var time = GetTime(e, _map.Time);
        if (time == null) return null;

        var strike = GetDecimal(e, _map.Strike) ?? 0;
        var size = (long)(GetDecimal(e, _map.Size) ?? 0);
        var price = GetDecimal(e, _map.Price) ?? 0;
        var premium = GetDecimal(e, _map.Premium) ?? price * size * 100;
        var putCall = (GetString(e, _map.PutCall) ?? "").ToUpperInvariant() switch
        {
            "C" or "CALL" or "CALLS" => "CALL",
            "P" or "PUT" or "PUTS" => "PUT",
            var other => other
        };
        var ticker = GetString(e, _map.Ticker) ?? fallbackTicker;
        var expiry = DateOnly.TryParse(GetString(e, _map.Expiry), CultureInfo.InvariantCulture, out var d) ? d : (DateOnly?)null;

        // Ohne ID im Feed bauen wir einen stabilen Schlüssel für die Duplikaterkennung.
        var id = GetString(e, _map.Id)
                 ?? $"{ticker}|{time.Value.ToUnixTimeMilliseconds()}|{strike}|{putCall}|{size}|{price}";

        return new FlowRecord(id, time.Value, ticker.ToUpperInvariant(), expiry, strike, putCall,
            (GetString(e, _map.Side) ?? "").ToUpperInvariant(), size, price, premium,
            GetDecimal(e, _map.Underlying));
    }

    private static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
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

    // Akzeptiert ISO-8601-Strings sowie Unix-Zeit in Sekunden oder Millisekunden.
    private static DateTimeOffset? GetTime(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n))
            return n > 10_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(n) : DateTimeOffset.FromUnixTimeSeconds(n);
        if (v.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t))
            return t;
        return null;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    public void Dispose() => _http.Dispose();
}

// Erzeugt Zufallsdaten, um Terminal-Ausgabe und DeepCharts-Anbindung ohne API-Zugang zu testen.
public sealed class DemoSource : IFlowSource
{
    private readonly Random _rnd = new();
    private readonly Dictionary<string, decimal> _spot = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SPY"] = 575m, ["QQQ"] = 495m, ["SPX"] = 5750m, ["NDX"] = 20100m, ["AAPL"] = 225m, ["NVDA"] = 130m, ["TSLA"] = 250m
    };

    public Task<IReadOnlyList<FlowRecord>> FetchAsync(string ticker, CancellationToken ct)
    {
        var spot = _spot.TryGetValue(ticker, out var s) ? s : 100m;
        spot += (decimal)(_rnd.NextDouble() - 0.5) * spot * 0.002m;
        _spot[ticker] = spot;

        var list = new List<FlowRecord>();
        for (int i = 0, n = _rnd.Next(0, 4); i < n; i++)
        {
            var isCall = _rnd.Next(2) == 0;
            var strike = Math.Round(spot * (1 + (decimal)(_rnd.NextDouble() - 0.5) * 0.06m));
            var size = (long)_rnd.Next(50, 3000);
            var price = Math.Round((decimal)_rnd.NextDouble() * 8 + 0.2m, 2);
            list.Add(new FlowRecord(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, ticker.ToUpperInvariant(),
                DateOnly.FromDateTime(DateTime.Today.AddDays(_rnd.Next(0, 45))), strike, isCall ? "CALL" : "PUT",
                new[] { "ASK", "BID", "MID", "ABOVE_ASK" }[_rnd.Next(4)], size, price, price * size * 100,
                Math.Round(spot, 2)));
        }
        return Task.FromResult<IReadOnlyList<FlowRecord>>(list);
    }
}
