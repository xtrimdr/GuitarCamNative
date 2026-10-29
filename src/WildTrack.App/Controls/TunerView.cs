using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using WildTrack.Audio.Dsp;

namespace WildTrack.App.Controls;

/// <summary>
/// The tuner's face (native only): the nearest note big (named from <c>Loc</c>, so Do Re Mi in Spanish and C D E in
/// English), a cents scale from −50 to +50 with the needle, and the frequency. A triangle at each side of the note
/// says which way to turn the peg (left = flat, tune up; right = sharp, tune down) and blinks faster the further off
/// the string is: the blink is the information, so a glance tells "close" from "way off". In tune (within
/// <see cref="InTuneCents"/>) the note, the needle and an "OK!" turn green (<see cref="Palette.InTune"/>, the mixer
/// meters' green). Readings are smoothed while the note stays the same and the last note is held a moment after the
/// string stops, so the needle doesn't jump. It redraws only when what it shows changed (the blink included), with
/// texts, brushes and pens from <see cref="Palette"/> and its triangles built once: the UI tick allocates nothing.
/// </summary>
public sealed class TunerView : Control
{
    /// <summary>Within this many cents of the note it reads "OK!".</summary>
    public const double InTuneCents = 3;
    /// <summary>How long the last note stays after the signal goes, in ticks of 16 ms.</summary>
    const int HoldTicks = 40;
    /// <summary>Share of the new reading per tick: the needle settles in about 100 ms.</summary>
    const double Ease = 0.35;
    /// <summary>Blinks per second of the turn arrow just outside the tolerance and at 50 cents off (or more).</summary>
    const double SlowBlinkHz = 1.5, FastBlinkHz = 8;

    static string[]? noteNames;

    int midi = -1;
    double cents, hz;
    int hold;
    double phase;
    long lastTicks;
    (int Midi, int Cents, bool InTune, bool Lit) shown = (-2, 0, false, false);
    StreamGeometry? upArrow, downArrow;

    public TunerView()
    {
        Width = 260;
        Height = 108;
    }

    /// <summary>Whether the (smoothed) reading is within <see cref="InTuneCents"/>, as last drawn.</summary>
    public bool InTune => shown.InTune;

    /// <summary>The note's name in the UI language ("Sol#", "G#"), by pitch class 0 = Do / C.</summary>
    public static string NoteName(int pitchClass) =>
        (noteNames ??= [.. Enumerable.Range(0, 12).Select(i => Loc.T($"note.{i}"))])[pitchClass];

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

        long now = Stopwatch.GetTimestamp();
        double dt = lastTicks == 0 ? 0 : Math.Min(0.1, (now - lastTicks) / (double)Stopwatch.Frequency);
        lastTicks = now;
        bool on = midi >= 0, inTune = on && Math.Abs(cents) <= InTuneCents;
        if (on && !inTune)
        {
            double off = Math.Clamp((Math.Abs(cents) - InTuneCents) / (50 - InTuneCents), 0, 1);
            phase += dt * (SlowBlinkHz + (FastBlinkHz - SlowBlinkHz) * off);
            phase -= Math.Floor(phase);
        }
        else phase = 0; // the next miss starts lit
        var state = (midi, (int)Math.Round(cents * 2), inTune, phase < 0.5);
        if (state == shown) return;
        shown = state;
        InvalidateVisual();
    }

    /// <summary>Resets the blink clock when the panel opens, so the first tick doesn't count the time it was closed.</summary>
    public void Restart() => lastTicks = 0;

    static StreamGeometry Triangle(double tipX, double baseX, double cy, double half)
    {
        var g = new StreamGeometry();
        using var c = g.Open();
        c.BeginFigure(new Point(baseX, cy - half), true);
        c.LineTo(new Point(tipX, cy));
        c.LineTo(new Point(baseX, cy + half));
        c.EndFigure(true);
        return g;
    }

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        bool on = midi >= 0;
        var reading = new TunerReading(hz, Math.Max(0, midi), cents);
        bool inTune = on && Math.Abs(cents) <= InTuneCents;
        var noteBrush = !on ? Palette.Faint : inTune ? Palette.InTune : Palette.Fg;

        var note = Palette.Text(on ? NoteName(reading.PitchClass) : "—", 34, noteBrush, Palette.UiBold);
        double nx = (w - note.Width) / 2;
        ctx.DrawText(note, new Point(nx, 0));
        if (on) ctx.DrawText(Palette.Text($"{reading.Octave}", 12, Palette.Dim, Palette.Mono), new Point(nx + note.Width + 2, 26));

        // which way to turn: ▶ at the left when flat (tune up), ◀ at the right when sharp (tune down)
        const double ay = 22, ax = 40;
        upArrow ??= Triangle(ax + 6, ax - 6, ay, 8);
        downArrow ??= Triangle(w - ax - 6, w - ax + 6, ay, 8);
        bool flat = on && !inTune && cents < 0, sharp = on && !inTune && cents > 0;
        bool lit = phase < 0.5;
        ctx.DrawGeometry(flat && lit ? Palette.Accent : Palette.Hover, null, upArrow);
        ctx.DrawGeometry(sharp && lit ? Palette.Accent : Palette.Hover, null, downArrow);
        if (flat || sharp)
        {
            var hint = Palette.Text(Loc.T(flat ? "tuner.up" : "tuner.down"), 11, Palette.Dim);
            ctx.DrawText(hint, new Point((flat ? ax : w - ax) - hint.Width / 2, ay + 12));
        }

        // scale: −50 … +50 cents
        double y = 64, x0 = 14, x1 = w - 14, mid = (x0 + x1) / 2;
        ctx.DrawLine(Palette.Pen(Palette.Line), new Point(x0, y + 0.5), new Point(x1, y + 0.5));
        foreach (int c in Ticks)
        {
            double x = Math.Round(mid + c / 50.0 * (x1 - x0) / 2) + 0.5;
            ctx.DrawLine(Palette.Pen(c == 0 ? (inTune ? Palette.InTune : Palette.Accent) : Palette.Faint), new Point(x, y - (c == 0 ? 9 : 5)), new Point(x, y + (c == 0 ? 9 : 5)));
        }
        if (on)
        {
            double x = mid + Math.Clamp(cents, -50, 50) / 50 * (x1 - x0) / 2;
            ctx.DrawRectangle(inTune ? Palette.InTune : Palette.Fg, null, new Rect(x - 2, y - 12, 4, 24), 2, 2);
        }

        var left = Palette.Text(on ? $"{cents:+0;-0;0} c" : Loc.T("tuner.play"), 11, Palette.Dim, on ? Palette.Mono : Palette.Ui);
        ctx.DrawText(left, new Point(x0, h - left.Height));
        if (!on) return;
        if (inTune)
        {
            var ok = Palette.Text(Loc.T("tuner.ok"), 13, Palette.InTune, Palette.UiBold);
            ctx.DrawText(ok, new Point((w - ok.Width) / 2, h - ok.Height));
        }
        var right = Palette.Text($"{hz:0.0} Hz", 11, Palette.Dim, Palette.Mono);
        ctx.DrawText(right, new Point(x1 - right.Width, h - right.Height));
    }

    static readonly int[] Ticks = [-50, -25, 0, 25, 50];
}
