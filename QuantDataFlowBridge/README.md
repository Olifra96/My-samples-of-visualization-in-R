# Quant Data Options Flow Bridge (C#)

Konsolen-App für das **Windows Terminal**: holt Options-Flow-Daten über die Quant-Data-API,
zeigt sie live und farbig im Terminal an und stellt sie gleichzeitig so bereit, dass
**DeepCharts** (oder jedes andere Tool) sie abholen kann.

```
Quant Data API ──► QuantDataFlowBridge.exe (Windows Terminal)
                     ├─ Live-Anzeige im Terminal (Calls grün, Puts rot, ≥1 Mio. $ gelb)
                     ├─ C:\QuantDataFlow\flow_YYYYMMDD.csv    (Tagesdatei, wird fortlaufend ergänzt)
                     ├─ C:\QuantDataFlow\flow_latest.json     (letzte N Einträge, atomar ersetzt)
                     └─ http://localhost:5005/flow            (JSON) bzw. /flow.csv
                                   ▲
                                   └── DeepCharts liest Datei oder Endpunkt
```

> Wichtig: Ein anderes Programm kann nicht direkt aus dem *Terminal-Fenster* lesen. Das Terminal
> dient nur zur Anzeige. DeepCharts greift auf die CSV-/JSON-Datei oder den lokalen HTTP-Endpunkt zu.

## Voraussetzungen

- Windows 10/11 mit [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Quant-Data-API-Key

## Starten

```powershell
cd QuantDataFlowBridge

# 1) Erst ohne API testen (Zufallsdaten):
dotnet run -- --demo

# 2) Mit echten Daten:
$env:QUANTDATA_API_KEY = "DEIN_KEY"          # nur für diese Sitzung
# dauerhaft: setx QUANTDATA_API_KEY "DEIN_KEY"  (danach Terminal neu öffnen)
dotnet run
```

Als einzelne `.exe` veröffentlichen:

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o C:\QuantDataFlow\app
C:\QuantDataFlow\app\QuantDataFlowBridge.exe
```

Beenden mit `Strg+C`.

## Konfiguration (`appsettings.json`)

| Bereich    | Schlüssel              | Bedeutung |
|------------|------------------------|-----------|
| `Api`      | `BaseUrl`, `FlowPath`  | Basis-URL und Pfad des Flow-Endpunkts laut Quant-Data-Doku |
|            | `TickerQueryParam`     | Name des Query-Parameters für den Ticker |
|            | `ApiKeyHeader`, `ApiKeyPrefix` | z. B. `Authorization` + `Bearer `, oder `X-API-Key` + leer |
|            | `DataArrayProperty`    | Name des Arrays in der Antwort (`{"data":[...]}`); leer, wenn die Antwort direkt ein Array ist |
|            | `PollSeconds`          | Abfrageintervall (Rate Limit beachten) |
| `FieldMap` | `Time`, `Strike`, …    | JSON-Feldnamen der API-Antwort → interne Felder |
| `Filter`   | `Tickers`, `MinPremium`| Welche Underlyings, ab welcher Prämie |
| `Output`   | `Directory`            | Zielordner für CSV/JSON |
|            | `HttpPort`             | Port des lokalen Endpunkts (`0` = aus) |

**Bevor du mit echten Daten startest:** `BaseUrl`, `FlowPath`, die Header und `FieldMap` mit der
Quant-Data-API-Dokumentation abgleichen. Die Werte in der Datei sind Platzhalter. Zeitstempel
werden als ISO-8601 oder Unix-Zeit (s/ms) erkannt. Fehlt `premium`, wird `price × size × 100`
gerechnet. Fehlt eine `id`, wird aus Zeit, Strike, Typ, Größe und Preis ein Schlüssel zur
Duplikaterkennung gebildet.

## Ausgabeformat

CSV (Komma-getrennt, Punkt als Dezimaltrennzeichen, Zeit in UTC):

```
time_utc,unix_ms,ticker,expiry,strike,put_call,side,size,price,premium,underlying
2026-10-09 14:31:05,1791556265000,SPY,2026-10-17,580,CALL,ASK,1200,2.15,258000,576.3
```

HTTP:

- `GET http://localhost:5005/flow?ticker=SPY` → JSON-Array
- `GET http://localhost:5005/flow.csv?ticker=SPY&since=1791556265000` → nur Einträge nach diesem Zeitpunkt
- `GET http://localhost:5005/health` → `ok`
