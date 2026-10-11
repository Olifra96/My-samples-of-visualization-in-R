# QuantData Options Flow Bridge (C#)

Konsolen-App für das **Windows Terminal**: Sie fragt den Options-Flow für NDX, QQQ und SPX per REST
bei QuantData ab, zeigt ihn farbig im Terminal an und stellt ihn als Datei bzw. lokalen
HTTP-Endpunkt bereit, aus dem **DeepCharts** die Daten abholt.

```
QuantData REST (POST, Bearer)
  /v1/options/tool/order-flow/consolidated      (primär)
  /v1/options/tool/order-flow/unconsolidated    (Fallback bei 404/503)
        │  Polling, Pagination über nextSearchAfter → searchAfter
        ▼
QuantDataFlowBridge.exe (Windows Terminal)
  ├─ Live-Anzeige + Statuszeile (OK / PARTIAL / Fallback / SPX nicht geliefert)
  ├─ C:\QuantDataFlow\flow_YYYYMMDD.csv     flache Tabelle (Provider-Spalten + reader_*-Spalten)
  ├─ C:\QuantDataFlow\flow_latest.json      { provider: <Rohdatensatz>, reader: {...} }
  ├─ C:\QuantDataFlow\status.json           Status je Ticker
  └─ http://localhost:5005/flow | /flow.csv | /status
                     ▲
                     └── DeepCharts
```

> Ein anderes Programm kann nicht direkt aus dem *Terminal-Fenster* lesen. Das Terminal dient nur
> zur Anzeige. DeepCharts greift auf die Dateien oder den lokalen Endpunkt zu.

## Starten

Voraussetzungen: Windows 10/11, [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0),
QuantData-API-Abo (ggf. mit unterschriebener OPRA-Vereinbarung).

```powershell
cd QuantDataFlowBridge
dotnet run -- --demo                 # Test mit Zufallsdaten, ohne API

$env:QUANTDATA_API_KEY = "…"         # nur für diese Terminal-Sitzung
dotnet run                           # echte Daten
```

Den Schlüssel nur als Umgebungsvariable setzen, nie in `appsettings.json`, Git oder Chats.
Die App gibt ihn nicht aus und protokolliert ihn nicht.

Als `.exe` veröffentlichen:

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o C:\QuantDataFlow\app
```

## Abruflogik

- **Polling**, kein Live-Stream: Abfrage alle `PollSeconds` (Standard 10 s).
- **Catch-up:** ab dem letzten gespeicherten `tradeTime` je Ticker (`checkpoints.json`) mit
  `OverlapSeconds` Überlappung, aufsteigend sortiert, höchstens `MaxCatchUpPages` (8) Seiten pro Zyklus.
  Ohne Checkpoint wird höchstens `MaxLookbackHours` (24 h) zurückgeschaut. Das ist kein Import der
  gesamten Historie.
- **PARTIAL:** Sind nach 8 Seiten noch Daten übrig, wird der Status PARTIAL gesetzt und zusätzlich
  die neueste Seite abgefragt, damit aktuelle Prints sofort sichtbar sind.
- **Fallback:** Antwortet `consolidated` mit 404 oder 503, wird für `FallbackMinutes` der
  unkonsolidierte Endpunkt verwendet (ohne `includeComprisingTrades`). Diese Zeilen sind mit `RAW` markiert.
- **SPX** ist optional: Wird SPX nicht geliefert, erscheint ein Hinweis, und NDX und QQQ laufen weiter.
- **429:** Die App wartet die Zeit aus `Retry-After` ab (sonst `DefaultRetryAfterSeconds`).
  **401/403:** Die App beendet sich mit einem Hinweis auf Key, Abo bzw. OPRA.
- **Duplikate** (durch Überlappung oder Seiten) werden über die Provider-`id` erkannt. Fehlt sie,
  wird ein Ersatzschlüssel aus Zeit, OSI, Größe und Preis gebildet.

## Provider-Daten vs. Reader-Ableitungen

| Herkunft | Felder |
|---|---|
| **Provider (QuantData)**, unverändert, fehlend = leer/null | alle CSV-Spalten ohne Präfix; in JSON das Objekt `provider` (vollständiger Rohdatensatz inkl. `comprisingTrades`) |
| **Reader (diese App)** | `reader_aggressor` + `reader_aggressor_confidence` (aus `tradeSideCode` über `Reader.TradeSideCodeMap`), `reader_comprising_trades` (Anzahl), `reader_endpoint`, `reader_received_utc` (Empfangszeit ≠ `tradeTime`) |

`reader_aggressor` beschreibt nur die Ausführung relativ zu Bid/Ask. Das ist **nicht** Opening/Closing
und **nicht** bullish/bearish. Ist ein Code nicht in der Zuordnung eingetragen, bleibt der Wert `UNKNOWN`.

## Noch zu bestätigen (aus `app/api/flow/route.ts`, `lib/flow.ts` bzw. QuantData-Doku)

1. **`Api.RequestBodyTemplate`**: genaue Form von `timeRange`, Ticker-Filter, Sortierung und
   Seitengröße. Die Vorlage ist eine Annahme. Platzhalter: `$ticker $fromMs $toMs $fromIso $toIso
   $pageSize $sortDirection`.
2. **`FieldMap`**: Bestätigt sind `tradeTime`, `tradeSideCode`, `tradeType`, `tradeConsolidationType`,
   `isGoldenSweep`, `comprisingTrades`, `premium` und `size`. Die übrigen Feldnamen sind Annahmen.
3. **`Reader.TradeSideCodeMap`**: die tatsächlichen `tradeSideCode`-Werte und ihre Einstufung.
4. **DeepCharts:** welches Format bzw. welcher Pfad eingelesen wird.

## HTTP

- `GET /flow?ticker=NDX&since=<tradeTime ms>` → JSON (`provider`/`reader`)
- `GET /flow.csv?ticker=QQQ` → CSV
- `GET /status` → Status je Ticker plus `generatedAt`
- `GET /health` → `ok`
