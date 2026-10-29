using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using WildTrack.Audio.Dsp;

namespace WildTrack.App.Controls;

/// <summary>
/// The master's multiband at a glance (asked by the user, in place of the crossovers' two knobs): the three bands'
/// curves over 20 Hz–20 kHz as the Linkwitz-Riley split leaves them (<see cref="Crossover3"/>), each sinking by the
/// reduction its compressor is doing now, and the two crossovers as lines you drag sideways (Shift = fine, double click
/// = back to its default). The curves are built only when a crossover moves; the live reduction just moves each one
/// down (a pushed transform), so a frame allocates nothing. The window feeds the reduction and the crossovers
/// (<see cref="SetCrossovers"/>, <see cref="SetReduction"/>) and decides what a drag does (<see cref="CrossoverDragged"/>).
/// </summary>
public sealed class MasterBandsGraph : Control
{
    const double FMin = 20, FMax = 20000;
    /// <summary>What the graph shows from the top down (dB): a bit over 0, and the deepest reduction worth seeing.</summary>
    const double DbTop = 3, DbBottom = -15;
    /// <summary>How close (px) to a crossover line a press grabs it.</summary>
    const double Grab = 7;
    const int Points = 72;
    static readonly Cursor SideCursor = new(StandardCursorType.SizeWestEast);

    double lowHz = MasterBands.DefaultLowXHz, highHz = MasterBands.DefaultHighXHz;
    readonly double[] gr = new double[MasterBands.Count];
    readonly StreamGeometry?[] lines = new StreamGeometry?[MasterBands.Count], fills = new StreamGeometry?[MasterBands.Count];
    TextLayout? lowText, highText;
    Size built;
    int dragging = -1, hover = -1;
    double downX, downHz;
    bool active = true;

    /// <summary>A crossover moved: false = the low one, true = the high one, and its new frequency.</summary>
    public event Action<bool, double>? CrossoverDragged;

    public MasterBandsGraph()
    {
        Height = 72;
        ClipToBounds = true;
    }

    /// <summary>Whether the multiband is working (dimmed when not).</summary>
    public bool Active
    {
        get => active;
        set { if (active == value) return; active = value; InvalidateVisual(); }
    }

    public void SetCrossovers(double low, double high)
    {
        if (low == lowHz && high == highHz && lowText != null) return;
        lowHz = low;
        highHz = high;
        lowText = Palette.Text($"{Hz(low)}", 9, Palette.Dim, Palette.Mono);
        highText = Palette.Text($"{Hz(high)}", 9, Palette.Dim, Palette.Mono);
        built = default;
        InvalidateVisual();
    }

    /// <summary>The band's reduction as the meter shows it (dB, ≥ 0); redraws only when it moved visibly.</summary>
    public void SetReduction(int band, double db)
    {
        db = Math.Clamp(db, 0, DbTop - DbBottom);
        if (Math.Abs(db - gr[band]) < 0.05) return;
        gr[band] = db;
        InvalidateVisual();
    }

    static string Hz(double f) => f >= 1000 ? $"{f / 1000:0.#}k" : $"{f:0}";

    double X(double f) => Math.Log(f / FMin) / Math.Log(FMax / FMin) * Bounds.Width;
    double F(double x) => FMin * Math.Pow(FMax / FMin, Math.Clamp(x / Math.Max(1, Bounds.Width), 0, 1));
    double Y(double db) => (DbTop - db) / (DbTop - DbBottom) * Bounds.Height;

    /// <summary>The band's magnitude (dB) at <paramref name="f"/>: LR4 low pass, band pass or high pass.</summary>
    double BandDb(int band, double f)
    {
        static double Lp(double f, double fc) { double r = Math.Pow(f / fc, 4); return 1 / (1 + r); }
        static double Hp(double f, double fc) { double r = Math.Pow(f / fc, 4); return r / (1 + r); }
        double m = band switch { 0 => Lp(f, lowHz), 1 => Hp(f, lowHz) * Lp(f, highHz), _ => Hp(f, highHz) };
        return 20 * Math.Log10(Math.Max(1e-6, m));
    }

    void Build()
    {
        built = Bounds.Size;
        double bottom = Bounds.Height + Y(DbBottom) + 2; // under the bottom even when a band sinks
        for (int b = 0; b < MasterBands.Count; b++)
        {
            var line = new StreamGeometry();
            var fill = new StreamGeometry();
            using (var lc = line.Open())
            using (var fc = fill.Open())
            {
                fc.BeginFigure(new Point(0, bottom), true);
                for (int i = 0; i <= Points; i++)
                {
                    double x = Bounds.Width * i / Points;
                    var p = new Point(x, Y(Math.Max(DbBottom - 6, BandDb(b, F(x)))));
                    if (i == 0) lc.BeginFigure(p, false);
                    else lc.LineTo(p);
                    fc.LineTo(p);
                }
                fc.LineTo(new Point(Bounds.Width, bottom));
                fc.EndFigure(true);
                lc.EndFigure(false);
            }
            lines[b] = line;
            fills[b] = fill;
        }
    }

    public override void Render(DrawingContext ctx)
    {
        var size = Bounds.Size;
        if (size.Width < 10 || size.Height < 10) return;
        if (built != size) Build();
        ctx.DrawRectangle(Palette.App, Palette.Pen(Palette.Line), new Rect(size), 6, 6);
        foreach (double f in (ReadOnlySpan<double>)[100, 1000, 10000])
            ctx.DrawLine(Palette.Pen(Palette.Line), new Point(Math.Round(X(f)) + 0.5, 0), new Point(Math.Round(X(f)) + 0.5, size.Height));
        double y0 = Math.Round(Y(0)) + 0.5;
        ctx.DrawLine(Palette.Pen(Palette.Line), new Point(0, y0), new Point(size.Width, y0));

        double alpha = active ? 1 : 0.4;
        using (ctx.PushClip(new RoundedRect(new Rect(size), 6)))
        {
            for (int b = 0; b < MasterBands.Count; b++)
            {
                double dy = Y(-gr[b]) - Y(0);
                using (ctx.PushTransform(Matrix.CreateTranslation(0, dy)))
                {
                    ctx.DrawGeometry(Palette.Alpha(Palette.AccentColor, (b == 1 ? 0.16 : 0.10) * alpha), null, fills[b]!);
                    ctx.DrawGeometry(null, Palette.Pen(active ? Palette.Accent : Palette.Dim, 1.5), lines[b]!);
                }
            }
        }

        // the crossovers: a line each, brighter while grabbed or under the pointer, with its frequency at the top
        for (int i = 0; i < 2; i++)
        {
            double x = Math.Round(X(i == 0 ? lowHz : highHz)) + 0.5;
            bool hot = dragging == i || hover == i;
            ctx.DrawLine(Palette.Pen(hot ? Palette.Fg : Palette.Faint, hot ? 1.5 : 1), new Point(x, 0), new Point(x, size.Height));
            var t = i == 0 ? lowText : highText;
            if (t == null) continue;
            double tx = i == 0 ? x - t.Width - 3 : x + 3;
            ctx.DrawText(t, new Point(Math.Clamp(tx, 3, size.Width - t.Width - 3), 3));
        }
    }

    int Hit(Point p)
    {
        double dl = Math.Abs(p.X - X(lowHz)), dh = Math.Abs(p.X - X(highHz));
        if (Math.Min(dl, dh) > Grab) return -1;
        return dl <= dh ? 0 : 1;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (!IsEnabled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var p = e.GetPosition(this);
        int i = Hit(p);
        if (i < 0) return;
        e.Handled = true;
        if (e.ClickCount == 2)
        {
            CrossoverDragged?.Invoke(i == 1, i == 0 ? MasterBands.DefaultLowXHz : MasterBands.DefaultHighXHz);
            return;
        }
        dragging = i;
        downX = p.X;
        downHz = i == 0 ? lowHz : highHz;
        e.Pointer.Capture(this);
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var p = e.GetPosition(this);
        if (dragging < 0)
        {
            int h = IsEnabled ? Hit(p) : -1;
            if (h != hover)
            {
                hover = h;
                Cursor = h >= 0 ? SideCursor : null;
                InvalidateVisual();
            }
            return;
        }
        double k = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0.25 : 1;
        double f = F(X(downHz) + (p.X - downX) * k);
        f = dragging == 0 ? Math.Clamp(f, MasterBands.LowXMin, MasterBands.LowXMax) : Math.Clamp(f, MasterBands.HighXMin, MasterBands.HighXMax);
        CrossoverDragged?.Invoke(dragging == 1, f);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (dragging < 0) return;
        e.Pointer.Capture(null);
        dragging = -1;
        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        dragging = -1;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        if (hover < 0) return;
        hover = -1;
        Cursor = null;
        InvalidateVisual();
    }
}
