using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QuantDataFlowBridge;

public sealed class ApiException(int statusCode, string message, TimeSpan? retryAfter = null) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public TimeSpan? RetryAfter { get; } = retryAfter;
    public bool IsRateLimit => StatusCode == 429;
    public bool IsAuth => StatusCode is 401 or 403;
}

public interface IOrderFlowApi
{
    // Sendet einen POST und liefert die JSON-Antwort. `ticker` dient nur der Demo-Quelle als Hinweis.
    Task<JsonElement> PostAsync(string path, JsonObject body, string ticker, CancellationToken ct);
}

// REST-Zugriff auf QuantData: POST + JSON, Authorization: Bearer <QUANTDATA_API_KEY>.
// Der Schlüssel wird nur im Header gesetzt und nie ausgegeben oder protokolliert.
public sealed class QuantDataHttpApi : IOrderFlowApi, IDisposable
{
    private readonly HttpClient _http;
    private readonly TimeSpan _defaultRetryAfter;

    public QuantDataHttpApi(ApiConfig cfg, string apiKey)
    {
        _defaultRetryAfter = TimeSpan.FromSeconds(cfg.DefaultRetryAfterSeconds);
        _http = new HttpClient { BaseAddress = new Uri(cfg.BaseUrl), Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    public async Task<JsonElement> PostAsync(string path, JsonObject body, string ticker, CancellationToken ct)
    {
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(path, content, ct);

        if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            throw new ApiException(429, "Rate Limit erreicht (HTTP 429)", RetryAfterOf(resp) ?? _defaultRetryAfter);

        if (!resp.IsSuccessStatusCode)
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            throw new ApiException((int)resp.StatusCode,
                $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} auf {path}: {Truncate(text, 300)}",
                RetryAfterOf(resp));
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return doc.RootElement.Clone();
    }

    private static TimeSpan? RetryAfterOf(HttpResponseMessage resp)
    {
        var ra = resp.Headers.RetryAfter;
        if (ra?.Delta is { } delta) return delta;
        if (ra?.Date is { } date) return date - DateTimeOffset.UtcNow is var d && d > TimeSpan.Zero ? d : TimeSpan.Zero;
        return null;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    public void Dispose() => _http.Dispose();
}

// Ergebnis eines Abrufs für einen Ticker.
public sealed record FetchResult(List<FlowRecord> Records, bool Partial, string Endpoint);

// Baut Requests aus der Vorlage, blättert über nextSearchAfter und fällt bei 404/503 auf den
// unkonsolidierten Endpunkt zurück.
public sealed class OrderFlowClient(IOrderFlowApi api, AppConfig cfg)
{
    private readonly Dictionary<string, DateTimeOffset> _fallbackUntil = new(StringComparer.OrdinalIgnoreCase);

    public async Task<FetchResult> FetchAsync(string ticker, DateTimeOffset from, DateTimeOffset to,
        bool ascending, int maxPages, CancellationToken ct)
    {
        var useFallback = _fallbackUntil.TryGetValue(ticker, out var until) && until > DateTimeOffset.UtcNow;
        try
        {
            return await FetchPagesAsync(ticker, from, to, ascending, maxPages, consolidated: !useFallback, ct);
        }
        catch (ApiException ex) when (!useFallback && ex.StatusCode is 404 or 503)
        {
            _fallbackUntil[ticker] = DateTimeOffset.UtcNow.AddMinutes(cfg.Api.FallbackMinutes);
            return await FetchPagesAsync(ticker, from, to, ascending, maxPages, consolidated: false, ct);
        }
    }

    private async Task<FetchResult> FetchPagesAsync(string ticker, DateTimeOffset from, DateTimeOffset to,
        bool ascending, int maxPages, bool consolidated, CancellationToken ct)
    {
        var a = cfg.Api;
        var path = consolidated ? a.ConsolidatedPath : a.UnconsolidatedPath;
        var endpoint = consolidated ? "consolidated" : "unconsolidated";
        var baseBody = BuildBody(ticker, from, to, ascending);
        if (!consolidated)
            foreach (var key in a.UnconsolidatedRemoveKeys) baseBody.Remove(key);

        var records = new List<FlowRecord>();
        JsonNode? cursor = null;
        for (var page = 1; page <= Math.Max(1, maxPages); page++)
        {
            // Die Query bleibt zwischen den Seiten identisch; nur der Cursor ändert sich.
            var body = (JsonObject)baseBody.DeepClone();
            if (cursor != null) body[a.CursorRequestProperty] = cursor.DeepClone();

            var root = await api.PostAsync(path, body, ticker, ct);
            var receivedAt = DateTimeOffset.UtcNow;

            var data = root;
            if (root.ValueKind == JsonValueKind.Object && !string.IsNullOrEmpty(a.DataArrayProperty))
                data = root.TryGetProperty(a.DataArrayProperty, out var d) ? d : default;
            if (data.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException($"Antwort enthält kein Array '{a.DataArrayProperty}'.");

            foreach (var item in data.EnumerateArray())
            {
                var rec = FlowRecord.FromProvider(item, cfg.FieldMap, cfg.Reader, ticker, endpoint, receivedAt);
                if (rec != null) records.Add(rec);
            }

            cursor = root.ValueKind == JsonValueKind.Object && root.TryGetProperty(a.NextCursorProperty, out var next)
                     && next.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                ? JsonNode.Parse(next.GetRawText())
                : null;
            if (cursor == null || data.GetArrayLength() == 0)
                return new FetchResult(records, Partial: false, endpoint);
        }
        return new FetchResult(records, Partial: true, endpoint);
    }

    private JsonObject BuildBody(string ticker, DateTimeOffset from, DateTimeOffset to, bool ascending)
    {
        var tokens = new Dictionary<string, Func<JsonNode>>
        {
            ["$ticker"] = () => JsonValue.Create(ticker),
            ["$fromMs"] = () => JsonValue.Create(from.ToUnixTimeMilliseconds()),
            ["$toMs"] = () => JsonValue.Create(to.ToUnixTimeMilliseconds()),
            ["$fromIso"] = () => JsonValue.Create(from.UtcDateTime.ToString("o")),
            ["$toIso"] = () => JsonValue.Create(to.UtcDateTime.ToString("o")),
            ["$pageSize"] = () => JsonValue.Create(cfg.Api.PageSize),
            ["$sortDirection"] = () => JsonValue.Create(ascending ? cfg.Api.SortAscValue : cfg.Api.SortDescValue)
        };
        var body = (JsonObject)cfg.Api.RequestBodyTemplate.DeepClone();
        Replace(body, tokens);
        return body;
    }

    private static void Replace(JsonNode node, Dictionary<string, Func<JsonNode>> tokens)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    var child = obj[key];
                    if (child is JsonValue v && v.TryGetValue<string>(out var s) && tokens.TryGetValue(s, out var make))
                        obj[key] = make();
                    else if (child != null) Replace(child, tokens);
                }
                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    var child = arr[i];
                    if (child is JsonValue v && v.TryGetValue<string>(out var s) && tokens.TryGetValue(s, out var make))
                        arr[i] = make();
                    else if (child != null) Replace(child, tokens);
                }
                break;
        }
    }
}

// Zufallsdaten im Provider-Format (Feldnamen aus FieldMap), um Anzeige und DeepCharts-Anbindung
// ohne API zu testen. Werte und Codes sind frei erfunden.
public sealed class DemoApi(FieldMapConfig map, ApiConfig api) : IOrderFlowApi
{
    private readonly Random _rnd = new();
    private readonly Dictionary<string, decimal> _spot = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NDX"] = 20100m, ["QQQ"] = 495m, ["SPX"] = 5750m, ["SPY"] = 575m
    };

    public Task<JsonElement> PostAsync(string path, JsonObject body, string ticker, CancellationToken ct)
    {
        var spot = _spot.TryGetValue(ticker, out var s) ? s : 100m;
        spot += (decimal)(_rnd.NextDouble() - 0.5) * spot * 0.002m;
        _spot[ticker] = spot;

        var data = new JsonArray();
        for (int i = 0, n = _rnd.Next(0, 4); i < n; i++)
        {
            var isCall = _rnd.Next(2) == 0;
            var strike = Math.Round(spot * (1 + (decimal)(_rnd.NextDouble() - 0.5) * 0.04m) / 5) * 5;
            var size = _rnd.Next(10, 1500);
            var price = Math.Round((decimal)_rnd.NextDouble() * 40 + 0.5m, 2);
            var dte = _rnd.Next(0, 30);
            data.Add(new JsonObject
            {
                [map.Id] = Guid.NewGuid().ToString("N"),
                [map.TradeTime] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                [map.Ticker] = ticker,
                [map.PutCall] = isCall ? "CALL" : "PUT",
                [map.Expiration] = DateTime.UtcNow.Date.AddDays(dte).ToString("yyyy-MM-dd"),
                [map.Dte] = dte,
                [map.Strike] = strike,
                [map.Size] = size,
                [map.Price] = price,
                [map.Premium] = price * size * 100,
                [map.UnderlyingPrice] = Math.Round(spot, 2),
                [map.TradeSideCode] = new[] { "DEMO_A", "DEMO_B", "DEMO_M" }[_rnd.Next(3)],
                [map.TradeConsolidationType] = path.Contains("unconsolidated") ? null : new[] { "SWEEP", "BLOCK", "SPLIT" }[_rnd.Next(3)],
                [map.IsGoldenSweep] = _rnd.Next(10) == 0
            });
        }
        var root = new JsonObject { [api.DataArrayProperty] = data, [api.NextCursorProperty] = null };
        return Task.FromResult(JsonDocument.Parse(root.ToJsonString()).RootElement.Clone());
    }
}
