using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using GuitarCam.App.Model;

namespace GuitarCam.App.Controls;

/// <summary>
/// The timeline: bar ruler, one lane per take with its clip and waveform, sticky track headers
/// (M / S, meter) and the playhead. Wheel scrolls, Shift/Ctrl+wheel zooms at the pointer.
/// </summary>
public sealed class TimelineView : Control
{
    public const double HeaderW = 230;
    public const double RulerH = 28;

    IReadOnlyList<Track> tracks = [];
    double pps = 10; // pixels per second
    double scrollX;
    double laneH = 78;
    bool fitted;
    readonly Dictionary<Track, (StreamGeometry Geo, double Key)> waveCache = [];

    public double Bpm { get; set; } = 120;
    public double LengthSec { get; set; }
    public double Playhead { get; private set; }
    public int Selected { get; private set; } = -1;

    public event Action<double>? SeekRequested;
    public event Action<int>? SelectionChanged;
    public event Action<Track>? TrackToggled;

    public TimelineView()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    public void SetTracks(IReadOnlyList<Track> list)
    {
        tracks = list;
        waveCache.Clear();
        fitted = false;
        Selected = list.Count > 0 ? 0 : -1;
        InvalidateVisual();
    }

    public void Select(int i)
    {
        Selected = i;
        SelectionChanged?.Invoke(i);
        InvalidateVisual();
    }

    /// <summary>Moves the playhead, scrolling a page when it runs off the right edge while playing.</summary>
    public void SetPlayhead(double sec, bool follow)
    {
        Playhead = sec;
        double x = X(sec);
        if (follow && (x > Bounds.Width - 40 || x < HeaderW)) scrollX = Math.Max(0, sec * pps - 40);
        InvalidateVisual();
    }

    public void FitAll()
    {
        double w = Bounds.Width - HeaderW - 24;
        if (w <= 0 || LengthSec <= 0) return;
        pps = w / LengthSec;
        scrollX = 0;
        InvalidateVisual();
    }

    double X(double sec) => HeaderW + sec * pps - scrollX;

    double Sec(double x) => (x - HeaderW + scrollX) / pps;

    public override void Render(DrawingContext ctx)
    {
        if (!fitted && Bounds.Width > HeaderW && LengthSec > 0)
        {
            fitted = true;
            pps = (Bounds.Width - HeaderW - 24) / LengthSec;
        }
        double W = Bounds.Width, H = Bounds.Height;
        ctx.FillRectangle(Palette.App, new Rect(0, 0, W, H));

        using (ctx.PushClip(new Rect(HeaderW, 0, W - HeaderW, H)))
        {
            DrawRuler(ctx, W);
            for (int i = 0; i < tracks.Count; i++) DrawLane(ctx, i, W);
            double px = X(Playhead);
            ctx.DrawLine(Palette.Pen(Palette.Accent, 1.5), new Point(px, 0), new Point(px, H));
            var head = new StreamGeometry();
            using (var g = head.Open())
            {
                g.BeginFigure(new Point(px - 6, 0), true);
                g.LineTo(new Point(px + 6, 0));
                g.LineTo(new Point(px, 8));
                g.EndFigure(true);
            }
            ctx.DrawGeometry(Palette.Accent, null, head);
        }

        // sticky headers
        ctx.FillRectangle(Palette.Panel, new Rect(0, 0, HeaderW, H));
        ctx.FillRectangle(Palette.Panel, new Rect(0, 0, HeaderW, RulerH));
        ctx.DrawText("PISTAS", 10.5, Palette.Faint, new Point(14, 8), Palette.UiBold);
        ctx.DrawLine(Palette.Pen(Palette.Line), new Point(0, RulerH - 0.5), new Point(W, RulerH - 0.5));
        for (int i = 0; i < tracks.Count; i++) DrawHeader(ctx, i);
        ctx.DrawLine(Palette.Pen(Palette.Line), new Point(HeaderW - 0.5, 0), new Point(HeaderW - 0.5, H));
    }

    void DrawRuler(DrawingContext ctx, double W)
    {
        ctx.FillRectangle(Palette.Panel, new Rect(HeaderW, 0, W - HeaderW, RulerH));
        double beat = 60 / Bpm, bar = 4 * beat;
        int step = 1;
        while (step * bar * pps < 56) step *= 2;
        int first = Math.Max(0, (int)(Sec(HeaderW) / bar) - 1);
        var tick = Palette.Pen(Palette.Line);
        var beatPen = Palette.Pen(Palette.Alpha(Color.Parse("#27272c"), 0.7));
        for (int b = first - first % step; ; b += step)
        {
            double x = X(b * bar);
            if (x > W) break;
            ctx.DrawLine(tick, new Point(x, 12), new Point(x, RulerH));
            ctx.DrawText((b + 1).ToString(), 10.5, Palette.Dim, new Point(x + 4, 6), Palette.Mono);
            if (step == 1 && beat * pps > 14)
                for (int k = 1; k < 4; k++)
                {
                    double bx = X(b * bar + k * beat);
                    ctx.DrawLine(beatPen, new Point(bx, RulerH - 6), new Point(bx, RulerH));
                }
        }
    }

    void DrawLane(DrawingContext ctx, int i, double W)
    {
        var t = tracks[i];
        double y = RulerH + i * laneH;
        bool sel = i == Selected;
        if (sel) ctx.FillRectangle(Palette.Alpha(Color.Parse("#1c1c20"), 0.6), new Rect(HeaderW, y, W - HeaderW, laneH));

        double x0 = X(t.ClipStartSec), x1 = X(t.ClipEndSec);
        var clip = new Rect(x0, y + 5, Math.Max(2, x1 - x0), laneH - 10);
        bool silent = t.Take.Muted;
        var fill = Palette.Alpha(Palette.AccentColor, sel ? 0.16 : 0.10);
        var border = Palette.Pen(Palette.Alpha(Palette.AccentColor, sel ? 0.9 : 0.45), sel ? 1.5 : 1);
        ctx.DrawRectangle(fill, border, clip, 5, 5);
        using (ctx.PushClip(clip))
        {
            ctx.DrawText(t.Take.Name, 11, sel ? Palette.Fg : Palette.Dim, new Point(Math.Max(x0, HeaderW) + 8, y + 7), Palette.UiBold);
            var geo = Waveform(t, y, W);
            var wave = Palette.Alpha(Palette.AccentColor, silent ? 0.3 : 0.85);
            ctx.DrawGeometry(null, Palette.Pen(wave, 1), geo);
        }
        ctx.DrawLine(Palette.Pen(Palette.Line), new Point(HeaderW, y + laneH - 0.5), new Point(W, y + laneH - 0.5));
    }

    /// <summary>Min/max per pixel column, cached until the zoom, scroll or size changes.</summary>
    StreamGeometry Waveform(Track t, double y, double W)
    {
        double key = pps * 1e6 + scrollX * 7.3 + W * 0.013 + y * 0.0007 + laneH;
        if (waveCache.TryGetValue(t, out var hit) && hit.Key == key) return hit.Geo;
        var geo = new StreamGeometry();
        double top = y + 24, bottom = y + laneH - 9;
        double mid = (top + bottom) / 2, half = (bottom - top) / 2;
        double binsPerSec = t.SampleRate / (double)Track.PeakBin;
        int xa = (int)Math.Max(HeaderW, X(t.ClipStartSec));
        int xb = (int)Math.Min(W, X(t.ClipEndSec));
        double lo = t.Take.TrimStartSec * binsPerSec, hi = (t.LengthSec - t.Take.TrimEndSec) * binsPerSec;
        using (var g = geo.Open())
        {
            for (int px = xa; px < xb; px++)
            {
                double s0 = (Sec(px) - t.OffsetSec) * binsPerSec;
                double s1 = (Sec(px + 1) - t.OffsetSec) * binsPerSec;
                int b0 = (int)Math.Max(lo, s0), b1 = (int)Math.Min(hi, Math.Max(s0 + 1, s1));
                float mn = 0, mx = 0;
                for (int b = Math.Max(0, b0); b < b1 && b < t.PeakMax.Length; b++)
                {
                    if (t.PeakMin[b] < mn) mn = t.PeakMin[b];
                    if (t.PeakMax[b] > mx) mx = t.PeakMax[b];
                }
                double ya = mid - Math.Min(1, mx) * half, yb = mid - Math.Max(-1, mn) * half;
                if (yb - ya < 1) yb = ya + 1;
                g.BeginFigure(new Point(px + 0.5, ya), false);
                g.LineTo(new Point(px + 0.5, yb));
                g.EndFigure(false);
            }
        }
        waveCache[t] = (geo, key);
        return geo;
    }

    Rect MuteRect(int i) => new(HeaderW - 84, RulerH + i * laneH + laneH - 30, 24, 20);
    Rect SoloRect(int i) => new(HeaderW - 56, RulerH + i * laneH + laneH - 30, 24, 20);

    void DrawHeader(DrawingContext ctx, int i)
    {
        var t = tracks[i];
        double y = RulerH + i * laneH;
        bool sel = i == Selected;
        ctx.FillRectangle(sel ? Palette.Raised : Palette.Panel, new Rect(0, y, HeaderW, laneH));
        if (sel) ctx.FillRectangle(Palette.Accent, new Rect(0, y, 2, laneH));

        var badge = new Rect(12, y + 10, 20, 20);
        ctx.DrawRectangle(Palette.Hover, null, badge, 5, 5);
        var num = Palette.Text((i + 1).ToString(), 11, Palette.Dim, Palette.Mono);
        ctx.DrawText(num, new Point(badge.Center.X - num.Width / 2, badge.Center.Y - num.Height / 2));
        ctx.DrawText(t.Take.Name, 12.5, Palette.Fg, new Point(40, y + 11), Palette.UiBold, HeaderW - 60);

        double vol = 20 * Math.Log10(Math.Max(1e-4, t.Take.Volume));
        string pan = Math.Abs(t.Take.Pan) < 0.01 ? "C" : t.Take.Pan < 0 ? $"L{-t.Take.Pan * 100:0}" : $"R{t.Take.Pan * 100:0}";
        ctx.DrawText($"{vol:+0.0;-0.0;0.0} dB · {pan}", 11, Palette.Faint, new Point(40, y + laneH - 27), Palette.Mono);

        DrawToggle(ctx, MuteRect(i), "M", t.Take.Muted, Palette.Brush("#f59e0b"));
        DrawToggle(ctx, SoloRect(i), "S", t.Take.Solo, Palette.Accent);

        // meter: two thin bars at the right edge
        double mh = laneH - 20, mx = HeaderW - 22;
        DrawMeterBar(ctx, new Rect(mx, y + 10, 4, mh), t.ShowL);
        DrawMeterBar(ctx, new Rect(mx + 6, y + 10, 4, mh), t.ShowR);
        ctx.DrawLine(Palette.Pen(Palette.Line), new Point(0, y + laneH - 0.5), new Point(HeaderW, y + laneH - 0.5));
    }

    static void DrawToggle(DrawingContext ctx, Rect r, string label, bool on, IBrush onBrush)
    {
        ctx.DrawRectangle(on ? onBrush : Palette.Hover, on ? null : Palette.Pen(Palette.Line), r, 4, 4);
        var t = Palette.Text(label, 10.5, on ? Palette.App : Palette.Dim, Palette.UiBold);
        ctx.DrawText(t, new Point(r.Center.X - t.Width / 2, r.Center.Y - t.Height / 2));
    }

    void DrawMeterBar(DrawingContext ctx, Rect r, float peak)
    {
        ctx.FillRectangle(Palette.App, r);
        double db = Palette.ToDb(peak);
        double k = Math.Clamp((db + 48) / 48, 0, 1);
        if (k <= 0) return;
        double h = r.Height * k;
        ctx.FillRectangle(db > -1 ? Palette.Rec : Palette.Accent, new Rect(r.X, r.Bottom - h, r.Width, h));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var p = e.GetPosition(this);
        if (p.Y < RulerH)
        {
            if (p.X > HeaderW) SeekRequested?.Invoke(Math.Max(0, Sec(p.X)));
            return;
        }
        int i = (int)((p.Y - RulerH) / laneH);
        if (i >= tracks.Count)
        {
            Select(-1);
            return;
        }
        if (MuteRect(i).Contains(p))
        {
            tracks[i].Take.Muted = !tracks[i].Take.Muted;
            TrackToggled?.Invoke(tracks[i]);
        }
        else if (SoloRect(i).Contains(p))
        {
            tracks[i].Take.Solo = !tracks[i].Take.Solo;
            TrackToggled?.Invoke(tracks[i]);
        }
        Select(i);
        if (p.X > HeaderW && e.ClickCount == 2) SeekRequested?.Invoke(Math.Max(0, Sec(p.X)));
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var p = e.GetPosition(this);
        if ((e.KeyModifiers & (KeyModifiers.Shift | KeyModifiers.Control)) != 0)
        {
            double at = Sec(Math.Max(HeaderW, p.X));
            pps = Math.Clamp(pps * Math.Pow(1.18, e.Delta.Y), 1, 800);
            scrollX = Math.Max(0, at * pps - (Math.Max(HeaderW, p.X) - HeaderW));
        }
        else if ((e.KeyModifiers & KeyModifiers.Alt) != 0)
        {
            laneH = Math.Clamp(laneH + e.Delta.Y * 8, 56, 200);
        }
        else
        {
            scrollX = Math.Max(0, scrollX - (e.Delta.Y + e.Delta.X) * 80);
        }
        e.Handled = true;
        InvalidateVisual();
    }
}
