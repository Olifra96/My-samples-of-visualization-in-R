// SDK-unabhängiger Kern für den Deepchart-Indikator "SPX Flow Bubbles".
// Holt SPX-Optionsprints im Hintergrund von der lokalen QuantDataFlowBridge (/flow.csv) und hält sie
// für das Zeichnen bereit. Keine Abhängigkeit von VolSysAPI: die Datei wird einfach ins
// Deepchart-Indikatorprojekt (src/) kopiert.
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DeepchartSpxFlow;

// Ein darzustellender Print. Provider-Werte (QuantData) und Reader-Ableitungen wie in der Bridge getrennt.
public sealed record SpxFlowBubble(
    string Key,                 // Reader: Schlüssel zur Duplikaterkennung
    DateTime TradeTimeUtc,      // Provider: tradeTime
    string PutCall,             // Provider: CALL / PUT
    decimal Premium,            // Provider: premium
    decimal? Strike,            // Provider
    string? Expiration,         // Provider
    string? ConsolidationType,  // Provider: tradeConsolidationType
    bool? IsGoldenSweep,        // Provider
    string Aggressor)           // Reader: BUY / SELL / UNKNOWN
{
    public bool IsCall => PutCall.StartsWith("C", StringComparison.OrdinalIgnoreCase);
    public bool IsPut => PutCall.StartsWith("P", StringComparison.OrdinalIgnoreCase);
    public string Label => PremiumLabel.Format(Premium);
}

public static class PremiumLabel
{
    // 850000 -> "850K", 1234567 -> "1.2M", 12500000 -> "13M", 1100000000 -> "1.1B"
    public static string Format(decimal premium)
    {
        var p = Math.Abs(premium);
        string Scale(decimal v, string suffix) =>
            (v < 10 ? Math.Round(v, 1, MidpointRounding.AwayFromZero).ToString("0.#", CultureInfo.InvariantCulture)
                    : Math.Round(v, 0, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture)) + suffix;

        if (p >= 1_000_000_000m) return Scale(p / 1_000_000_000m, "B");
        if (p >= 999_500m) return Scale(p / 1_000_000m, "M");
        if (p >= 1_000m) return Math.Round(p / 1_000m, 0, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "K";
        return Math.Round(p, 0).ToString("0", CultureInfo.InvariantCulture);
    }
}

public static class BubbleSize
{
    // Fläche proportional zur Prämie (Radius ~ Wurzel), begrenzt auf [minRadius, maxRadius].
    // Bei minPremium hat die Bubble minRadius; bei maxPremium (und darüber) maxRadius.
    public static double Radius(decimal premium, decimal minPremium, decimal maxPremium, double minRadius, double maxRadius)
    {
        if (maxPremium <= minPremium || premium <= minPremium) return minRadius;
        var t = Math.Sqrt((double)((Math.Min(premium, maxPremium) - minPremium) / (maxPremium - minPremium)));
        return minRadius + t * (maxRadius - minRadius);
    }
}

// Liest das CSV-Format der Bridge anhand der Kopfzeile (Spaltenreihenfolge egal, Anführungszeichen erlaubt).
public static class FlowCsv
{
    public static List<(SpxFlowBubble Bubble, long ReceivedMs)> Parse(string csv)
    {
        var result = new List<(SpxFlowBubble, long)>();
        var lines = csv.Split('\n');
        if (lines.Length == 0) return result;

        var header = SplitLine(lines[0].TrimEnd('\r'));
        var idx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < header.Count; i++) idx[header[i]] = i;

        string? Get(List<string> row, string col) =>
            idx.TryGetValue(col, out var i) && i < row.Count && row[i].Length > 0 ? row[i] : null;
        decimal? Dec(List<string> row, string col) =>
            decimal.TryParse(Get(row, col), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;
        long? Lng(List<string> row, string col) =>
            long.TryParse(Get(row, col), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : null;

        for (var n = 1; n < lines.Length; n++)
        {
            var line = lines[n].TrimEnd('\r');
            if (line.Length == 0) continue;
            var row = SplitLine(line);

            var timeMs = Lng(row, "trade_time_ms");
            var premium = Dec(row, "premium");
            var putCall = Get(row, "put_call");
            var key = Get(row, "reader_key");
            // Ohne Zeit, Prämie oder Call/Put lässt sich keine Bubble sinnvoll zeichnen.
            if (timeMs == null || premium == null || putCall == null || key == null) continue;

            bool? golden = Get(row, "is_golden_sweep") switch { "true" => true, "false" => false, _ => null };
            result.Add((new SpxFlowBubble(
                key,
                DateTimeOffset.FromUnixTimeMilliseconds(timeMs.Value).UtcDateTime,
                putCall.ToUpperInvariant(),
                premium.Value,
                Dec(row, "strike"),
                Get(row, "expiration"),
                Get(row, "trade_consolidation_type"),
                golden,
                Get(row, "reader_aggressor") ?? "UNKNOWN"),
                Lng(row, "reader_received_ms") ?? 0));
        }
        return result;
    }

    private static List<string> SplitLine(string line)
    {
        var cells = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else sb.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { cells.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        cells.Add(sb.ToString());
        return cells;
    }
}

// Pollt die Bridge im Hintergrund. Das Zeichnen liest nur Snapshot(); es wartet nie auf das Netzwerk.
public sealed class SpxFlowFeed : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _ticker;
    private readonly TimeSpan _interval;
    private readonly object _lock = new();
    private readonly Dictionary<string, SpxFlowBubble> _byKey = new();
    private List<SpxFlowBubble> _sorted = new();
    private CancellationTokenSource? _cts;
    private long _lastReceivedMs;

    public SpxFlowFeed(string bridgeUrl, string ticker = "SPX", TimeSpan? interval = null)
    {
        _http = new HttpClient { BaseAddress = new Uri(bridgeUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(10) };
        _ticker = ticker;
        _interval = interval ?? TimeSpan.FromSeconds(2);
    }

    // Wird nach jedem Abruf mit neuen Prints ausgelöst (aus einem Hintergrund-Thread).
    public event Action? Updated;

    public string? LastError { get; private set; }
    public DateTime? LastSuccessUtc { get; private set; }

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { await PollOnceAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex) { LastError = ex.Message; }

                try { await Task.Delay(_interval, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }, ct);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    public async Task<int> PollOnceAsync(CancellationToken ct = default)
    {
        // Kleine Überlappung; Duplikate werden über den Schlüssel verworfen.
        var url = $"flow.csv?ticker={Uri.EscapeDataString(_ticker)}";
        if (_lastReceivedMs > 0) url += $"&receivedSince={_lastReceivedMs - 5_000}";

        var csv = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
        var rows = FlowCsv.Parse(csv);

        var added = 0;
        lock (_lock)
        {
            foreach (var (bubble, receivedMs) in rows)
            {
                if (receivedMs > _lastReceivedMs) _lastReceivedMs = receivedMs;
                if (_byKey.ContainsKey(bubble.Key)) continue;
                _byKey[bubble.Key] = bubble;
                added++;
            }
            if (added > 0) _sorted = _byKey.Values.OrderBy(b => b.TradeTimeUtc).ToList();
        }

        LastError = null;
        LastSuccessUtc = DateTime.UtcNow;
        if (added > 0) Updated?.Invoke();
        return added;
    }

    // Alle Prints ab minPremium im Zeitfenster [fromUtc, toUtc], nach Trade-Zeit sortiert.
    public IReadOnlyList<SpxFlowBubble> Snapshot(decimal minPremium, DateTime? fromUtc = null, DateTime? toUtc = null)
    {
        List<SpxFlowBubble> all;
        lock (_lock) all = _sorted;
        return all.Where(b => b.Premium >= minPremium
                              && (fromUtc == null || b.TradeTimeUtc >= fromUtc)
                              && (toUtc == null || b.TradeTimeUtc <= toUtc))
                  .ToList();
    }

    public void Dispose()
    {
        Stop();
        _http.Dispose();
    }
}
