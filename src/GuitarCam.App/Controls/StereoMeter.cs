using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace GuitarCam.App.Controls;

/// <summary>
/// Horizontal stereo meter like the web app's StereoMeter: fast attack, 24 dB/s release, 1.5 s peak hold
/// and a clip light that stays on until clicked. A click anywhere else raises <see cref="OpenRequested"/> (the web's
/// meter wrapper opens Configuración → Entradas); with nobody listening it has no tooltip and no hand cursor there.
/// Native: <see cref="ShowScale"/> adds a small dB scale under the bars (the transport bar's input, asked by the user).
/// </summary>
public sealed class StereoMeter : Control
{
    const double MinDb = -60;
    readonly double[] level = [MinDb, MinDb];
    readonly double[] hold = [MinDb, MinDb];
    readonly double[] holdAge = [0, 0];
    bool clip;
    DateTime last = DateTime.Now;

    const double LightW = 7;
    /// <summary>With the scale: the two bars take this much and the ticks and numbers the rest.</summary>
    const double ScaledBarsH = 10, ScaleH = 11;
    static readonly int[] ScaleDb = [-48, -36, -24, -12, -6, 0];
    bool showScale;

    /// <summary>Ticks and numbers in dB under the bars (−60 to 0, the meter's range).</summary>
    public bool ShowScale
    {
        get => showScale;
        set { showScale = value; Height = value ? ScaledBarsH + ScaleH : 14; InvalidateVisual(); }
    }
    const string ClipTip = "Saturación: se prende si la entrada llega al máximo (bajá la ganancia). Click para apagarlo.";
    const string OpenTip = "Configurar entradas y salidas";
    static readonly Avalonia.Input.Cursor Hand = new(Avalonia.Input.StandardCursorType.Hand);

    Action? openRequested;
    bool overLight;

    /// <summary>A click outside the clip light.</summary>
    public event Action? OpenRequested
    {
        add { openRequested += value; UpdateHover(); }
        remove { openRequested -= value; UpdateHover(); }
    }

    public StereoMeter()
    {
        Height = 14;
        UpdateHover();
    }

    bool OnLight(Point p) => p.X >= Bounds.Width - LightW - 3 && p.Y <= BarsH;

    double BarsH => showScale ? ScaledBarsH : Bounds.Height;

    void UpdateHover()
    {
        bool open = openRequested != null;
        Cursor = overLight || open ? Hand : null;
        ToolTip.SetTip(this, overLight ? ClipTip : open ? OpenTip : null);
    }

    protected override void OnPointerMoved(Avalonia.Input.PointerEventArgs e)
    {
        bool on = OnLight(e.GetPosition(this));
        if (on == overLight) return;
        overLight = on;
        UpdateHover();
    }

    public void Push(float peakL, float peakR)
    {
        var now = DateTime.Now;
        double dt = (now - last).TotalSeconds;
        last = now;
        float[] p = [peakL, peakR];
        for (int c = 0; c < 2; c++)
        {
            double db = Math.Max(MinDb, Palette.ToDb(p[c]));
            level[c] = db > level[c] ? db : Math.Max(db, level[c] - 24 * dt);
            if (db >= hold[c])
            {
                hold[c] = db;
                holdAge[c] = 0;
            }
            else if ((holdAge[c] += dt) > 1.5) hold[c] = Math.Max(level[c], hold[c] - 24 * dt);
            if (db > -0.3) clip = true;
        }
        InvalidateVisual();
    }

    protected override void OnPointerPressed(Avalonia.Input.PointerPressedEventArgs e)
    {
        if (!OnLight(e.GetPosition(this)))
        {
            openRequested?.Invoke();
            return;
        }
        clip = false;
        InvalidateVisual();
        e.Handled = true;
    }

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width - 10, h = (BarsH - 2) / 2;
        for (int c = 0; c < 2; c++)
        {
            var r = new Rect(0, c * (h + 2), w, h);
            ctx.DrawRectangle(Palette.Raised, null, r, 2, 2);
            double k = (level[c] - MinDb) / -MinDb;
            if (k > 0)
            {
                double split = (-6 - MinDb) / -MinDb;
                ctx.DrawRectangle(Palette.Accent, null, new Rect(0, r.Y, w * Math.Min(k, split), h), 2, 2);
                if (k > split) ctx.FillRectangle(Palette.Brush("#f59e0b"), new Rect(w * split, r.Y, w * (k - split), h));
            }
            double hk = (hold[c] - MinDb) / -MinDb;
            if (hk > 0) ctx.FillRectangle(hold[c] > -1 ? Palette.Rec : Palette.Fg, new Rect(w * hk - 1.5, r.Y, 1.5, h));
        }
        ctx.DrawRectangle(clip ? Palette.Rec : Palette.Raised, null, new Rect(w + 3, 0, LightW, BarsH), 2, 2);
        if (!showScale) return;
        var tick = Palette.Pen(Palette.Line);
        foreach (int db in ScaleDb)
        {
            double x = Math.Round(w * (db - MinDb) / -MinDb) - 0.5;
            ctx.DrawLine(tick, new Point(x, BarsH + 1), new Point(x, BarsH + 3));
            var t = Palette.Text(db.ToString(), 8, Palette.Faint, Palette.Mono);
            ctx.DrawText(t, new Point(Math.Clamp(x - t.Width / 2, 0, w - t.Width + 1), BarsH + 2.5));
        }
    }
}
