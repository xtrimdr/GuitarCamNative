using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace GuitarCam.App.Controls;

/// <summary>
/// A mixer strip's meter (from the web's Meter in Mixer.tsx, completed in the native app): two bars, left and right,
/// from -48 to +3 dBFS over the green → yellow → red gradient, each solid up to its RMS and dimmed from there up to its
/// peak (with a thin line on the peak), and a dB scale beside them that only shows the marks that fit. A
/// <see cref="Mono"/> source (a track whose takes are mono) shows one bar as wide as the pair, with the louder side,
/// so every strip keeps its width and the faders stay level. Over the bars: the peak-hold readout (the highest peak
/// of either side since the last reset, in dBFS) and a clip light per bar, which latches red once its peak passes
/// <see cref="ClipDb"/> (the same threshold as <see cref="StereoMeter"/>'s light). A click anywhere on the meter
/// resets them. The window feeds <see cref="Set"/> (peaks as amplitude with their fall-off, RMS as amplitude already
/// integrated) from its tick, so any strip (track, folder, return, master) can drive one; it redraws only when what
/// it shows changes, and the readout's text is rebuilt only when its tenth of a dB changes.
/// </summary>
public sealed class MixerMeter : Control
{
    /// <summary>In priority order: a mark is shown only if it is at least 9 px from the ones already shown.</summary>
    static readonly int[] ScaleMarks = [0, -12, -24, -6, -36, -18, -3];
    /// <summary>The marks' labels, built once (a render allocates no text).</summary>
    static readonly string[] MarkTexts = ScaleMarks.Select(db => db == 0 ? "0" : (-db).ToString()).ToArray();
    const double ScaleW = 14;
    /// <summary>The readout's row and the clip light over the bar (px); the bar starts under them.</summary>
    const double ReadoutH = 11, LightH = 4, BarTop = ReadoutH + 1 + LightH + 3;
    /// <summary>Peaks above this light the clip light (dBFS), as in <see cref="StereoMeter"/>.</summary>
    public const double ClipDb = -0.3;
    static readonly double ClipAmp = Math.Pow(10, ClipDb / 20);
    /// <summary>Under this (−100 dBFS) the readout says −∞.</summary>
    const double SilentAmp = 1e-5;
    /// <summary>How much of the gradient the stretch between RMS and peak shows.</summary>
    const double PeakOpacity = 0.4;
    static readonly Cursor Hand = new(StandardCursorType.Hand);

    static readonly IBrush Gradient = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Palette.MeterGreen, 0),
            new GradientStop(Palette.MeterGreen, 0.7),
            new GradientStop(Palette.MeterYellow, 0.84),
            new GradientStop(Palette.MeterRed, 0.96),
        },
    }.ToImmutable();

    /// <summary>Each bar's width and the gap between the two (px); a mono bar takes the pair's width.</summary>
    readonly double barW, pairW;
    const double Gap = 1;
    readonly string barTip, holdTip;
    double levelL, levelR, rmsL, rmsR, hold;
    bool mono, clipL, clipR, overHead;
    /// <summary>The readout as shown, in tenths of a dB (int.MinValue = −∞), and its text.</summary>
    int holdTenths = int.MinValue;
    string holdText = "-∞";

    public MixerMeter(bool wide = false)
    {
        barW = wide ? 7 : 5;
        pairW = 2 * barW + Gap;
        Width = pairW + 2 + ScaleW;
        Cursor = Hand;
        barTip = Loc.T("meter.mixer.tip");
        holdTip = Loc.T("meter.mixer.hold.tip");
        ToolTip.SetTip(this, barTip);
    }

    /// <summary>One bar with the louder side instead of two (a mono source); the light is lit if either side clipped.</summary>
    public bool Mono
    {
        set
        {
            if (value == mono) return;
            mono = value;
            InvalidateVisual();
        }
    }

    /// <summary>
    /// What to show: each side's peak (amplitude, with its fall-off; it also feeds the peak hold and the clip lights) and
    /// RMS (amplitude, already integrated).
    /// </summary>
    public void Set(double peakL, double peakR, double rmsL, double rmsR)
    {
        bool dirty = Moved(peakL, levelL) || Moved(peakR, levelR) || Moved(rmsL, this.rmsL) || Moved(rmsR, this.rmsR);
        levelL = peakL; levelR = peakR;
        this.rmsL = rmsL; this.rmsR = rmsR;
        double peak = Math.Max(peakL, peakR);
        if (peak > hold)
        {
            hold = peak;
            int tenths = Tenths(peak);
            if (tenths != holdTenths)
            {
                holdTenths = tenths;
                holdText = tenths == int.MinValue ? "-∞" : $"{tenths / 10.0:+0.0;-0.0;0.0}";
                dirty = true;
            }
        }
        if (peakL >= ClipAmp && !clipL) { clipL = true; dirty = true; }
        if (peakR >= ClipAmp && !clipR) { clipR = true; dirty = true; }
        if (dirty) InvalidateVisual();
    }

    static bool Moved(double now, double was) => Math.Abs(Pct(now) - Pct(was)) >= 0.002;

    /// <summary>Clears the peak hold and the clip light (a click on the meter).</summary>
    public void ResetHold()
    {
        hold = 0;
        holdTenths = int.MinValue;
        holdText = "-∞";
        clipL = clipR = false;
        InvalidateVisual();
    }

    static int Tenths(double amp) => amp <= SilentAmp ? int.MinValue : (int)Math.Round(200 * Math.Log10(amp));

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        bool on = e.GetPosition(this).Y < BarTop;
        if (on == overHead) return;
        overHead = on;
        ToolTip.SetTip(this, on ? holdTip : barTip);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        ResetHold();
        e.Handled = true;
    }

    static double DbToPct(double db) => (db + 48) / 51;
    static double Pct(double amp) => amp <= 0.0001 ? 0 : Math.Clamp(DbToPct(20 * Math.Log10(amp)), 0, 1);

    public override void Render(DrawingContext ctx)
    {
        // a transparent backdrop so the whole meter (readout included) takes the click
        ctx.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        var t = Palette.Text(holdText, 8, clipL || clipR ? Palette.Rec : Palette.Dim, Palette.Mono);
        ctx.DrawText(t, new Point((Bounds.Width - t.Width) / 2, 0));

        double h = Bounds.Height - BarTop;
        if (h <= 0) return;
        double Y(int db) => BarTop + h - DbToPct(db) * h;
        // the marks that fit (indices into ScaleMarks), without a list: each is checked against the ones already kept
        Span<int> marks = stackalloc int[ScaleMarks.Length];
        int nMarks = 0;
        for (int i = 0; i < ScaleMarks.Length; i++)
        {
            bool fits = true;
            for (int k = 0; k < nMarks; k++) if (Math.Abs(Y(ScaleMarks[marks[k]]) - Y(ScaleMarks[i])) < 9) { fits = false; break; }
            if (fits) marks[nMarks++] = i;
        }

        if (mono)
            Bar(ctx, 0, pairW, h, Math.Max(levelL, levelR), Math.Max(rmsL, rmsR), clipL || clipR, marks[..nMarks]);
        else
        {
            Bar(ctx, 0, barW, h, levelL, rmsL, clipL, marks[..nMarks]);
            Bar(ctx, barW + Gap, barW, h, levelR, rmsR, clipR, marks[..nMarks]);
        }
        for (int k = 0; k < nMarks; k++)
        {
            int db = ScaleMarks[marks[k]];
            var m = Palette.Text(MarkTexts[marks[k]], 8, db == 0 ? Palette.Dim : Palette.Faint, Palette.Mono);
            ctx.DrawText(m, new Point(pairW + 2, Y(db) - m.Height / 2));
        }
    }

    /// <summary>One bar at <paramref name="x"/>: its clip light, the RMS solid, the stretch up to the peak dimmed, the marks.</summary>
    void Bar(DrawingContext ctx, double x, double w, double h, double peak, double rms, bool clip, ReadOnlySpan<int> marks)
    {
        ctx.DrawRectangle(clip ? Palette.Rec : Palette.Raised, null, new Rect(x, ReadoutH + 1, w, LightH), 2, 2);
        var bar = new Rect(x, BarTop, w, h);
        double peakPct = Pct(peak), rmsPct = Math.Min(Pct(rms), peakPct);
        using (ctx.PushClip(new RoundedRect(bar, 2)))
        {
            ctx.FillRectangle(Palette.Raised, bar);
            // the gradient is laid over the whole bar and clipped to each stretch, so its colors stay where the dB are
            double yPeak = BarTop + h * (1 - peakPct), yRms = BarTop + h * (1 - rmsPct);
            if (peakPct > rmsPct)
            {
                using (ctx.PushClip(new Rect(x, yPeak, w, yRms - yPeak)))
                using (ctx.PushOpacity(PeakOpacity))
                    ctx.FillRectangle(Gradient, bar);
                using (ctx.PushClip(new Rect(x, yPeak, w, 1.5)))
                    ctx.FillRectangle(Gradient, bar);
            }
            if (rmsPct > 0)
                using (ctx.PushClip(new Rect(x, yRms, w, BarTop + h - yRms)))
                    ctx.FillRectangle(Gradient, bar);
            foreach (int i in marks)
            {
                int db = ScaleMarks[i];
                double y = Math.Round(BarTop + h - DbToPct(db) * h) + 0.5;
                ctx.DrawLine(Palette.Pen(Palette.Alpha(db == 0 ? Palette.FgColor : Palette.AppColor, db == 0 ? 0.4 : 0.6)), new Point(x, y), new Point(x + w, y));
            }
        }
    }
}
