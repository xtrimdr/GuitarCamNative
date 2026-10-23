using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace GuitarCam.App.Controls;

/// <summary>
/// Port of Meter in GuitarCam/src/components/Mixer.tsx: one peak bar from -48 to +3 dBFS over the web's
/// green → yellow → red gradient, with a dB scale beside it that only shows the marks that fit.
/// The window feeds <see cref="Level"/> (amplitude, already with its fall-off) from its tick.
/// </summary>
public sealed class MixerMeter : Control
{
    /// <summary>In priority order: a mark is shown only if it is at least 9 px from the ones already shown.</summary>
    static readonly int[] ScaleMarks = [0, -12, -24, -6, -36, -18, -3];
    const double ScaleW = 14;

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
    double level;

    public MixerMeter(bool wide = false)
    {
        barW = wide ? 12 : 8;
        Width = barW + 2 + ScaleW;
        ToolTip.SetTip(this, "Nivel de pico (dBFS)");
    }

    public double Level
    {
        set
        {
            double v = Pct(value);
            if (Math.Abs(v - Pct(level)) < 0.002) { level = value; return; }
            level = value;
            InvalidateVisual();
        }
    }

    static double DbToPct(double db) => (db + 48) / 51;
    static double Pct(double amp) => amp <= 0.0001 ? 0 : Math.Clamp(DbToPct(20 * Math.Log10(amp)), 0, 1);

    public override void Render(DrawingContext ctx)
    {
        double h = Bounds.Height;
        if (h <= 0) return;
        double Y(int db) => h - DbToPct(db) * h;
        var marks = new List<int>();
        foreach (int db in ScaleMarks)
            if (marks.All(m => Math.Abs(Y(m) - Y(db)) >= 9)) marks.Add(db);

        var bar = new Rect(0, 0, barW, h);
        using (ctx.PushClip(new RoundedRect(bar, 2)))
        {
            ctx.FillRectangle(Gradient, bar);
            // cover from the top down to the level
            ctx.FillRectangle(Palette.Raised, new Rect(0, 0, barW, h * (1 - Pct(level))));
            foreach (int db in marks)
            {
                double y = Math.Round(Y(db)) + 0.5;
                ctx.DrawLine(Palette.Pen(Palette.Alpha(db == 0 ? Palette.FgColor : Palette.AppColor, db == 0 ? 0.4 : 0.6)), new Point(0, y), new Point(barW, y));
            }
        }
        foreach (int db in marks)
        {
            var t = Palette.Text(db == 0 ? "0" : (-db).ToString(), 8, db == 0 ? Palette.Dim : Palette.Faint, Palette.Mono);
            ctx.DrawText(t, new Point(barW + 2, Y(db) - t.Height / 2));
        }
    }
}
