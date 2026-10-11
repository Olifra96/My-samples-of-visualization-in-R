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
| `SpxFlowBubbles.cs`: Indikator-Klasse | **Entwurf**: Logik fertig, Stellen mit `SDK-OFFEN` noch nicht kompilierbar |

## Positionierung (Variante a)

- Die Bubble sitzt auf der Bar, in die der Trade-Zeitpunkt fällt (Binärsuche über `BarClass.dt`).
- Die Höhe ist der NQ-**Schlusskurs** dieser Bar. Die Genauigkeit entspricht damit der Bar-Auflösung
  (Minuten-Chart: Kurs am Ende der Minute), nicht dem exakten Tick zum Trade-Zeitpunkt.
- Fällt der Trade in die noch laufende Bar, folgt die Bubble dem aktuellen Kurs, bis die Bar
  abgeschlossen ist.
- Wegen des Pollings (QuantData → Bridge → Indikator) erscheint eine Bubble einige Sekunden nach dem
  Trade. Sie wird aber an der richtigen Zeit-Position gezeichnet.

## Einbau (später)

1. `SpxFlowFeed.cs` nach `src/` des Deepchart-Indikatorprojekts kopieren.
2. Die Indikator-Klasse `SpxFlowBubbles.cs` dazulegen (folgt).
3. `dotnet build src/DeepchartIndicators.csproj` ausführen. Die DLL landet in `Documents\Deepchart\Indicators`.
4. Die Bridge muss laufen. Der Indikator muss auf dem **Desktop** berechnet werden, weil der
   Deepchart-Server `localhost` nicht erreicht.
