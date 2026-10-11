# Deepchart API Dump

Schreibt die öffentliche Schnittstelle von `VolSysAPI.dll` und `VolumetricaCore.dll` (Typen, Methoden,
Eigenschaften, Enum-Werte, Attribute wie `[DesktopOnly]`) in eine Textdatei. Die DLLs werden nur als
Metadaten gelesen, es wird kein Deepchart-Code ausgeführt. Die Ausgabe zeigt dasselbe wie die
Autovervollständigung in Visual Studio.

```powershell
cd DeepchartApiDump
dotnet run
# Deepchart in anderem Ordner:
dotnet run -- --dir "D:\Apps\Deepchart"
```

Ergebnis: `Dokumente\deepchart_api.txt`. Standardmäßig enthält sie nur die Namespaces `VolSysAPI`,
`VolumetricaAPI` und `VolumetricaControls`. Mit `--all` werden alle Namespaces ausgegeben.
