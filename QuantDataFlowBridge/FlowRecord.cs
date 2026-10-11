using System.Text.Json.Serialization;

namespace QuantDataFlowBridge;

public sealed record FlowRecord(
    string Id,
    DateTimeOffset Time,
    string Ticker,
    DateOnly? Expiry,
    decimal Strike,
    string PutCall,   // "CALL" / "PUT"
    string Side,      // z. B. "ASK", "BID", "MID"
    long Size,
    decimal Price,
    decimal Premium,
    decimal? Underlying)
{
    [JsonIgnore]
    public bool IsCall => PutCall.StartsWith("C", StringComparison.OrdinalIgnoreCase);
}
