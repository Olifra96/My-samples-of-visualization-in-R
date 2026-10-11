// ENTWURF – noch nicht ins Deepchart-Projekt kopieren.
// Alle Stellen mit "SDK-OFFEN" hängen von Teilen der Deepchart-API ab, die noch nicht dokumentiert
// vorliegen (Erzeugen von Annotations/Gruppen, Zugriff auf die Bars, genaue Override-Signaturen,
// Name der Kreisform). Alles andere nutzt nur dokumentierte Member.
//
// Darstellung: SPX-Optionsprints (QuantData, über die lokale Bridge) als Bubbles auf dem NQ-Chart.
//   X = Bar, in die der Trade-Zeitpunkt fällt
//   Y = NQ-Schlusskurs dieser Bar (Variante a, keine SPX->NQ-Umrechnung)
//   Text = Prämie verkürzt (1.2M), Farbe: Call grün, Put rot
#nullable enable
using System;
using System.Collections.Generic;
using VolSysAPI.Indicators;
using VolumetricaAPI.Chart;
using VolumetricaControls;

namespace DeepchartSpxFlow;

public class SpxFlowBubbles : Indicator
{
    public static IndicatorDescriptionBase Register() =>
        new IndicatorDescriptionBase { Name = "QuantData - SPX Flow Bubbles" };

    // ---------- Parameter (Einstellungsfenster) ----------

    [VolCustom(CategoryIndex = 0, PropertyIndex = 0, MinValue = 0, IncrementValue = 50000)]
    public double MinPremium { get; set; } = 250_000;

    [VolCustom(CategoryIndex = 0, PropertyIndex = 1, MinValue = 0, IncrementValue = 500000)]
    public double PremiumForMaxSize { get; set; } = 5_000_000;

    [VolCustom(CategoryIndex = 0, PropertyIndex = 2)]
    public bool SizeByPremium { get; set; } = true;

    [VolCustom(CategoryIndex = 0, PropertyIndex = 3, MinValue = 6, MaxValue = 80)]
    public int MinSizePx { get; set; } = 22;

    [VolCustom(CategoryIndex = 0, PropertyIndex = 4, MinValue = 6, MaxValue = 120)]
    public int MaxSizePx { get; set; } = 48;

    [VolCustom(CategoryIndex = 1, PropertyIndex = 0)]
    public ColorRef CallColor { get; set; } = ColorRef.FromRgb(0, 170, 80);

    [VolCustom(CategoryIndex = 1, PropertyIndex = 1)]
    public ColorRef PutColor { get; set; } = ColorRef.FromRgb(220, 40, 40);

    [VolCustom(CategoryIndex = 1, PropertyIndex = 2, MinValue = 0, MaxValue = 255)]
    public int FillOpacity { get; set; } = 170;

    [VolCustom(CategoryIndex = 2, PropertyIndex = 0)]
    public string BridgeUrl { get; set; } = "http://localhost:5005";

    [VolCustom(CategoryIndex = 2, PropertyIndex = 1)]
    public string Ticker { get; set; } = "SPX";

    // ---------- Zustand ----------

    private SpxFlowFeed? _feed;
    private IAnnGroup? _group;
    private readonly Dictionary<string, (IAnnotation Ann, int BarIndex)> _drawn = new();
    private string _drawnWith = "";   // Parameterstand der gezeichneten Bubbles

    public override void OnSet(bool setDefault, bool themeOverride)
    {
        OnOpenCall = OnCloseCall = CallHandler.HistRT;
        OnTickCall = CallHandler.RT;                                  // SDK-OFFEN: Name der Eigenschaft
    }

    // SDK-OFFEN: genaue Signaturen von OnClose / OnTick / Aufräum-Methode.
    public override void OnClose() => Sync();
    public override void OnTick() => Sync();
    public override void OnDispose()
    {
        _feed?.Dispose();
        _feed = null;
    }

    // Läuft im Chart-Thread. Liest nur den Cache des Feeds, wartet nie auf das Netzwerk.
    private void Sync()
    {
        if (_feed == null)
        {
            _feed = new SpxFlowFeed(BridgeUrl, Ticker);
            _feed.Start();
        }
        _group ??= CreateGroup();

        // Geänderte Parameter: alles neu zeichnen.
        var state = $"{MinPremium}|{PremiumForMaxSize}|{SizeByPremium}|{MinSizePx}|{MaxSizePx}|{CallColor}|{PutColor}|{FillOpacity}";
        if (state != _drawnWith)
        {
            _group.Clear();
            _drawn.Clear();
            _drawnWith = state;
        }

        var lastIndex = BarCount - 1;
        if (lastIndex < 0) return;
        var firstBar = GetBar(0);
        var lastBar = GetBar(lastIndex);

        foreach (var b in _feed.Snapshot((decimal)MinPremium))
        {
            var tradeLocal = b.TradeTimeUtc.ToLocalTime();
            if (tradeLocal < firstBar.dt) continue;

            if (_drawn.TryGetValue(b.Key, out var existing))
            {
                // Bubble in der laufenden Bar: Y folgt dem aktuellen Kurs, bis die Bar abgeschlossen ist.
                if (existing.BarIndex == lastIndex) existing.Ann.Y = GetBar(lastIndex).Close;
                continue;
            }

            var index = FindBarIndex(tradeLocal, lastIndex);
            if (index < 0) continue;
            var bar = GetBar(index);

            var ann = CreateBubble();
            ann.CoordinateXType = CoordinateTypeEnum.Absolute;          // Bar i liegt bei X = i + 1
            ann.CoordinateYType = CoordinateTypeEnum.Absolute;
            ann.X = index + 1;
            ann.Y = bar.Close;

            var size = SizeByPremium
                ? BubbleSize.Radius(b.Premium, (decimal)MinPremium, (decimal)PremiumForMaxSize, MinSizePx, MaxSizePx)
                : MinSizePx;
            ann.Width = size;                                           // SDK-OFFEN: Einheit (Pixel?)
            ann.Height = size;

            var color = b.IsCall ? CallColor : PutColor;
            ann.BackColor = color.WithOpacity((byte)FillOpacity);
            ann.LineColor = color;
            ann.LineWidth = b.IsGoldenSweep == true ? 3 : 1;
            ann.Text = b.Label;
            ann.ForeColor = ColorRef.White;
            ann.FontBold = true;
            ann.Tooltip = $"{Ticker} {b.PutCall} {b.Strike} {b.Expiration}\n" +
                          $"Prämie {b.Premium:N0} $  {b.ConsolidationType}\n" +
                          $"Trade {tradeLocal:HH:mm:ss}  Aggressor (Reader) {b.Aggressor}";
            ann.Area = ChartAreaRef.Main;
            ann.PlotForeground = true;
            ann.Visible = true;

            _drawn[b.Key] = (ann, index);
        }
    }

    // Binäre Suche: letzte Bar, deren Startzeit <= Trade-Zeit ist.
    // SDK-OFFEN: ob BarClass.dt Start- oder Endzeit der Bar ist.
    private int FindBarIndex(DateTime tradeLocal, int lastIndex)
    {
        int lo = 0, hi = lastIndex, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (GetBar(mid).dt <= tradeLocal) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found;
    }

    // ---------- SDK-OFFEN: diese vier Stellen anhand der Doku/Beispiele ersetzen ----------

    private int BarCount => VAn.BarCount;                          // SDK-OFFEN
    private BarClass GetBar(int index) => VAn.GetBar(index);       // SDK-OFFEN

    private IAnnGroup CreateGroup()                                 // SDK-OFFEN
    {
        var g = CreateAnnGroup();
        g.Area = ChartAreaRef.Main;
        g.PlotForeground = true;
        return g;
    }

    private IAnnotation CreateBubble()                              // SDK-OFFEN
    {
        var a = CreateAnnotation(AnnotationTypeEnum.Ellipse);       // SDK-OFFEN: Name der Kreisform
        _group!.AnnItems.Add(a);
        return a;
    }
}
