using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

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

    // Gibt nur die wirklich neuen Einträge zurück (Duplikate aus wiederholtem Polling werden verworfen).
    public List<FlowRecord> AddNew(IEnumerable<FlowRecord> records)
    {
        var added = new List<FlowRecord>();
        lock (_lock)
        {
            foreach (var r in records.OrderBy(r => r.Time))
            {
                if (!_seen.Add(r.Id)) continue;
                _seenOrder.Enqueue(r.Id);
                if (_seenOrder.Count > 50_000) _seen.Remove(_seenOrder.Dequeue());

                _items.AddLast(r);
                if (_items.Count > _max) _items.RemoveFirst();
                added.Add(r);
            }
        }
        return added;
    }

    public List<FlowRecord> Snapshot(string? ticker = null, DateTimeOffset? since = null)
    {
        lock (_lock)
            return _items
                .Where(r => ticker == null || r.Ticker.Equals(ticker, StringComparison.OrdinalIgnoreCase))
                .Where(r => since == null || r.Time > since)
                .ToList();
    }
}

public static class FlowFormat
{
    public const string CsvHeader =
        "time_utc,unix_ms,ticker,expiry,strike,put_call,side,size,price,premium,underlying";

    public static string ToCsv(FlowRecord r) => string.Join(',',
        r.Time.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        r.Time.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
        r.Ticker,
        r.Expiry?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
        r.Strike.ToString(CultureInfo.InvariantCulture),
        r.PutCall,
        r.Side,
        r.Size.ToString(CultureInfo.InvariantCulture),
        r.Price.ToString(CultureInfo.InvariantCulture),
        r.Premium.ToString("0.##", CultureInfo.InvariantCulture),
        r.Underlying?.ToString(CultureInfo.InvariantCulture) ?? "");

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false
    };
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

    public void Write(IReadOnlyList<FlowRecord> added, FlowStore store)
    {
        if (_cfg.WriteCsv && added.Count > 0)
        {
            foreach (var group in added.GroupBy(r => r.Time.UtcDateTime.Date))
            {
                var path = CsvPathFor(group.Key);
                var sb = new StringBuilder();
                if (!File.Exists(path)) sb.AppendLine(FlowFormat.CsvHeader);
                foreach (var r in group) sb.AppendLine(FlowFormat.ToCsv(r));
                WithRetry(() => File.AppendAllText(path, sb.ToString()));
            }
        }

        if (_cfg.WriteJson && added.Count > 0)
        {
            // Atomar ersetzen, damit ein Leser nie eine halb geschriebene Datei sieht.
            var tmp = JsonPath + ".tmp";
            var json = JsonSerializer.Serialize(store.Snapshot(), FlowFormat.Json);
            WithRetry(() =>
            {
                File.WriteAllText(tmp, json);
                File.Move(tmp, JsonPath, overwrite: true);
            });
        }
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

// Minimaler lokaler HTTP-Server: http://localhost:{port}/flow[.csv]?ticker=SPY&since=<unix_ms>
public sealed class HttpSink : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly FlowStore _store;

    public HttpSink(int port, FlowStore store)
    {
        _store = store;
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

            switch (path)
            {
                case "/flow":
                    Respond(ctx, 200, "application/json",
                        JsonSerializer.Serialize(_store.Snapshot(ticker, since), FlowFormat.Json));
                    break;
                case "/flow.csv":
                    var sb = new StringBuilder().AppendLine(FlowFormat.CsvHeader);
                    foreach (var r in _store.Snapshot(ticker, since)) sb.AppendLine(FlowFormat.ToCsv(r));
                    Respond(ctx, 200, "text/csv", sb.ToString());
                    break;
                case "" or "/health":
                    Respond(ctx, 200, "text/plain", "ok");
                    break;
                default:
                    Respond(ctx, 404, "text/plain", "Endpunkte: /flow, /flow.csv, /health");
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
