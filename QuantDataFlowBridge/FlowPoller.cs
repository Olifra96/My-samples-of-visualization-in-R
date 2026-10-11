using System.Text.Json;

namespace QuantDataFlowBridge;

public enum TickerState { Pending, Ok, Partial, Unavailable, Error }

public sealed record TickerStatus(string Ticker, TickerState State, string Endpoint, string? Message,
    DateTimeOffset? Checkpoint, DateTimeOffset UpdatedAt);

// Ein Abrufzyklus pro Ticker:
//  1. Catch-up ab Checkpoint (mit Überlappung, max. 24 h zurück), aufsteigend, höchstens N Seiten.
//  2. Wenn der Catch-up nicht fertig wurde (PARTIAL): zusätzlich die neueste Seite, damit aktuelle
//     Prints sichtbar sind, während der Rückstand über mehrere Zyklen aufgeholt wird.
public sealed class FlowPoller(OrderFlowClient client, AppConfig cfg, string statePath)
{
    private readonly Dictionary<string, DateTimeOffset> _checkpoints = LoadCheckpoints(statePath);
    private readonly Dictionary<string, TickerStatus> _status = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public IReadOnlyList<TickerStatus> Status
    {
        get { lock (_lock) return _status.Values.ToList(); }
    }

    public async Task<List<FlowRecord>> PollTickerAsync(string ticker, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var oldest = now.AddHours(-cfg.Api.MaxLookbackHours);
        var from = _checkpoints.TryGetValue(ticker, out var cp)
            ? cp.AddSeconds(-cfg.Api.OverlapSeconds)
            : oldest;
        if (from < oldest) from = oldest;

        try
        {
            var catchUp = await client.FetchAsync(ticker, from, now, ascending: true, cfg.Api.MaxCatchUpPages, ct);
            var records = catchUp.Records;

            if (records.Count > 0)
            {
                var latest = records.Max(r => r.TradeTime);
                if (!_checkpoints.TryGetValue(ticker, out var prev) || latest > prev)
                    _checkpoints[ticker] = latest;
                SaveCheckpoints();
            }

            if (catchUp.Partial)
            {
                var newest = await client.FetchAsync(ticker, from, now, ascending: false, maxPages: 1, ct);
                records.AddRange(newest.Records);
            }

            SetStatus(ticker, catchUp.Partial ? TickerState.Partial : TickerState.Ok, catchUp.Endpoint,
                catchUp.Partial ? "Catch-up läuft, ältere Prints folgen" : null);
            return records;
        }
        catch (ApiException ex) when (!ex.IsRateLimit && !ex.IsAuth && IsOptional(ticker))
        {
            SetStatus(ticker, TickerState.Unavailable, "", $"vom Anbieter nicht geliefert ({ex.StatusCode})");
            return new();
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not ApiException { IsRateLimit: true })
        {
            SetStatus(ticker, TickerState.Error, "", ex.Message);
            if (ex is ApiException { IsAuth: true }) throw;
            return new();
        }
    }

    private bool IsOptional(string ticker) =>
        cfg.Filter.OptionalTickers.Contains(ticker, StringComparer.OrdinalIgnoreCase);

    private void SetStatus(string ticker, TickerState state, string endpoint, string? message)
    {
        lock (_lock)
            _status[ticker] = new TickerStatus(ticker, state, endpoint, message,
                _checkpoints.TryGetValue(ticker, out var cp) ? cp : null, DateTimeOffset.UtcNow);
    }

    private static Dictionary<string, DateTimeOffset> LoadCheckpoints(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var ms = JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(path)) ?? new();
                return ms.ToDictionary(p => p.Key, p => DateTimeOffset.FromUnixTimeMilliseconds(p.Value),
                    StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception) { /* beschädigte Datei: ohne Checkpoint neu beginnen */ }
        return new(StringComparer.OrdinalIgnoreCase);
    }

    private void SaveCheckpoints()
    {
        var json = JsonSerializer.Serialize(_checkpoints.ToDictionary(p => p.Key, p => p.Value.ToUnixTimeMilliseconds()));
        var tmp = statePath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, statePath, overwrite: true);
    }
}
