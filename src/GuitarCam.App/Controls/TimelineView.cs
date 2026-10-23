using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using GuitarCam.App.Model;
using GuitarCam.Projects;
using GuitarCam.Audio.Dsp;

namespace GuitarCam.App.Controls;

/// <summary>
/// The timeline: bar ruler, one lane per track with its clips and waveforms, sticky track headers
/// Port of GuitarCam/src/components/Timeline.tsx gestures, handles, snap and lane controls.
/// Native differences: drag previews are local and commit on release while the window rebuilds audio;
/// multiple clips per track swap only with the clip under the pointer. Thumbnails are not ported yet.
/// </summary>
public sealed class TimelineView : Control
{
    public const double HeaderW = 264;
    public const double RulerH = 32;

    IReadOnlyList<LiveTrack> tracks = [];
    double pps = 10; // pixels per second
    double scrollX;
    double laneH = 64;
    double scrollY;
    string snapMode = "1/4";
    bool bars = true;
    Point lastPointer;
    string? gesture;
    IPointer? capturedPointer;
    Point down;
    double downScrollX, downScrollY;
    int fromIndex = -1, overIndex = -1;
    LiveClip? dragged;
    Clip? original, preview;
    ClipEditMode dragMode;
    bool moved;
    string? swapId;
    KeyModifiers modifiers;
    double originalPan;
    readonly DispatcherTimer dragScroll;
    bool fitMode = true;
    readonly Dictionary<LiveClip, (StreamGeometry Geo, double Key)> waveCache = [];

    public double Bpm { get; set; } = 120;
    public double LengthSec { get; set; }
    public double Playhead { get; private set; }
    public int Selected { get; private set; } = -1;
    public string? SelectedClipId { get; private set; }
    public string SnapMode { get => snapMode; set { snapMode = value; InvalidateVisual(); } }
    public bool RulerInBars { get => bars; set { bars = value; InvalidateVisual(); } }
    public double TrackHeight => laneH;
    int? recordingLane;
    /// <summary>
    /// The track a take is going into while recording (tracks.Count = an extra row for a new track at the
    /// end); null when not recording. Like the web, the timeline only scrolls and zooms meanwhile.
    /// </summary>
    public int? RecordingLane
    {
        get => recordingLane;
        set { recordingLane = value; CancelGesture(); ClampScroll(); InvalidateVisual(); }
    }
    int ExtraRows => recordingLane == tracks.Count ? 1 : 0;
    public event Action? EditGestureStarted;
    public event Action<Clip, string?, string?>? ClipEditCommitted;
    public event Action<int, int>? ReorderRequested;
    public event Action<string>? RemoveTrackRequested;
    public event Action<LiveTrack, LiveClip?, Point>? ClipContextRequested;
    public event Action<Point>? NewTrackRequested;
    public event Action<LiveTrack, double?, double?>? ChannelRequested;
    public event Action<double>? TrackHeightChanged;

    public event Action<double>? SeekRequested;
    public event Action<int>? SelectionChanged;
    /// <summary>Requests a channel edit from the window; true selects solo, false selects mute.</summary>
    public event Action<LiveTrack, bool>? TrackToggleRequested;

    public TimelineView()
    {
        ClipToBounds = true;
        Focusable = true;
        dragScroll = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Input, (_, _) =>
        {
            if (gesture is not ("clip" or "reorder" or "scrub")) return;
            double oldX = scrollX, oldY = scrollY;
            if (lastPointer.Y < RulerH + 36) scrollY -= Math.Ceiling((RulerH + 36 - lastPointer.Y) / 4);
            else if (lastPointer.Y > Bounds.Height - 36) scrollY += Math.Ceiling((lastPointer.Y - Bounds.Height + 36) / 4);
            if (gesture is "clip" or "scrub")
            {
                if (lastPointer.X > Bounds.Width - 16) scrollX += Math.Max(2, (lastPointer.X - Bounds.Width + 16) / 2);
                else if (lastPointer.X < HeaderW + 16) scrollX = Math.Max(0, scrollX - Math.Max(2, (HeaderW + 16 - lastPointer.X) / 2));
            }
            ClampScroll();
            if (oldX != scrollX || oldY != scrollY) UpdateGesture();
        });
        DetachedFromVisualTree += (_, _) => dragScroll.Stop();
        dragScroll.Stop();
    }

    public void SetTracks(IReadOnlyList<LiveTrack> list, bool preserveView = false)
    {
        tracks = list;
        waveCache.Clear();
        if (!preserveView) fitMode = true;
        Selected = list.Count > 0 ? 0 : -1;
        if (!list.Any(t => t.Clips.Any(c => c.Clip.Id == SelectedClipId))) SelectedClipId = null;
        ClampScroll();
        InvalidateVisual();
    }

    public void Select(int i)
    {
        Selected = i;
        if (i < 0 || i >= tracks.Count) SelectedClipId = null;
        else if (!tracks[i].Clips.Any(c => c.Clip.Id == SelectedClipId)) SelectedClipId = tracks[i].Clips.FirstOrDefault()?.Clip.Id;
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
        fitMode = true;
        pps = w / Math.Max(8, LengthSec);
        scrollX = 0;
        InvalidateVisual();
    }

    double X(double sec) => HeaderW + sec * pps - scrollX;

    double Sec(double x) => (x - HeaderW + scrollX) / pps;
    double LaneY(int i)
    {
        int slot = i;
        if (gesture == "reorder" && moved && i < tracks.Count)
        {
            if (i == fromIndex) slot = overIndex;
            else if (fromIndex < overIndex && i > fromIndex && i <= overIndex) slot--;
            else if (fromIndex > overIndex && i >= overIndex && i < fromIndex) slot++;
        }
        return RulerH + slot * laneH - scrollY;
    }
    double Grid => ClipEditing.GridSeconds(snapMode, Bpm);
    double ViewSec => Math.Max(8, Math.Max(LengthSec, Playhead + 2));
    double MinPps => Math.Max(0.01, (Bounds.Width - HeaderW) / Math.Max(600, ViewSec * 8));
    void ClampScroll() => scrollY = Math.Clamp(scrollY, 0, Math.Max(0, (tracks.Count + ExtraRows) * laneH + 44 - (Bounds.Height - RulerH)));
    public void SetTrackHeight(double height)
    {
        double next = Math.Clamp(height, 40, 160);
        if (Math.Abs(next - laneH) < 0.001) return;
        laneH = next;
        ClampScroll();
        waveCache.Clear();
        TrackHeightChanged?.Invoke(laneH);
        InvalidateVisual();
    }
    Clip DisplayClip(LiveClip clip) => dragged == clip && preview != null && overIndex == fromIndex ? preview : clip.Clip;
    ClipEnvelope.ClipWindow Window(LiveClip clip, Clip? value = null)
    {
        var c = value ?? DisplayClip(clip);
        return ClipEditing.Window(c, clip.Media, clip.LengthSec);
    }
    LiveClip? ClipAt(int i, double x) => i >= 0 && i < tracks.Count
        ? tracks[i].Clips.LastOrDefault(c => { var w = Window(c); return x >= X(w.Start) && x <= X(w.End); }) : null;

    public override void Render(DrawingContext ctx)
    {
        if (fitMode && Bounds.Width > HeaderW)
        {
            pps = (Bounds.Width - HeaderW - 24) / Math.Max(8, LengthSec);
        }
        double W = Bounds.Width, H = Bounds.Height;
        ctx.FillRectangle(Palette.App, new Rect(0, 0, W, H));

        using (ctx.PushClip(new Rect(HeaderW, RulerH, W - HeaderW, Math.Max(0, H - RulerH))))
        {
            for (int i = 0; i < tracks.Count; i++) DrawLane(ctx, i, W);
            if (recordingLane is { } r) DrawRecordingBar(ctx, r, W);
        }
        DrawRuler(ctx, W);
        using (ctx.PushClip(new Rect(HeaderW, 0, W - HeaderW, H)))
        {
            double px = X(Playhead);
            var playColor = recordingLane != null ? Palette.Rec : Palette.Accent;
            ctx.DrawLine(Palette.Pen(playColor, 1.5), new Point(px, 0), new Point(px, H));
            var head = new StreamGeometry();
            using (var g = head.Open())
            {
                g.BeginFigure(new Point(px - 6, 0), true);
                g.LineTo(new Point(px + 6, 0));
                g.LineTo(new Point(px, 8));
                g.EndFigure(true);
            }
            ctx.DrawGeometry(playColor, null, head);
        }

        // sticky headers
        ctx.FillRectangle(Palette.Panel, new Rect(0, 0, HeaderW, H));
        ctx.FillRectangle(Palette.Panel, new Rect(0, 0, HeaderW, RulerH));
        ctx.DrawLine(Palette.Pen(Palette.Line), new Point(0, RulerH - 0.5), new Point(W, RulerH - 0.5));
        using (ctx.PushClip(new Rect(0, RulerH, HeaderW, Math.Max(0, H - RulerH))))
            for (int i = 0; i < tracks.Count; i++) DrawHeader(ctx, i);
        if (ExtraRows > 0) DrawRecordingHeader(ctx, LaneY(tracks.Count), W);
        double addY = LaneY(tracks.Count + ExtraRows);
        if (addY >= RulerH && addY < H)
        {
            ctx.FillRectangle(Palette.App, new Rect(0, addY, W, 44));
            ctx.DrawText("\uE710", 12, Palette.Dim, new Point(12, addY + 13), new Typeface("Segoe Fluent Icons, Segoe MDL2 Assets"));
            ctx.DrawText("Nueva pista", 12, Palette.Dim, new Point(32, addY + 13));
            ctx.DrawLine(Palette.Pen(Palette.Line), new Point(0, addY + 43.5), new Point(W, addY + 43.5));
        }
        if (tracks.Count == 0 && recordingLane == null)
        {
            ctx.DrawText("Todavía no hay pistas", 12.5, Palette.Dim, new Point(HeaderW + 24, RulerH + 66), Palette.UiBold);
            ctx.DrawText("Grabá la base con R, o creá pistas vacías desde «Nueva pista» y grabá en cada una.", 12,
                Palette.Faint, new Point(HeaderW + 24, RulerH + 90));
            ctx.DrawText("En las tomas siguientes vas a escuchar las anteriores mientras tocás.", 12,
                Palette.Faint, new Point(HeaderW + 24, RulerH + 108));
        }
        if (dragged != null && overIndex >= 0 && overIndex != fromIndex)
        {
            var r = new Rect(HeaderW + 1, LaneY(overIndex) + 1, Math.Max(1, W - HeaderW - 2), laneH - 2);
            ctx.DrawRectangle(Palette.Alpha(Palette.AccentColor, 0.1), Palette.Pen(Palette.Accent, 2), r);
            string text = swapId == null ? $"Mover a «{tracks[overIndex].Track.Name}»" : $"Intercambiar con «{tracks[overIndex].Track.Name}»";
            ctx.DrawText(text, 11, Palette.Accent, new Point(Math.Max(HeaderW + 10, W - 280), r.Y + 8), Palette.UiBold);
        }
        ctx.DrawLine(Palette.Pen(Palette.Line), new Point(HeaderW - 0.5, 0), new Point(HeaderW - 0.5, H));
    }

    /// <summary>The take being recorded: a red bar from 0 to the playhead (Timeline.tsx recBar).</summary>
    void DrawRecordingBar(DrawingContext ctx, int lane, double W)
    {
        double y = LaneY(lane);
        if (lane == tracks.Count) ctx.FillRectangle(Palette.Alpha(Palette.RecColor, 0.14 * 0.4), new Rect(HeaderW, y, W - HeaderW, laneH));
        double x0 = X(0), x1 = X(Playhead);
        if (x1 > x0) ctx.DrawRectangle(Palette.Alpha(Palette.RecColor, 0.2), Palette.Pen(Palette.Alpha(Palette.RecColor, 0.5)), new Rect(x0, y + 6, x1 - x0, laneH - 12), 4, 4);
        if (lane == tracks.Count) ctx.DrawLine(Palette.Pen(Palette.Line), new Point(HeaderW, y + laneH - 0.5), new Point(W, y + laneH - 0.5));
    }

    /// <summary>The extra row of a take going to a new track (Timeline.tsx extraRecRow).</summary>
    void DrawRecordingHeader(DrawingContext ctx, double y, double W)
    {
        ctx.FillRectangle(Palette.Brush("#1d1214"), new Rect(0, y, HeaderW, laneH));
        var badge = new Rect(12, y + (laneH - 20) / 2, 20, 20);
        ctx.DrawRectangle(Palette.RecSoft, null, badge, 4, 4);
        ctx.DrawEllipse(Palette.Rec, null, badge.Center, 4, 4);
        var text = Palette.Text("Grabando…", 13, Palette.Rec);
        ctx.DrawText(text, new Point(42, y + (laneH - text.Height) / 2));
        ctx.DrawLine(Palette.Pen(Palette.Line), new Point(0, y + laneH - 0.5), new Point(W, y + laneH - 0.5));
    }

    void DrawRuler(DrawingContext ctx, double W)
    {
        ctx.FillRectangle(Palette.Panel, new Rect(HeaderW, 0, W - HeaderW, RulerH));
        if (!bars)
        {
            double[] steps = [0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800];
            double seconds = steps.FirstOrDefault(s => s * pps >= 64, 1800);
            for (double s = Math.Max(0, Math.Floor(Sec(HeaderW) / seconds) * seconds); X(s) <= W; s += seconds)
            {
                double x = X(s);
                ctx.DrawLine(Palette.Pen(Palette.Line), new Point(x, 12), new Point(x, RulerH));
                string label = s >= 60 ? $"{(int)(s / 60)}:{s % 60:00}" : $"{s:0.##}s";
                ctx.DrawText(label, 11, Palette.Dim, new Point(x + 4, 6), Palette.Mono);
            }
            return;
        }
        double beat = 60 / Bpm, bar = 4 * beat;
        int step = 1;
        while (step * bar * pps < 56) step *= 2;
        int first = Math.Max(0, (int)(Sec(HeaderW) / bar) - 1);
        var tick = Palette.Pen(Palette.Line);
        var beatPen = Palette.Pen(Palette.Alpha(Palette.LineColor, 0.7));
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
        double y = LaneY(i);
        if (y + laneH < RulerH || y > Bounds.Height) return;
        bool sel = i == Selected;
        if (sel) ctx.FillRectangle(Palette.Alpha(Palette.RaisedColor, 0.6), new Rect(HeaderW, y, W - HeaderW, laneH));
        foreach (var c in t.Clips) DrawClip(ctx, t, c, y, sel, W);
        if (t.Clips.Count == 0)
        {
            var empty = new Rect(HeaderW + 6, y + 6, Math.Max(1, Math.Min(W - HeaderW - 12, 520)), laneH - 12);
            ctx.DrawRectangle(null, Palette.Pen(sel ? Palette.Alpha(Palette.AccentColor, 0.5) : Palette.Line), empty, 4, 4);
            ctx.DrawText(sel ? "Apretá R para grabar en esta pista" : "Pista vacía · seleccionala para grabar acá", 12,
                sel ? Palette.Dim : Palette.Faint, new Point(empty.X + 12, empty.Y + Math.Max(2, (empty.Height - 16) / 2)));
        }
        ctx.DrawLine(Palette.Pen(Palette.Line), new Point(HeaderW, y + laneH - 0.5), new Point(W, y + laneH - 0.5));
    }

    void DrawClip(DrawingContext ctx, LiveTrack t, LiveClip c, double y, bool sel, double W)
    {
        var model = DisplayClip(c);
        var w = Window(c, model);
        double x0 = X(w.Start), x1 = X(w.End);
        sel = sel && (SelectedClipId == null || c.Clip.Id == SelectedClipId);
        if (x1 < HeaderW || x0 > W) return;
        var clip = new Rect(x0, y + 5, Math.Max(2, x1 - x0), laneH - 10);
        var color = c.Offline ? Color.Parse("#71717a") : Palette.AccentColor;
        bool silent = t.Channel.Muted || tracks.Any(o => o.Channel.Solo) && !t.Channel.Solo;
        using var opacity = ctx.PushOpacity(!c.Offline && silent ? 0.5 : 1);
        var fill = c.Offline ? Palette.Alpha(color, silent ? 0.05 : sel ? 0.16 : 0.10) : Palette.Alpha(Palette.HoverColor, 0.4);
        var border = Palette.Pen(c.Offline ? Palette.Alpha(color, sel ? 0.9 : 0.45)
            : sel ? Palette.Alpha(Palette.AccentColor, 0.6) : Palette.Line, c.Offline && sel ? 1.5 : 1);
        ctx.DrawRectangle(fill, border, clip, 5, 5);
        using (ctx.PushClip(clip))
        {
            if (c.Offline)
            {
                ctx.DrawText(c.Name, 11, sel ? Palette.Fg : Palette.Dim, new Point(Math.Max(x0, HeaderW) + 8, y + 7), Palette.UiBold);
                ctx.DrawText("Sin conexión: falta el archivo de audio", 11, Palette.Faint, new Point(Math.Max(x0, HeaderW) + 8, y + 26), Palette.Ui);
            }
            else
            {
                var geo = Waveform(c, model, w, y, W);
                var wave = silent ? Palette.Faint : sel ? Palette.Accent : Palette.Alpha(Palette.DimColor, 0.8);
                ctx.DrawGeometry(null, Palette.Pen(wave, 1), geo);
            }
            var f = ClipEnvelope.FitFades(model.FadeInSec, model.FadeOutSec, w.End - w.Start);
            void Fade(double a, double b, double edge)
            {
                var geo = new StreamGeometry();
                using (var g = geo.Open())
                {
                    g.BeginFigure(new Point(edge, clip.Top), true);
                    g.LineTo(new Point(a, clip.Top));
                    g.LineTo(new Point(b, clip.Bottom));
                    g.EndFigure(true);
                }
                ctx.DrawGeometry(Palette.Alpha(Color.Parse("#000000"), 0.55), null, geo);
                ctx.DrawLine(Palette.Pen(Palette.Accent, 1.5), new Point(a, clip.Top), new Point(b, clip.Bottom));
            }
            if (f.FadeIn > 0) Fade(x0 + f.FadeIn * pps, x0, x0);
            if (f.FadeOut > 0) Fade(x1 - f.FadeOut * pps, x1, x1);
            if (sel || clip.Contains(lastPointer))
            {
                ctx.DrawRectangle(Palette.Accent, Palette.Pen(Palette.App), new Rect(x0 + Math.Max(5, f.FadeIn * pps) - 5, clip.Top, 10, 10), 2, 2);
                ctx.DrawRectangle(Palette.Accent, Palette.Pen(Palette.App), new Rect(x1 - Math.Max(5, f.FadeOut * pps) - 5, clip.Top, 10, 10), 2, 2);
            }
            if (w.AnchorCut > 0.0005)
            {
                for (double sy = clip.Top - 6; sy < clip.Bottom; sy += 6)
                    ctx.DrawLine(Palette.Pen(Palette.Accent, 2), new Point(x0, sy + 6), new Point(x0 + 6, sy));
                if (clip.Width > 60)
                {
                    var label = Palette.Text($"{w.AnchorCut:0.00} s", 10.5, Palette.Accent, Palette.Mono);
                    ctx.DrawRectangle(Palette.Alpha(Color.Parse("#000000"), 0.7), null,
                        new Rect(x0 + 10, clip.Bottom - 19, label.Width + 22, 16), 2, 2);
                    ctx.DrawText("\uE8C6", 10, Palette.Accent, new Point(x0 + 14, clip.Bottom - 17), new Typeface("Segoe Fluent Icons, Segoe MDL2 Assets"));
                    ctx.DrawText(label, new Point(x0 + 28, clip.Bottom - 18));
                }
            }
        }
    }

    /// <summary>Min/max per pixel column, cached until the zoom, scroll or size changes.</summary>
    StreamGeometry Waveform(LiveClip c, Clip model, ClipEnvelope.ClipWindow w, double y, double W)
    {
        var a = c.Audio!;
        double key = pps * 1e6 + scrollX * 7.3 + W * 0.013 + y * 0.0007 + laneH
            + model.AtSec * 1.7e-3 + model.TrimStartSec * 3.1e-3 + model.TrimEndSec * 5.3e-3;
        if (waveCache.TryGetValue(c, out var hit) && hit.Key == key) return hit.Geo;
        var geo = new StreamGeometry();
        double top = y + 6, bottom = y + laneH - 6;
        double mid = (top + bottom) / 2, half = (bottom - top) / 2;
        double binsPerSec = a.SampleRate / (double)MediaAudio.PeakBin;
        int xa = (int)Math.Max(HeaderW, X(w.Start));
        int xb = (int)Math.Min(W, X(w.End));
        double lo = model.TrimStartSec * binsPerSec, hi = (a.LengthSec - model.TrimEndSec) * binsPerSec;
        using (var g = geo.Open())
        {
            for (int px = xa; px < xb; px++)
            {
                double s0 = (Sec(px) - model.AtSec) * binsPerSec;
                double s1 = (Sec(px + 1) - model.AtSec) * binsPerSec;
                int b0 = (int)Math.Max(lo, s0), b1 = (int)Math.Min(hi, Math.Max(s0 + 1, s1));
                float mn = 0, mx = 0;
                for (int b = Math.Max(0, b0); b < b1 && b < a.PeakMax.Length; b++)
                {
                    if (a.PeakMin[b] < mn) mn = a.PeakMin[b];
                    if (a.PeakMax[b] > mx) mx = a.PeakMax[b];
                }
                double ya = mid - Math.Min(1, mx) * half, yb = mid - Math.Max(-1, mn) * half;
                if (yb - ya < 1) yb = ya + 1;
                g.BeginFigure(new Point(px + 0.5, ya), false);
                g.LineTo(new Point(px + 0.5, yb));
                g.EndFigure(false);
            }
        }
        waveCache[c] = (geo, key);
        return geo;
    }

    Rect MuteRect(int i) => new(HeaderW - 94, LaneY(i) + 8, 24, 24);
    Rect SoloRect(int i) => new(HeaderW - 66, LaneY(i) + 8, 24, 24);
    Rect RemoveRect(int i) => new(HeaderW - 38, LaneY(i) + 8, 24, 24);
    Rect VolumeRect(int i) => new(52, LaneY(i) + 40, 92, 20);
    Rect PanRect(int i) => new(186, LaneY(i) + 40, 24, 24);

    void DrawHeader(DrawingContext ctx, int i)
    {
        var t = tracks[i];
        double y = LaneY(i);
        if (y + laneH < RulerH || y > Bounds.Height) return;
        bool sel = i == Selected;
        ctx.FillRectangle(sel ? Palette.Raised : Palette.Panel, new Rect(0, y, HeaderW, laneH));
        if (sel) ctx.FillRectangle(Palette.Accent, new Rect(0, y, 2, laneH));

        for (int k = 0; k < 3; k++)
        {
            ctx.FillRectangle(Palette.Faint, new Rect(8, y + 13 + k * 5, 2, 2));
            ctx.FillRectangle(Palette.Faint, new Rect(13, y + 13 + k * 5, 2, 2));
        }
        var badge = new Rect(26, y + 10, 20, 20);
        ctx.DrawRectangle(Palette.Hover, null, badge, 5, 5);
        var num = Palette.Text((i + 1).ToString(), 11, Palette.Dim, Palette.Mono);
        ctx.DrawText(num, new Point(badge.Center.X - num.Width / 2, badge.Center.Y - num.Height / 2));
        if (recordingLane == i)
        {
            ctx.DrawText("Grabando…", 13, Palette.Rec, new Point(52, y + 11), Palette.Ui, HeaderW - 152);
            ctx.DrawEllipse(Palette.Rec, null, new Point(RemoveRect(i).Center.X, y + 20), 4, 4);
        }
        else ctx.DrawText(t.Track.Name, 12.5, Palette.Fg, new Point(52, y + 11), Palette.UiBold, HeaderW - 152);

        if (recordingLane != i && lastPointer.Y >= y && lastPointer.Y < y + laneH)
            ctx.DrawText("×", 16, Palette.Faint, new Point(RemoveRect(i).X + 6, y + 7));
        ctx.DrawLine(Palette.Pen(Palette.Line), new Point(0, y + laneH - 0.5), new Point(HeaderW, y + laneH - 0.5));
        if (t.Clips.Count == 0) return;

        var ch = t.Channel;
        string pan = Math.Abs(ch.Pan) < 0.01 ? "C" : ch.Pan < 0 ? $"L{-ch.Pan * 100:0}" : $"R{ch.Pan * 100:0}";
        if (laneH >= 62)
        {
            var v = VolumeRect(i);
            double px = v.X + Math.Clamp(ch.Volume / 1.5, 0, 1) * v.Width;
            ctx.DrawLine(Palette.Pen(Palette.Line, 3), new Point(v.X, v.Center.Y), new Point(v.Right, v.Center.Y));
            ctx.DrawLine(Palette.Pen(Palette.Accent, 3), new Point(v.X, v.Center.Y), new Point(px, v.Center.Y));
            ctx.DrawEllipse(Palette.Accent, null, new Point(px, v.Center.Y), 4, 4);
            ctx.DrawText($"{ch.Volume * 100:0}", 10.5, Palette.Faint, new Point(148, y + 43), Palette.Mono);
            var pr = PanRect(i);
            ctx.DrawEllipse(Palette.Raised, Palette.Pen(Palette.Line), pr.Center, 10, 10);
            double angle = ch.Pan * Math.PI * 0.75 - Math.PI / 2;
            ctx.DrawLine(Palette.Pen(Palette.Accent, 2), pr.Center,
                new Point(pr.Center.X + Math.Cos(angle) * 8, pr.Center.Y + Math.Sin(angle) * 8));
            ctx.DrawText(pan, 10.5, Palette.Faint, new Point(216, y + 43), Palette.Mono);
        }

        DrawToggle(ctx, MuteRect(i), "M", ch.Muted, Palette.Brush("#f59e0b"));
        DrawToggle(ctx, SoloRect(i), "S", ch.Solo, Palette.Accent);

        // meter: two thin bars at the right edge
        double mh = laneH - 20, mx = HeaderW - 10;
        DrawMeterBar(ctx, new Rect(mx, y + 10, 4, mh), t.ShowL);
        DrawMeterBar(ctx, new Rect(mx + 6, y + 10, 4, mh), t.ShowR);
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
        lastPointer = p;
        modifiers = e.KeyModifiers;
        if (recordingLane != null) { e.Handled = true; return; }
        var buttons = e.GetCurrentPoint(this).Properties;
        if (buttons.IsMiddleButtonPressed)
        {
            StartGesture("pan", e);
            return;
        }
        int i = (int)Math.Floor((p.Y - RulerH + scrollY) / laneH);
        if (buttons.IsRightButtonPressed)
        {
            if (i >= 0 && i < tracks.Count && p.Y >= RulerH)
            {
                var clip = p.X > HeaderW ? ClipAt(i, p.X) : tracks[i].Clips.FirstOrDefault(c => c.Clip.Id == SelectedClipId) ?? tracks[i].Clips.FirstOrDefault();
                SelectClip(i, clip?.Clip.Id);
                ClipContextRequested?.Invoke(tracks[i], clip, p);
            }
            e.Handled = true;
            return;
        }
        if (!buttons.IsLeftButtonPressed) return;
        if (p.Y < RulerH)
        {
            if (p.X > HeaderW) { StartGesture("scrub", e); UpdateGesture(); }
            return;
        }
        if (i < 0 || i >= tracks.Count)
        {
            if (i == tracks.Count && p.X < HeaderW && p.Y < LaneY(tracks.Count) + 44) NewTrackRequested?.Invoke(p);
            Select(-1);
            return;
        }
        fromIndex = overIndex = i;
        if (p.X < 24)
        {
            StartGesture("reorder", e);
            return;
        }
        if (p.X < HeaderW && RemoveRect(i).Contains(p))
        {
            RemoveTrackRequested?.Invoke(tracks[i].Track.Id);
            return;
        }
        if (p.X < HeaderW && tracks[i].Clips.Count == 0) { Select(i); return; }
        if (p.X < HeaderW && MuteRect(i).Contains(p))
        {
            TrackToggleRequested?.Invoke(tracks[i], false);
        }
        else if (p.X < HeaderW && SoloRect(i).Contains(p))
        {
            TrackToggleRequested?.Invoke(tracks[i], true);
        }
        else if (laneH >= 62 && VolumeRect(i).Contains(p))
        {
            if (e.ClickCount == 2) ChannelRequested?.Invoke(tracks[i], 1, null);
            else { StartGesture("volume", e); UpdateGesture(); }
        }
        else if (laneH >= 62 && PanRect(i).Contains(p))
        {
            if (e.ClickCount == 2) ChannelRequested?.Invoke(tracks[i], null, 0);
            else { originalPan = tracks[i].Channel.Pan; StartGesture("pan-knob", e); }
        }
        else if (p.X >= HeaderW)
        {
            var clip = ClipAt(i, p.X);
            if (e.ClickCount == 2)
            {
                SelectClip(i, clip?.Clip.Id);
                SeekRequested?.Invoke(Math.Max(0, ClipEditing.Snap(Sec(p.X), Grid, (modifiers & KeyModifiers.Alt) != 0)));
                return;
            }
            if (clip == null)
            {
                Select(tracks[i].Clips.Count == 0 ? i : -1);
                return;
            }
            SelectClip(i, clip.Clip.Id);
            dragged = clip;
            original = ClipEditing.Copy(clip.Clip);
            preview = ClipEditing.Copy(clip.Clip);
            dragMode = HitHandle(clip, p);
            StartGesture("clip", e);
            return;
        }
        Select(i);
        InvalidateVisual();
    }

    public void SelectClip(int index, string? clipId)
    {
        SelectedClipId = clipId;
        Select(index);
    }

    ClipEditMode HitHandle(LiveClip clip, Point p)
    {
        var w = Window(clip);
        double left = X(w.Start), right = X(w.End);
        var c = DisplayClip(clip);
        var f = ClipEnvelope.FitFades(c.FadeInSec, c.FadeOutSec, w.End - w.Start);
        double top = LaneY(tracks.ToList().FindIndex(t => t.Clips.Contains(clip))) + 5;
        if (p.Y <= top + 10)
        {
            if (Math.Abs(p.X - (right - Math.Max(5, f.FadeOut * pps))) <= 6) return ClipEditMode.FadeOut;
            if (Math.Abs(p.X - (left + Math.Max(5, f.FadeIn * pps))) <= 6) return ClipEditMode.FadeIn;
        }
        if (p.X - left < 8) return ClipEditMode.TrimStart;
        if (right - p.X < 8) return ClipEditMode.TrimEnd;
        return ClipEditMode.Move;
    }

    void StartGesture(string kind, PointerPressedEventArgs e)
    {
        gesture = kind;
        down = e.GetPosition(this);
        downScrollX = scrollX;
        downScrollY = scrollY;
        moved = false;
        capturedPointer = e.Pointer;
        e.Pointer.Capture(this);
        if (kind is "clip" or "reorder" or "scrub") dragScroll.Start();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        lastPointer = e.GetPosition(this);
        modifiers = e.KeyModifiers;
        if (gesture != null) UpdateGesture();
        else UpdateHint();
        InvalidateVisual();
    }

    void UpdateGesture()
    {
        if (gesture == null) return;
        double dxPx = lastPointer.X - down.X + scrollX - downScrollX;
        if (gesture == "pan")
        {
            scrollX = Math.Max(0, downScrollX - (lastPointer.X - down.X));
            scrollY = downScrollY - (lastPointer.Y - down.Y);
            ClampScroll();
        }
        else if (gesture == "scrub")
            SeekRequested?.Invoke(Math.Max(0, ClipEditing.Snap(Sec(lastPointer.X), Grid, (modifiers & KeyModifiers.Alt) != 0)));
        else if (gesture == "volume")
        {
            var r = VolumeRect(fromIndex);
            ChannelRequested?.Invoke(tracks[fromIndex], Math.Clamp((lastPointer.X - r.X) / r.Width * 1.5, 0, 1.5), null);
        }
        else if (gesture == "pan-knob")
        {
            double pan = Math.Clamp(originalPan - (lastPointer.Y - down.Y) / 80, -1, 1);
            ChannelRequested?.Invoke(tracks[fromIndex], null, Math.Abs(pan) < 0.04 ? 0 : pan);
        }
        else
        {
            if (!moved && Math.Abs(dxPx) < 3 && Math.Abs(lastPointer.Y - down.Y) < 3) return;
            if (!moved) EditGestureStarted?.Invoke();
            moved = true;
            if (gesture == "reorder")
                overIndex = Math.Clamp((int)Math.Floor(fromIndex + (lastPointer.Y - down.Y + scrollY - downScrollY) / laneH + 0.5), 0, tracks.Count - 1);
            else if (dragged != null && original != null)
            {
                int lane = (int)Math.Floor((lastPointer.Y - RulerH + scrollY) / laneH);
                overIndex = dragMode == ClipEditMode.Move && lane >= 0 && lane < tracks.Count ? lane : fromIndex;
                swapId = overIndex != fromIndex ? ClipAt(overIndex, lastPointer.X)?.Clip.Id : null;
                if (overIndex != fromIndex) preview = ClipEditing.Copy(original);
                else preview = ClipEditing.Drag(original, dragged.Media, dragMode, dxPx / pps, pps, Playhead, Grid,
                    (modifiers & KeyModifiers.Alt) != 0, tracks.SelectMany(t => t.Clips).Where(c => c != dragged).Select(c => Window(c)), dragged.LengthSec);
            }
        }
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (gesture == null) return;
        lastPointer = e.GetPosition(this);
        modifiers = e.KeyModifiers;
        UpdateGesture();
        string kind = gesture;
        var value = preview;
        int from = fromIndex, to = overIndex;
        string? target = kind == "clip" && to >= 0 && to != from ? tracks[to].Track.Id : null;
        string? swap = swapId;
        bool changed = moved;
        ClearGesture();
        e.Pointer.Capture(null);
        if (kind == "clip" && changed && value != null) ClipEditCommitted?.Invoke(value, target, swap);
        if (kind == "reorder" && changed && from != to) ReorderRequested?.Invoke(from, to);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        ClearGesture();
    }

    public void CancelGesture() => ClearGesture();
    void ClearGesture()
    {
        gesture = null;
        var pointer = capturedPointer;
        capturedPointer = null;
        dragged = null;
        original = preview = null;
        swapId = null;
        fromIndex = overIndex = -1;
        dragScroll.Stop();
        pointer?.Capture(null);
        Cursor = new Cursor(StandardCursorType.Arrow);
        InvalidateVisual();
    }

    void UpdateHint()
    {
        int i = (int)Math.Floor((lastPointer.Y - RulerH + scrollY) / laneH);
        string? tip = null;
        var cursor = StandardCursorType.Arrow;
        if (lastPointer.Y < RulerH && lastPointer.X >= HeaderW)
            tip = "Click o arrastrá: mover el cabezal (Alt: sin imán) · Shift+rueda: zoom · Alt+rueda: altura · click del medio: mover";
        else if (i >= 0 && i < tracks.Count)
        {
            if (lastPointer.X < 24) { tip = "Arrastrar para reordenar"; cursor = StandardCursorType.Hand; }
            else if (RemoveRect(i).Contains(lastPointer)) tip = tracks[i].Clips.Count == 0 ? "Quitar pista (Supr)" : "Quitar pista del timeline (el archivo queda en Historial) · click derecho: más opciones";
            else if (lastPointer.X < HeaderW && tracks[i].Clips.Count == 0) tip = null;
            else if (MuteRect(i).Contains(lastPointer)) tip = tracks[i].Channel.Muted ? "Activar sonido" : "Silenciar (Mute)";
            else if (SoloRect(i).Contains(lastPointer)) tip = tracks[i].Channel.Solo ? "Quitar solo" : "Solo (escuchar solo esta pista)";
            else if (laneH >= 62 && VolumeRect(i).Contains(lastPointer)) tip = $"Volumen {tracks[i].Channel.Volume * 100:0}% (doble click: 100%)";
            else if (laneH >= 62 && PanRect(i).Contains(lastPointer))
            {
                double pan = tracks[i].Channel.Pan;
                string value = Math.Abs(pan) < 0.01 ? "C" : $"{(pan < 0 ? "L" : "R")}{Math.Abs(pan) * 100:0}";
                tip = $"Paneo {value} (doble click: centro)";
            }
            else if (lastPointer.X >= HeaderW && ClipAt(i, lastPointer.X) is { } clip)
            {
                var mode = HitHandle(clip, lastPointer);
                var w = Window(clip);
                var f = ClipEnvelope.FitFades(clip.Clip.FadeInSec, clip.Clip.FadeOutSec, w.End - w.Start);
                tip = mode switch
                {
                    ClipEditMode.TrimStart => "Recortar inicio", ClipEditMode.TrimEnd => "Recortar final",
                    ClipEditMode.FadeIn => $"Fade in {f.FadeIn:0.00} s", ClipEditMode.FadeOut => $"Fade out {f.FadeOut:0.00} s",
                    _ => "Arrastrá para mover (Alt: sin imán; arriba/abajo: a otra pista) · bordes: recortar · esquinas de arriba: fades · Supr: quitar clip · click derecho: opciones",
                };
                if (w.AnchorCut > 0.0005 && X(w.End) - X(w.Start) > 60
                    && new Rect(X(w.Start) + 10, LaneY(i) + laneH - 24,
                        Palette.Text($"{w.AnchorCut:0.00} s", 10.5, Palette.Accent, Palette.Mono).Width + 22, 16).Contains(lastPointer))
                    tip = "Esta parte quedó antes del 0 y no suena ni se ve";
                cursor = mode == ClipEditMode.Move ? StandardCursorType.Hand : StandardCursorType.SizeWestEast;
            }
        }
        ToolTip.SetTip(this, tip);
        Cursor = new Cursor(cursor);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var p = e.GetPosition(this);
        int i = (int)Math.Floor((p.Y - RulerH + scrollY) / laneH);
        if (laneH >= 62 && p.Y >= RulerH && i >= 0 && i < tracks.Count && tracks[i].Clips.Count > 0 && PanRect(i).Contains(p))
        {
            double pan = Math.Clamp(tracks[i].Channel.Pan + e.Delta.Y * ((e.KeyModifiers & KeyModifiers.Shift) != 0 ? 0.01 : 0.05), -1, 1);
            ChannelRequested?.Invoke(tracks[i], null, Math.Abs(pan) < 0.04 ? 0 : pan);
        }
        else if ((e.KeyModifiers & (KeyModifiers.Shift | KeyModifiers.Control)) != 0)
        {
            double px = Playhead * pps - scrollX;
            if (px < 0 || px > Bounds.Width - HeaderW) px = (Bounds.Width - HeaderW) / 2;
            pps = Math.Clamp(pps * Math.Pow(1.18, e.Delta.Y), MinPps, 600);
            double fit = (Bounds.Width - HeaderW - 24) / Math.Max(8, LengthSec);
            fitMode = Math.Abs(pps / fit - 1) < 0.02;
            scrollX = Math.Max(0, Playhead * pps - px);
        }
        else if ((e.KeyModifiers & KeyModifiers.Alt) != 0)
        {
            SetTrackHeight(laneH + e.Delta.Y * 6);
        }
        else
        {
            if (p.X < HeaderW || LengthSec * pps <= Bounds.Width - HeaderW)
            {
                scrollY -= (e.Delta.Y + e.Delta.X) * 60;
                ClampScroll();
            }
            else scrollX = Math.Max(0, scrollX - (e.Delta.Y + e.Delta.X) * 80);
        }
        e.Handled = true;
        InvalidateVisual();
    }
}
