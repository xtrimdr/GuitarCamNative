using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace GuitarCam.App.Controls;

/// <summary>
/// A mixer strip's meter (from the web's Meter in Mixer.tsx, completed in the native app): a bar from -48 to +3 dBFS
/// over the green → yellow → red gradient, solid up to the RMS and dimmed from there up to the peak (with a thin line
/// on the peak), and a dB scale beside it that only shows the marks that fit. Over the bar: the peak-hold readout
/// (the highest peak since the last reset, in dBFS) and the clip light, which latches red once the peak passes
/// <see cref="ClipDb"/> (the same threshold as <see cref="StereoMeter"/>'s light). A click anywhere on the meter
/// resets both. The window feeds <see cref="Level"/> (amplitude, already with its fall-off) and <see cref="Rms"/>
/// (amplitude, already integrated) from its tick, so any strip (track, folder, return, master) can drive one; it
/// redraws only when what it shows changes, and the readout's text is rebuilt only when its tenth of a dB changes.
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

    readonly double barW;
    readonly string barTip, holdTip;
    double level, rms, hold;
    bool hasRms, clip, overHead;
    /// <summary>The readout as shown, in tenths of a dB (int.MinValue = −∞), and its text.</summary>
    int holdTenths = int.MinValue;
    string holdText = "-∞";

    public MixerMeter(bool wide = false)
    {
        barW = wide ? 12 : 8;
        Width = barW + 2 + ScaleW;
        Cursor = Hand;
        barTip = Loc.T("meter.mixer.tip");
        holdTip = Loc.T("meter.mixer.hold.tip");
        ToolTip.SetTip(this, barTip);
    }

    /// <summary>The peak to show (amplitude, with its fall-off); it also feeds the peak hold and the clip light.</summary>
    public double Level
    {
        set
        {
            bool dirty = Math.Abs(Pct(value) - Pct(level)) >= 0.002;
            level = value;
            if (value > hold)
            {
                hold = value;
                int tenths = Tenths(value);
                if (tenths != holdTenths)
                {
                    holdTenths = tenths;
                    holdText = tenths == int.MinValue ? "-∞" : $"{tenths / 10.0:+0.0;-0.0;0.0}";
                    dirty = true;
                }
            }
            if (value >= ClipAmp && !clip) { clip = true; dirty = true; }
            if (dirty) InvalidateVisual();
        }
    }

    /// <summary>The RMS to show (amplitude, already integrated). Without it the bar shows the peak in full.</summary>
    public double Rms
    {
        set
        {
            hasRms = true;
            if (Math.Abs(Pct(value) - Pct(rms)) < 0.002) { rms = value; return; }
            rms = value;
            InvalidateVisual();
        }
    }

    /// <summary>Clears the peak hold and the clip light (a click on the meter).</summary>
    public void ResetHold()
    {
        hold = 0;
        holdTenths = int.MinValue;
        holdText = "-∞";
        clip = false;
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
        var t = Palette.Text(holdText, 8, clip ? Palette.Rec : Palette.Dim, Palette.Mono);
        ctx.DrawText(t, new Point((Bounds.Width - t.Width) / 2, 0));
        ctx.DrawRectangle(clip ? Palette.Rec : Palette.Raised, null, new Rect(0, ReadoutH + 1, barW, LightH), 2, 2);

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

        var bar = new Rect(0, BarTop, barW, h);
        double peakPct = Pct(level), rmsPct = hasRms ? Math.Min(Pct(rms), peakPct) : peakPct;
        using (ctx.PushClip(new RoundedRect(bar, 2)))
        {
            ctx.FillRectangle(Palette.Raised, bar);
            // the gradient is laid over the whole bar and clipped to each stretch, so its colors stay where the dB are
            double yPeak = BarTop + h * (1 - peakPct), yRms = BarTop + h * (1 - rmsPct);
            if (peakPct > rmsPct)
            {
                using (ctx.PushClip(new Rect(0, yPeak, barW, yRms - yPeak)))
                using (ctx.PushOpacity(PeakOpacity))
                    ctx.FillRectangle(Gradient, bar);
                using (ctx.PushClip(new Rect(0, yPeak, barW, 1.5)))
                    ctx.FillRectangle(Gradient, bar);
            }
            if (rmsPct > 0)
                using (ctx.PushClip(new Rect(0, yRms, barW, BarTop + h - yRms)))
                    ctx.FillRectangle(Gradient, bar);
            for (int k = 0; k < nMarks; k++)
            {
                int db = ScaleMarks[marks[k]];
                double y = Math.Round(Y(db)) + 0.5;
                ctx.DrawLine(Palette.Pen(Palette.Alpha(db == 0 ? Palette.FgColor : Palette.AppColor, db == 0 ? 0.4 : 0.6)), new Point(0, y), new Point(barW, y));
            }
        }
        for (int k = 0; k < nMarks; k++)
        {
            int db = ScaleMarks[marks[k]];
            var m = Palette.Text(MarkTexts[marks[k]], 8, db == 0 ? Palette.Dim : Palette.Faint, Palette.Mono);
            ctx.DrawText(m, new Point(barW + 2, Y(db) - m.Height / 2));
        }
    }
}
