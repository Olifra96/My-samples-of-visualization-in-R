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

IFlowSource source;
if (demo)
{
    source = new DemoSource();
}
else
{
    var apiKey = Environment.GetEnvironmentVariable("QUANTDATA_API_KEY");
    if (string.IsNullOrWhiteSpace(apiKey))
        return Fail("Umgebungsvariable QUANTDATA_API_KEY ist nicht gesetzt (oder mit --demo starten).");
    source = new QuantDataSource(cfg.Api, cfg.FieldMap, apiKey);
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var store = new FlowStore(cfg.Output.JsonMaxRecords);
var files = new FileSink(cfg.Output);
using var http = cfg.Output.HttpPort > 0 ? new HttpSink(cfg.Output.HttpPort, store) : null;

try { http?.Start(cts.Token); }
catch (Exception ex) { return Fail($"HTTP-Endpunkt auf Port {cfg.Output.HttpPort} konnte nicht starten: {ex.Message}"); }

ConsoleView.Banner(cfg, demo, files, http != null);

var poll = TimeSpan.FromSeconds(Math.Max(1, demo ? Math.Min(cfg.Api.PollSeconds, 3) : cfg.Api.PollSeconds));
while (!cts.IsCancellationRequested)
{
    foreach (var ticker in cfg.Filter.Tickers)
    {
        if (cts.IsCancellationRequested) break;
        try
        {
            var fetched = await source.FetchAsync(ticker, cts.Token);
            var filtered = fetched.Where(r => r.Premium >= cfg.Filter.MinPremium);
            var added = store.AddNew(filtered);
            files.Write(added, store);
            foreach (var r in added) ConsoleView.Print(r);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { break; }
        catch (Exception ex)
        {
            ConsoleView.Error($"{ticker}: {ex.Message}");
        }
    }

    try { await Task.Delay(poll, cts.Token); }
    catch (OperationCanceledException) { break; }
}

(source as IDisposable)?.Dispose();
Console.ResetColor();
Console.WriteLine("Beendet.");
return 0;

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
            Console.WriteLine("════════════════ Quant Data Options Flow Bridge ════════════════");
            Console.ResetColor();
            Console.WriteLine($" Quelle     : {(demo ? "DEMO (Zufallsdaten)" : cfg.Api.BaseUrl + cfg.Api.FlowPath)}");
            Console.WriteLine($" Ticker     : {string.Join(", ", cfg.Filter.Tickers)}   Min. Premium: {cfg.Filter.MinPremium:N0} $");
            if (cfg.Output.WriteCsv)  Console.WriteLine($" CSV        : {files.CsvPathFor(DateTime.UtcNow)}");
            if (cfg.Output.WriteJson) Console.WriteLine($" JSON       : {files.JsonPath}");
            if (httpOn) Console.WriteLine($" HTTP       : http://localhost:{cfg.Output.HttpPort}/flow  (/flow.csv, ?ticker=SPY&since=<unix_ms>)");
            Console.WriteLine(" Beenden mit Strg+C");
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"{"Zeit (lokal)",-12} {"Ticker",-6} {"Typ",-4} {"Strike",9} {"Verfall",-10} {"Seite",-9} {"Größe",7} {"Preis",8} {"Premium",14}");
            Console.ResetColor();
        }
    }

    public static void Print(FlowRecord r)
    {
        lock (Lock)
        {
            Console.Write($"{r.Time.ToLocalTime():HH:mm:ss}     {r.Ticker,-6} ");
            Console.ForegroundColor = r.IsCall ? ConsoleColor.Green : ConsoleColor.Red;
            Console.Write($"{(r.IsCall ? "C" : "P"),-4} ");
            Console.ResetColor();
            Console.Write($"{r.Strike,9:0.##} {r.Expiry?.ToString("yyyy-MM-dd") ?? "",-10} {r.Side,-9} {r.Size,7:N0} {r.Price,8:0.00} ");
            Console.ForegroundColor = r.Premium >= 1_000_000 ? ConsoleColor.Yellow : ConsoleColor.Gray;
            Console.WriteLine(r.Premium.ToString("N0", CultureInfo.CurrentCulture).PadLeft(12) + " $");
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
