using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QuantDataFlowBridge;

// Hält die letzten Flow-Einträge im Speicher (Quelle für JSON-Datei und HTTP-Endpunkt).
public sealed class FlowStore
{
    private readonly object _lock = new();
    private readonly LinkedList<FlowRecord> _items = new();
    private readonly HashSet<string> _seen = new();
    private readonly Queue<string> _seenOrder = new();
    private readonly int _max;

    public FlowStore(int maxRecords) => _max = Math.Max(1, maxRecords);

    // Gibt nur die wirklich neuen Einträge zurück (Überlappung und Mehrfachseiten werden dedupliziert).
    public List<FlowRecord> AddNew(IEnumerable<FlowRecord> records)
    {
        var added = new List<FlowRecord>();
        lock (_lock)
        {
            foreach (var r in records.OrderBy(r => r.TradeTime))
            {
                if (!_seen.Add(r.Key)) continue;
                _seenOrder.Enqueue(r.Key);
                if (_seenOrder.Count > 200_000) _seen.Remove(_seenOrder.Dequeue());

                // Sortiert einfügen, da Catch-up-Seiten ältere Prints nachliefern können.
                var node = _items.Last;
                while (node != null && node.Value.TradeTime > r.TradeTime) node = node.Previous;
                if (node == null) _items.AddFirst(r); else _items.AddAfter(node, r);
                if (_items.Count > _max) _items.RemoveFirst();
                added.Add(r);
            }
        }
        return added;
    }

    // minPremium: Datensätze ohne Premium-Angabe werden dabei ausgeschlossen (nicht als 0 gewertet).
    public List<FlowRecord> Snapshot(string? ticker = null, DateTimeOffset? since = null, decimal? minPremium = null)
    {
        lock (_lock)
            return _items
                .Where(r => ticker == null || r.DisplayTicker.Equals(ticker, StringComparison.OrdinalIgnoreCase))
                .Where(r => since == null || r.TradeTime > since)
                .Where(r => minPremium == null || r.Premium >= minPremium)
                .ToList();
    }
}

public static class FlowFormat
{
    // Spalten ohne Präfix = Provider-Felder (QuantData); Spalten mit "reader_" = von dieser App berechnet.
    public const string CsvHeader =
        "trade_time_utc,trade_time_ms,ticker,osi,put_call,expiration,dte,strike,premium,size,volume,open_interest," +
        "price,underlying_price,trade_side_code,trade_type,trade_consolidation_type,sentiment,is_golden_sweep," +
        "reader_aggressor,reader_aggressor_confidence,reader_comprising_trades,reader_endpoint,reader_received_utc";

    public static string ToCsv(FlowRecord r) => string.Join(',',
        r.TradeTime.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
        r.TradeTime.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
        Esc(r.DisplayTicker), Esc(r.Osi), Esc(r.PutCall), Esc(r.Expiration), Num(r.Dte), Num(r.Strike),
        Num(r.Premium), Num(r.Size), Num(r.Volume), Num(r.OpenInterest), Num(r.Price), Num(r.UnderlyingPrice),
        Esc(r.TradeSideCode), Esc(r.TradeType), Esc(r.TradeConsolidationType), Esc(r.Sentiment),
        r.IsGoldenSweep switch { true => "true", false => "false", null => "" },
        r.Aggressor, r.AggressorConfidence,
        r.ComprisingTradeCount?.ToString(CultureInfo.InvariantCulture) ?? "",
        r.Endpoint,
        r.ReceivedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));

    private static string Num(decimal? d) => d?.ToString(CultureInfo.InvariantCulture) ?? "";

    private static string Esc(string? s) =>
        s == null ? "" : s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    // JSON: Rohdaten des Providers unverändert unter "provider", eigene Ableitungen unter "reader".
    public static JsonObject ToJson(FlowRecord r) => new()
    {
        ["provider"] = JsonNode.Parse(r.Raw.GetRawText()),
        ["reader"] = new JsonObject
        {
            ["key"] = r.Key,
            ["requestedTicker"] = r.RequestedTicker,
            ["endpoint"] = r.Endpoint,
            ["receivedAt"] = r.ReceivedAt.ToUnixTimeMilliseconds(),
            ["aggressor"] = r.Aggressor,
            ["aggressorConfidence"] = r.AggressorConfidence,
            ["comprisingTradeCount"] = r.ComprisingTradeCount
        }
    };

    public static string ToJsonArray(IEnumerable<FlowRecord> records) =>
        new JsonArray(records.Select(r => (JsonNode)ToJson(r)).ToArray()).ToJsonString();

    public static string StatusJson(IReadOnlyList<TickerStatus> status) => new JsonObject
    {
        ["generatedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        ["tickers"] = new JsonArray(status.Select(s => (JsonNode)new JsonObject
        {
            ["ticker"] = s.Ticker,
            ["state"] = s.State.ToString().ToUpperInvariant(),
            ["endpoint"] = s.Endpoint,
            ["message"] = s.Message,
            ["checkpoint"] = s.Checkpoint?.ToUnixTimeMilliseconds(),
            ["updatedAt"] = s.UpdatedAt.ToUnixTimeMilliseconds()
        }).ToArray())
    }.ToJsonString();
}

// Schreibt pro Tag eine CSV-Datei (anhängend) und eine stets aktuelle JSON-Datei mit den letzten N Einträgen.
public sealed class FileSink
{
    private readonly OutputConfig _cfg;

    public FileSink(OutputConfig cfg)
    {
        _cfg = cfg;
        Directory.CreateDirectory(cfg.Directory);
    }

    public string CsvPathFor(DateTime utcDate) =>
        Path.Combine(_cfg.Directory, $"flow_{utcDate:yyyyMMdd}.csv");

    public string JsonPath => Path.Combine(_cfg.Directory, "flow_latest.json");
    public string StatusPath => Path.Combine(_cfg.Directory, "status.json");
    public string StatePath => Path.Combine(_cfg.Directory, "checkpoints.json");

    public void Write(IReadOnlyList<FlowRecord> added, FlowStore store)
    {
        if (_cfg.WriteCsv && added.Count > 0)
        {
            // CSV ist nach Empfang angehängt; Catch-up-Prints können älter sein als bereits geschriebene Zeilen.
            foreach (var group in added.GroupBy(r => r.TradeTime.UtcDateTime.Date))
            {
                var path = CsvPathFor(group.Key);
                var sb = new StringBuilder();
                if (!File.Exists(path)) sb.AppendLine(FlowFormat.CsvHeader);
                foreach (var r in group) sb.AppendLine(FlowFormat.ToCsv(r));
                WithRetry(() => File.AppendAllText(path, sb.ToString()));
            }
        }

        if (_cfg.WriteJson && added.Count > 0)
            WriteAtomic(JsonPath, FlowFormat.ToJsonArray(store.Snapshot()));
    }

    public void WriteStatus(IReadOnlyList<TickerStatus> status) =>
        WriteAtomic(StatusPath, FlowFormat.StatusJson(status));

    // Atomar ersetzen, damit ein Leser nie eine halb geschriebene Datei sieht.
    private static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        WithRetry(() =>
        {
            File.WriteAllText(tmp, content);
            File.Move(tmp, path, overwrite: true);
        });
    }

    // Unter Windows kann die Datei kurz von einem Leser (z. B. DeepCharts) gesperrt sein.
    private static void WithRetry(Action action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { action(); return; }
            catch (IOException) when (attempt < 5) { Thread.Sleep(100 * attempt); }
        }
    }
}

// Minimaler lokaler HTTP-Server (nur localhost):
//   /flow[.csv]?ticker=SPX&since=<trade_time_ms>&minPremium=<USD>   /status   /health
public sealed class HttpSink : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly FlowStore _store;
    private readonly Func<IReadOnlyList<TickerStatus>> _status;

    public HttpSink(int port, FlowStore store, Func<IReadOnlyList<TickerStatus>> status)
    {
        _store = store;
        _status = status;
        _listener.Prefixes.Add($"http://localhost:{port}/");
    }

    public void Start(CancellationToken ct)
    {
        _listener.Start();
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested && _listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch (Exception) when (ct.IsCancellationRequested || !_listener.IsListening) { break; }
                catch (HttpListenerException) { continue; }
                _ = Task.Run(() => Handle(ctx));
            }
        }, ct);
    }

    private void Handle(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath.TrimEnd('/').ToLowerInvariant() ?? "";
            var ticker = ctx.Request.QueryString["ticker"];
            DateTimeOffset? since = long.TryParse(ctx.Request.QueryString["since"], out var ms)
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
            decimal? minPremium = decimal.TryParse(ctx.Request.QueryString["minPremium"], NumberStyles.Number,
                CultureInfo.InvariantCulture, out var mp) ? mp : null;

            switch (path)
            {
                case "/flow":
                    Respond(ctx, 200, "application/json", FlowFormat.ToJsonArray(_store.Snapshot(ticker, since, minPremium)));
                    break;
                case "/flow.csv":
                    var sb = new StringBuilder().AppendLine(FlowFormat.CsvHeader);
                    foreach (var r in _store.Snapshot(ticker, since, minPremium)) sb.AppendLine(FlowFormat.ToCsv(r));
                    Respond(ctx, 200, "text/csv", sb.ToString());
                    break;
                case "/status":
                    Respond(ctx, 200, "application/json", FlowFormat.StatusJson(_status()));
                    break;
                case "" or "/health":
                    Respond(ctx, 200, "text/plain", "ok");
                    break;
                default:
                    Respond(ctx, 404, "text/plain", "Endpunkte: /flow, /flow.csv, /status, /health");
                    break;
            }
        }
        catch (Exception ex)
        {
            try { Respond(ctx, 500, "text/plain", ex.Message); } catch { /* Verbindung bereits weg */ }
        }
    }

    private static void Respond(HttpListenerContext ctx, int status, string contentType, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = contentType + "; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes);
        ctx.Response.Close();
    }

    public void Dispose()
    {
        if (_listener.IsListening) _listener.Stop();
        _listener.Close();
    }
}
