using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using GuitarCam.Audio.Dsp;

namespace GuitarCam.App.Controls;

/// <summary>
/// The tuner's face (native only): the nearest note big, a cents scale from −50 to +50 with the needle, and the
/// frequency. In tune (within <see cref="InTuneCents"/>) the note and the needle turn to the accent. Readings are
/// smoothed while the note stays the same and the last note is held a moment after the string stops, so the needle
/// doesn't jump; it redraws only when what it shows changed.
/// </summary>
public sealed class TunerView : Control
{
    public const double InTuneCents = 3;
    /// <summary>How long the last note stays after the signal goes, in ticks of 16 ms.</summary>
    const int HoldTicks = 40;
    /// <summary>Share of the new reading per tick: the needle settles in about 100 ms.</summary>
    const double Ease = 0.35;

    int midi = -1;
    double cents, hz;
    int hold;
    (int Midi, int Cents) shown = (-2, 0);

    public TunerView()
    {
        Width = 260;
        Height = 104;
    }

    /// <summary>Takes the latest reading (null = no clear note) from the UI tick.</summary>
    public void Push(TunerReading? r)
    {
        if (r is { } v)
        {
            if (v.Midi == midi) (cents, hz) = (cents + (v.Cents - cents) * Ease, hz + (v.Hz - hz) * Ease);
            else (midi, cents, hz) = (v.Midi, v.Cents, v.Hz);
            hold = HoldTicks;
        }
        else if (hold > 0 && --hold == 0) midi = -1;
        var now = (midi, (int)Math.Round(cents * 2));
        if (now == shown) return;
        shown = now;
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        bool on = midi >= 0;
        var reading = new TunerReading(hz, Math.Max(0, midi), cents);
        bool inTune = on && Math.Abs(cents) <= InTuneCents;
        var noteBrush = !on ? Palette.Faint : inTune ? Palette.Accent : Palette.Fg;

        var note = Palette.Text(on ? reading.Note : "—", 34, noteBrush, Palette.UiBold);
        double nx = (w - note.Width) / 2;
        ctx.DrawText(note, new Point(nx, 0));
        if (on) ctx.DrawText(Palette.Text($"{reading.Octave}", 12, Palette.Dim, Palette.Mono), new Point(nx + note.Width + 2, 26));

        // scale: −50 … +50 cents
        double y = 62, x0 = 14, x1 = w - 14, mid = (x0 + x1) / 2;
        ctx.DrawLine(Palette.Pen(Palette.Line), new Point(x0, y + 0.5), new Point(x1, y + 0.5));
        foreach (int c in new[] { -50, -25, 0, 25, 50 })
        {
            double x = Math.Round(mid + c / 50.0 * (x1 - x0) / 2) + 0.5;
            ctx.DrawLine(Palette.Pen(c == 0 ? Palette.Accent : Palette.Faint), new Point(x, y - (c == 0 ? 9 : 5)), new Point(x, y + (c == 0 ? 9 : 5)));
        }
        if (on)
        {
            double x = mid + Math.Clamp(cents, -50, 50) / 50 * (x1 - x0) / 2;
            ctx.DrawRectangle(inTune ? Palette.Accent : Palette.Fg, null, new Rect(x - 2, y - 12, 4, 24), 2, 2);
        }

        var left = Palette.Text(on ? $"{cents:+0;-0;0} c" : Loc.T("tuner.play"), 11, Palette.Dim, on ? Palette.Mono : Palette.Ui);
        ctx.DrawText(left, new Point(x0, h - left.Height));
        if (!on) return;
        var right = Palette.Text($"{hz:0.0} Hz", 11, Palette.Dim, Palette.Mono);
        ctx.DrawText(right, new Point(x1 - right.Width, h - right.Height));
    }
}
