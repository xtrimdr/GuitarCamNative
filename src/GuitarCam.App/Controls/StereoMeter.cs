using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace GuitarCam.App.Controls;

/// <summary>
/// Horizontal stereo meter like the web app's StereoMeter: fast attack, 24 dB/s release, 1.5 s peak hold
/// and a clip light that stays on until clicked.
/// </summary>
public sealed class StereoMeter : Control
{
    const double MinDb = -60;
    readonly double[] level = [MinDb, MinDb];
    readonly double[] hold = [MinDb, MinDb];
    readonly double[] holdAge = [0, 0];
    bool clip;
    DateTime last = DateTime.Now;

    public StereoMeter()
    {
        Height = 14;
        Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
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
        clip = false;
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width - 10, h = (Bounds.Height - 2) / 2;
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
        ctx.DrawRectangle(clip ? Palette.Rec : Palette.Raised, null, new Rect(w + 3, 0, 7, Bounds.Height), 2, 2);
    }
}
