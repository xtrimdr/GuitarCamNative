using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using WildTrack.App.Model;

namespace WildTrack.App.Controls;

/// <summary>
/// Port of GuitarCam/src/components/MiniTimeline.tsx: the whole project in one strip above the mixer and the
/// Mastering view, each track's clips as thin bars and the playhead, under a small ruler in bars or in minutes and
/// seconds, as the timeline's ruler switch says (native only, asked by the user). Click or drag to move the playhead
/// (not while recording). The control only raises <see cref="SeekRequested"/>; the window moves the engine.
/// </summary>
public sealed class MiniTimeline : Control
{
    const double PadX = 12, BarH = 18;
    /// <summary>Height of the ruler over the bar, and the px its labels and ticks need between them.</summary>
    const double RulerH = 16, LabelPx = 44, BarLabelPx = 28, MinorPx = 5;
    IReadOnlyList<LiveTrack> tracks = [];
    double playhead, duration, bpm = 120;
    bool recording, scrubbing, held, inBars = true;
    double downX;
    /// <summary>The ruler's labels, built once each, so a redraw while playing does not allocate.</summary>
    readonly Dictionary<long, string> labels = [];
    public event Action<double>? SeekRequested;
    /// <summary>A drag started (true) or ended (false), like <see cref="TimelineView.ScrubChanged"/>.</summary>
    public event Action<bool>? ScrubChanged;

    public MiniTimeline()
    {
        Height = 40;
        Cursor = new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(this, "Hacé clic o arrastrá para mover el cabezal");
    }

    public void SetTracks(IReadOnlyList<LiveTrack> list) { tracks = list; InvalidateVisual(); }

    /// <summary>The ruler in bars and beats (true) or in minutes and seconds, like <see cref="TimelineView.RulerInBars"/>.</summary>
    public bool RulerInBars { get => inBars; set { if (inBars == value) return; inBars = value; InvalidateVisual(); } }

    public double Bpm { get => bpm; set { if (bpm == value || value <= 0) return; bpm = value; InvalidateVisual(); } }

    public void SetPlayhead(double sec, double durationSec, bool isRecording)
    {
        if (sec == playhead && durationSec == duration && isRecording == recording) return;
        if (isRecording != recording) Cursor = new Cursor(isRecording ? StandardCursorType.Arrow : StandardCursorType.Hand);
        playhead = sec; duration = durationSec; recording = isRecording;
        InvalidateVisual();
    }

    // Past the clips (open-ended playback or recording) the strip grows so the playhead stays on it.
    double Span => Math.Max(1, Math.Max(duration, playhead * 1.05));
    Rect Bar => new(PadX, RulerH + (Bounds.Height - RulerH - BarH) / 2, Math.Max(1, Bounds.Width - 2 * PadX), BarH);

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(Palette.Panel, new Rect(Bounds.Size));
        var bar = Bar;
        double span = Span;
        double X(double s) => bar.X + Math.Max(0, s) / span * bar.Width;
        DrawRuler(ctx, bar, span);
        ctx.DrawRectangle(Palette.Raised, null, bar, 2, 2);

        int rows = 0;
        foreach (var t in tracks) if (t.Clips.Count > 0) rows++;
        if (rows > 0)
        {
            double rowH = Math.Max(2, Math.Min(5, 16.0 / rows));
            double total = rows * rowH + (rows - 1);
            double y = bar.Y + (bar.Height - total) / 2;
            foreach (var t in tracks)
            {
                if (t.Clips.Count == 0) continue;
                var fill = t.Channel.Muted ? Palette.Alpha(Palette.FaintColor, 0.4) : Palette.Alpha(Palette.AccentColor, 0.45);
                foreach (var c in t.Clips)
                    ctx.DrawRectangle(fill, null, new Rect(X(c.ClipStartSec), y, Math.Max(1, X(c.ClipEndSec) - X(c.ClipStartSec)), rowH), 1, 1);
                y += rowH + 1;
            }
        }
        double px = X(playhead);
        ctx.DrawRectangle(recording ? Palette.Rec : Palette.Accent, null, new Rect(px - 1, bar.Y - 2, 2, bar.Height + 4), 1, 1);
    }

    /// <summary>The whole song's ruler: labelled steps (bars, or the timeline's time steps) and the minor ticks between them when they fit.</summary>
    void DrawRuler(DrawingContext ctx, Rect bar, double span)
    {
        double pps = bar.Width / span, right = bar.Right;
        var major = Palette.Pen(Palette.Line);
        var minor = Palette.Pen(Palette.Alpha(Palette.LineColor, 0.7));
        double step, sub;
        int subs;
        if (inBars)
        {
            double beat = 60 / bpm, len = 4 * beat;
            int n = 1;
            while (n * len * pps < BarLabelPx && n < 1 << 20) n *= 2;
            step = n * len;
            (sub, subs) = n == 1 ? (beat, 4) : (len, n);
        }
        else
        {
            var steps = TimelineView.TimeSteps;
            int si = 0;
            while (si < steps.Length - 1 && steps[si] * pps < LabelPx) si++;
            step = steps[si];
            subs = TimelineView.TimeDivs[si];
            sub = step / subs;
        }
        bool fine = sub * pps >= MinorPx;
        for (long i = 0; ; i++)
        {
            double s = i * step, x = Math.Round(bar.X + s * pps) + 0.5;
            if (x > right) break;
            ctx.DrawLine(major, new Point(x, 3), new Point(x, RulerH));
            var label = Label(i, step);
            if (x + 3 + label.Length * 6 <= right + PadX) ctx.DrawText(label, 10, Palette.Faint, new Point(x + 3, 1), Palette.Mono);
            if (fine)
                for (int k = 1; k < subs; k++)
                {
                    double mx = Math.Round(bar.X + (s + k * sub) * pps) + 0.5;
                    if (mx > right) break;
                    ctx.DrawLine(minor, new Point(mx, RulerH - 4), new Point(mx, RulerH));
                }
        }
    }

    /// <summary>The i-th step's label: its bar number, or its time as the timeline's ruler writes it.</summary>
    string Label(long i, double step)
    {
        long key = inBars ? -1 - (long)Math.Round(i * step / (240 / bpm)) : (long)Math.Round(i * step * 1000) * 2 + (step < 1 ? 1 : 0);
        if (labels.TryGetValue(key, out var l)) return l;
        double s = i * step;
        l = inBars ? (-key).ToString()
            : s >= 60 ? (step < 1 ? $"{(int)(s / 60)}:{s % 60:00.###}" : $"{(int)(s / 60)}:{s % 60:00}") : $"{s:0.###}s";
        labels[key] = l;
        return l;
    }

    double SecAt(Point p) => Math.Clamp((p.X - Bar.X) / Bar.Width * Span, 0, Span);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || recording) return;
        scrubbing = true;
        downX = e.GetPosition(this).X;
        e.Pointer.Capture(this);
        SeekRequested?.Invoke(SecAt(e.GetPosition(this)));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (!scrubbing) return;
        var p = e.GetPosition(this);
        if (!held && Math.Abs(p.X - downX) >= 3)
        {
            held = true;
            ScrubChanged?.Invoke(true);
        }
        // a click is one jump, on press (see TimelineView's scrub)
        if (held) SeekRequested?.Invoke(SecAt(p));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (!scrubbing) return;
        if (held) SeekRequested?.Invoke(SecAt(e.GetPosition(this)));
        EndScrub();
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) => EndScrub();

    void EndScrub()
    {
        if (!scrubbing) return;
        scrubbing = false;
        if (!held) return;
        held = false;
        ScrubChanged?.Invoke(false);
    }
}
