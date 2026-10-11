using System.Text.Json;
using System.Text.Json.Nodes;

namespace QuantDataFlowBridge;

public sealed class AppConfig
{
    public ApiConfig Api { get; set; } = new();
    public FieldMapConfig FieldMap { get; set; } = new();
    public FilterConfig Filter { get; set; } = new();
    public ReaderConfig Reader { get; set; } = new();
    public OutputConfig Output { get; set; } = new();

    public static AppConfig Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Konfigurationsdatei nicht gefunden: {path}");

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };
        return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), options) ?? new AppConfig();
    }
}

public sealed class ApiConfig
{
    public string BaseUrl { get; set; } = "https://api.quantdata.us/";
    public string ConsolidatedPath { get; set; } = "v1/options/tool/order-flow/consolidated";
    public string UnconsolidatedPath { get; set; } = "v1/options/tool/order-flow/unconsolidated";

    // Schlüssel, die nur der konsolidierte Endpunkt kennt und die beim Fallback aus dem Body entfernt werden.
    public List<string> UnconsolidatedRemoveKeys { get; set; } = new() { "includeComprisingTrades" };

    public string DataArrayProperty { get; set; } = "data";
    public string NextCursorProperty { get; set; } = "nextSearchAfter";
    public string CursorRequestProperty { get; set; } = "searchAfter";

    public int PageSize { get; set; } = 100;
    public int MaxCatchUpPages { get; set; } = 8;
    public int OverlapSeconds { get; set; } = 30;
    public int MaxLookbackHours { get; set; } = 24;
    public int PollSeconds { get; set; } = 10;
    public int FallbackMinutes { get; set; } = 5;
    public int DefaultRetryAfterSeconds { get; set; } = 30;

    public string SortAscValue { get; set; } = "ASC";
    public string SortDescValue { get; set; } = "DESC";

    // Vorlage für den POST-Body. Platzhalter (als String-Werte): $ticker, $fromMs, $toMs, $fromIso, $toIso,
    // $pageSize, $sortDirection. Die genaue Form muss mit der QuantData-Doku bzw. route.ts abgeglichen werden.
    public JsonObject RequestBodyTemplate { get; set; } = new();
}

// JSON-Feldnamen der QuantData-Antwort. Fehlt ein Feld im Datensatz, bleibt der Wert null.
public sealed class FieldMapConfig
{
    public string Id { get; set; } = "id";
    public string TradeTime { get; set; } = "tradeTime";
    public string Ticker { get; set; } = "ticker";
    public string Osi { get; set; } = "osi";
    public string PutCall { get; set; } = "putCall";
    public string Expiration { get; set; } = "expirationDate";
    public string Dte { get; set; } = "dte";
    public string Strike { get; set; } = "strike";
    public string Premium { get; set; } = "premium";
    public string Size { get; set; } = "size";
    public string Volume { get; set; } = "volume";
    public string OpenInterest { get; set; } = "openInterest";
    public string Price { get; set; } = "price";
    public string UnderlyingPrice { get; set; } = "underlyingPrice";
    public string TradeSideCode { get; set; } = "tradeSideCode";
    public string TradeType { get; set; } = "tradeType";
    public string TradeConsolidationType { get; set; } = "tradeConsolidationType";
    public string Sentiment { get; set; } = "sentiment";
    public string IsGoldenSweep { get; set; } = "isGoldenSweep";
    public string ComprisingTrades { get; set; } = "comprisingTrades";
}

public sealed class FilterConfig
{
    public List<string> Tickers { get; set; } = new() { "NDX", "QQQ", "SPX" };

    // Ticker, deren Ausfall nur als Hinweis gemeldet wird (der Rest läuft weiter).
    public List<string> OptionalTickers { get; set; } = new() { "SPX" };

    // Nur für Anzeige und Ausgabe; der Datenabruf selbst filtert nicht nach Premium.
    public decimal MinPremium { get; set; }
}

public sealed class ReaderConfig
{
    // Zuordnung tradeSideCode -> Aggressor-Seite (Reader-Ableitung). Nicht aufgeführte Codes bleiben UNKNOWN.
    public Dictionary<string, SideMapping> TradeSideCodeMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class SideMapping
{
    public string Aggressor { get; set; } = "UNKNOWN";   // BUY / SELL / UNKNOWN
    public string Confidence { get; set; } = "LOW";      // HIGH / MEDIUM / LOW
}

public sealed class OutputConfig
{
    public string Directory { get; set; } = "output";
    public bool WriteCsv { get; set; } = true;
    public bool WriteJson { get; set; } = true;
    public int JsonMaxRecords { get; set; } = 500;

    // 0 = lokaler HTTP-Endpunkt deaktiviert
    public int HttpPort { get; set; } = 5005;
}
