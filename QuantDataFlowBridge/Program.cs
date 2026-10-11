using System.Globalization;
using System.Text;
using QuantDataFlowBridge;

// Aufruf:
//   QuantDataFlowBridge.exe            -> echte Daten (API-Key aus Umgebungsvariable QUANTDATA_API_KEY)
//   QuantDataFlowBridge.exe --demo     -> Zufallsdaten zum Testen ohne API
//   QuantDataFlowBridge.exe --config pfad\zu\appsettings.json

Console.OutputEncoding = Encoding.UTF8;

var demo = args.Contains("--demo", StringComparer.OrdinalIgnoreCase);
var configIdx = Array.FindIndex(args, a => a.Equals("--config", StringComparison.OrdinalIgnoreCase));
var configPath = configIdx >= 0 && configIdx + 1 < args.Length
    ? args[configIdx + 1]
    : Path.Combine(AppContext.BaseDirectory, "appsettings.json");

AppConfig cfg;
try { cfg = AppConfig.Load(configPath); }
catch (Exception ex) { return Fail($"Konfiguration konnte nicht geladen werden: {ex.Message}"); }

if (cfg.Filter.Tickers.Count == 0)
    return Fail("Keine Ticker in 'Filter.Tickers' konfiguriert.");
if (cfg.Api.RequestBodyTemplate.Count == 0)
    return Fail("'Api.RequestBodyTemplate' ist leer.");

IOrderFlowApi api;
if (demo)
{
    api = new DemoApi(cfg.FieldMap, cfg.Api);
}
else
{
    var apiKey = Environment.GetEnvironmentVariable("QUANTDATA_API_KEY");
    if (string.IsNullOrWhiteSpace(apiKey))
        return Fail("Umgebungsvariable QUANTDATA_API_KEY ist nicht gesetzt (oder mit --demo starten).");
    api = new QuantDataHttpApi(cfg.Api, apiKey.Trim());
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var store = new FlowStore(cfg.Output.JsonMaxRecords);
var files = new FileSink(cfg.Output);
var poller = new FlowPoller(new OrderFlowClient(api, cfg),
    cfg, demo ? Path.Combine(cfg.Output.Directory, "checkpoints.demo.json") : files.StatePath);
using var http = cfg.Output.HttpPort > 0 ? new HttpSink(cfg.Output.HttpPort, store, () => poller.Status) : null;

try { http?.Start(cts.Token); }
catch (Exception ex) { return Fail($"HTTP-Endpunkt auf Port {cfg.Output.HttpPort} konnte nicht starten: {ex.Message}"); }

ConsoleView.Banner(cfg, demo, files, http != null);

var poll = TimeSpan.FromSeconds(Math.Max(1, demo ? Math.Min(cfg.Api.PollSeconds, 3) : cfg.Api.PollSeconds));
var exitCode = 0;
var lastStatusLine = "";

while (!cts.IsCancellationRequested)
{
    var wait = poll;
    foreach (var ticker in cfg.Filter.Tickers)
    {
        if (cts.IsCancellationRequested) break;
        try
        {
            var fetched = await poller.PollTickerAsync(ticker, cts.Token);
            var added = store.AddNew(fetched.Where(r => (r.Premium ?? 0) >= cfg.Filter.MinPremium));
            files.Write(added, store);
            foreach (var r in added) ConsoleView.Print(r);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { break; }
        catch (ApiException ex) when (ex.IsRateLimit)
        {
            wait = ex.RetryAfter ?? TimeSpan.FromSeconds(cfg.Api.DefaultRetryAfterSeconds);
            ConsoleView.Warn($"Rate Limit erreicht – nächster Abruf in {wait.TotalSeconds:0} s.");
            break;
        }
        catch (ApiException ex) when (ex.IsAuth)
        {
            ConsoleView.Error($"Zugriff verweigert (HTTP {ex.StatusCode}). API-Key, aktives API-Abo und " +
                              "unterschriebene OPRA-Vereinbarung prüfen.");
            exitCode = 2;
            cts.Cancel();
        }
        catch (Exception ex)
        {
            ConsoleView.Error($"{ticker}: {ex.Message}");
        }
    }

    var status = poller.Status;
    try { files.WriteStatus(status); } catch (IOException) { /* nächster Zyklus */ }
    var line = ConsoleView.StatusLine(status);
    if (line != lastStatusLine) { ConsoleView.Status(line); lastStatusLine = line; }

    try { await Task.Delay(wait, cts.Token); }
    catch (OperationCanceledException) { break; }
}

(api as IDisposable)?.Dispose();
Console.ResetColor();
Console.WriteLine("Beendet.");
return exitCode;

static int Fail(string message)
{
    ConsoleView.Error(message);
    return 1;
}

static class ConsoleView
{
    private static readonly object Lock = new();

    public static void Banner(AppConfig cfg, bool demo, FileSink files, bool httpOn)
    {
        lock (Lock)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("══════════════ QuantData Options Flow Bridge ══════════════");
            Console.ResetColor();
            Console.WriteLine($" Quelle  : {(demo ? "DEMO (Zufallsdaten, keine echten Codes)" : cfg.Api.BaseUrl + cfg.Api.ConsolidatedPath)}");
            Console.WriteLine($" Ticker  : {string.Join(", ", cfg.Filter.Tickers)}   Anzeige ab Premium: {cfg.Filter.MinPremium:N0} $");
            if (cfg.Output.WriteCsv)  Console.WriteLine($" CSV     : {files.CsvPathFor(DateTime.UtcNow)}");
            if (cfg.Output.WriteJson) Console.WriteLine($" JSON    : {files.JsonPath}");
            if (httpOn) Console.WriteLine($" HTTP    : http://localhost:{cfg.Output.HttpPort}/flow  (/flow.csv, /status)");
            Console.WriteLine(" Polling per REST (kein Live-Stream). Beenden mit Strg+C.");
            Console.WriteLine(" Aggressor = Reader-Ableitung aus tradeSideCode (Ausführung relativ zu Bid/Ask, keine Absicht).");
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"{"Trade-Zeit",-12} {"Ticker",-6} {"C/P",-4} {"Strike",9} {"Verfall",-10} {"DTE",4} {"Größe",7} {"Premium",14}  {"SideCode",-9} {"Aggr.",-7} {"Typ",-8} Flags");
            Console.ResetColor();
        }
    }

    public static void Print(FlowRecord r)
    {
        lock (Lock)
        {
            Console.Write($"{r.TradeTime.ToLocalTime():HH:mm:ss.fff} {r.DisplayTicker,-6} ");
            var pc = r.PutCall?.ToUpperInvariant();
            Console.ForegroundColor = pc?.StartsWith('C') == true ? ConsoleColor.Green
                : pc?.StartsWith('P') == true ? ConsoleColor.Red : ConsoleColor.Gray;
            Console.Write($"{pc ?? "?",-4} ");
            Console.ResetColor();
            Console.Write($"{Fmt(r.Strike, "0.##"),9} {r.Expiration ?? "–",-10} {Fmt(r.Dte, "0"),4} {Fmt(r.Size, "N0"),7} ");
            Console.ForegroundColor = r.Premium >= 1_000_000 ? ConsoleColor.Yellow : ConsoleColor.Gray;
            Console.Write($"{(r.Premium is { } p ? p.ToString("N0", CultureInfo.CurrentCulture) + " $" : "–"),14}  ");
            Console.ResetColor();
            Console.Write($"{r.TradeSideCode ?? "–",-9} ");
            Console.ForegroundColor = r.Aggressor switch
            {
                "BUY" => ConsoleColor.Green, "SELL" => ConsoleColor.Red, _ => ConsoleColor.DarkGray
            };
            Console.Write($"{r.Aggressor,-7} ");
            Console.ResetColor();
            var flags = new List<string>();
            if (r.IsGoldenSweep == true) flags.Add("GOLDEN");
            if (r.ComprisingTradeCount is > 0) flags.Add($"{r.ComprisingTradeCount} Teil-Trades");
            if (r.Endpoint == "unconsolidated") flags.Add("RAW");
            Console.WriteLine($"{r.TradeConsolidationType ?? "–",-8} {string.Join(' ', flags)}");
        }
    }

    private static string Fmt(decimal? d, string format) =>
        d?.ToString(format, CultureInfo.CurrentCulture) ?? "–";

    public static string StatusLine(IReadOnlyList<TickerStatus> status) =>
        string.Join(" | ", status.Select(s => s.State switch
        {
            TickerState.Ok => $"{s.Ticker} OK" + (s.Endpoint == "unconsolidated" ? " (Fallback unconsolidated)" : ""),
            TickerState.Partial => $"{s.Ticker} PARTIAL – {s.Message}",
            TickerState.Unavailable => $"{s.Ticker}: {s.Message}, weiter ohne",
            TickerState.Error => $"{s.Ticker} FEHLER",
            _ => $"{s.Ticker} …"
        }));

    public static void Status(string line)
    {
        lock (Lock)
        {
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Status: {line}");
            Console.ResetColor();
        }
    }

    public static void Warn(string message)
    {
        lock (Lock)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
            Console.ResetColor();
        }
    }

    public static void Error(string message)
    {
        lock (Lock)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] FEHLER: {message}");
            Console.ResetColor();
        }
    }
}
