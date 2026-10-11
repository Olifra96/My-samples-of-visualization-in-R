# Deepchart-Indikator „SPX Flow Bubbles“ (in Arbeit)

Ziel: SPX-Optionsflow von QuantData als Bubbles auf dem **NQ-Chart** in Deepchart.

- **Position:** waagerecht am Zeitpunkt des Trades (`tradeTime`), senkrecht am **NQ-Kurs zu diesem
  Zeitpunkt** (Variante a). Es gibt keine Umrechnung von SPX auf NQ.
- **Inhalt:** Prämie verkürzt (`850K`, `1.2M`, `13M`).
- **Farbe:** Call grün, Put rot.
- **Parameter:** Mindest-Prämie (frei einstellbar), Bubble-Größe nach Prämie, Bridge-URL.

```
QuantData ──► QuantDataFlowBridge (Windows Terminal)
                 └─ http://localhost:5005/flow.csv?ticker=SPX&receivedSince=…
                                  ▲
               SpxFlowFeed (Hintergrund-Abruf, Cache, Duplikaterkennung)
                                  ▲
               Indikator-Klasse (VolSysAPI): liest Snapshot(minPremium), zeichnet Bubbles
```

## Stand

| Teil | Status |
|---|---|
| `SpxFlowFeed.cs`: Abruf, CSV-Parsing, Prämienfilter, Beschriftung, Bubble-Radius | fertig und getestet; keine SDK-Abhängigkeit |
| Indikator-Klasse (`Indicator`-Ableitung, Annotations, NQ-Kurs zur Trade-Zeit) | wartet auf ein Beispiel aus dem Deepchart-SDK |

## Einbau (später)

1. `SpxFlowFeed.cs` nach `src/` des Deepchart-Indikatorprojekts kopieren.
2. Die Indikator-Klasse `SpxFlowBubbles.cs` dazulegen (folgt).
3. `dotnet build src/DeepchartIndicators.csproj` ausführen. Die DLL landet in `Documents\Deepchart\Indicators`.
4. Die Bridge muss laufen. Der Indikator muss auf dem **Desktop** berechnet werden, weil der
   Deepchart-Server `localhost` nicht erreicht.
