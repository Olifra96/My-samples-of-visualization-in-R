using System.Text.Json;

namespace QuantDataFlowBridge;

public sealed class AppConfig
{
    public ApiConfig Api { get; set; } = new();
    public FieldMapConfig FieldMap { get; set; } = new();
    public FilterConfig Filter { get; set; } = new();
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
    public string BaseUrl { get; set; } = "";
    public string FlowPath { get; set; } = "";
    public string TickerQueryParam { get; set; } = "ticker";
    public string ApiKeyHeader { get; set; } = "Authorization";
    public string ApiKeyPrefix { get; set; } = "Bearer ";

    // Name des Arrays in der Antwort (z. B. {"data":[...]}); leer = Antwort ist direkt ein Array.
    public string DataArrayProperty { get; set; } = "data";
    public int PollSeconds { get; set; } = 15;
}

// Ordnet die JSON-Feldnamen der Quant-Data-Antwort unseren internen Feldern zu.
public sealed class FieldMapConfig
{
    public string Id { get; set; } = "id";
    public string Time { get; set; } = "timestamp";
    public string Ticker { get; set; } = "ticker";
    public string Expiry { get; set; } = "expiration";
    public string Strike { get; set; } = "strike";
    public string PutCall { get; set; } = "put_call";
    public string Side { get; set; } = "side";
    public string Size { get; set; } = "size";
    public string Price { get; set; } = "price";
    public string Premium { get; set; } = "premium";
    public string Underlying { get; set; } = "underlying_price";
}

public sealed class FilterConfig
{
    public List<string> Tickers { get; set; } = new();
    public decimal MinPremium { get; set; }
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
