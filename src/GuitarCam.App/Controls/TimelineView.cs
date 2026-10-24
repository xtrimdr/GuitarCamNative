using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using GuitarCam.App.Model;
using GuitarCam.App.Video;
using GuitarCam.Projects;
using GuitarCam.Audio.Dsp;

namespace GuitarCam.App.Controls;

/// <summary>
/// The timeline: bar ruler, one lane per track with its clips and waveforms, sticky track headers
/// Port of GuitarCam/src/components/Timeline.tsx gestures, handles, snap and lane controls.
/// Native differences: an empty lane is just empty (the user asked to drop the web's dashed box and its "Apretá R" hint);
/// drag previews are local and commit on release (the window applies them in place, still playing);
/// multiple clips per track swap only with the clip under the pointer. Thumbnails are not ported yet.
/// </summary>
public sealed class TimelineView : Control
{
    /// <summary>The width the header rows are laid out for; a wider column gives the rest to the volume slider.</summary>
    const double LayoutHeaderW = 264;
    public const double MinHeaderW = 324, MaxHeaderW = 460;
    public const double RulerH = 32;
    /// <summary>Minimum px between minor ruler ticks before they get their own small number.</summary>
    const double MinorLabelPx = 30;
    const double BaseHeaderH = 64; // the header's fixed part (name, volume, pan); taller lanes add effect rows under it
    const double FxRowH = 22;
    /// <summary>Width of the track-header column; dragged from its right edge (native only).</summary>
    public double HeaderW { get; private set; } = MinHeaderW;
    public event Action<double>? HeaderWidthChanged;
    double originalHeaderW;
    int fxRow;
    /// <summary>The effect rows of the header, top to bottom: key (<see cref="FxIcon"/> icon too), label, default amount.</summary>
    static readonly (string Key, string Label, double Default)[] FxRows =
        [(FxIcon.Sat, Loc.T("fx.life"), 0.3), (FxIcon.Comp, Loc.T("fx.comp"), 0.5), (FxIcon.Reverb, "Reverb", 0), (FxIcon.Delay, "Delay", 0)];
    /// <summary>Channel effect edit from a header row: track, effect key, toggle on/off, new amount (an amount also turns it on).</summary>
    public event Action<LiveTrack, string, bool, double?>? EffectRequested;

    public void SetHeaderWidth(double w)
    {
        w = Math.Clamp(Math.Round(w), MinHeaderW, MaxHeaderW);
        if (w == HeaderW) return;
        HeaderW = w;
        ClampScroll();
        HeaderWidthChanged?.Invoke(w);
        InvalidateVisual();
    }

    int FxRowCount => laneH >= BaseHeaderH + FxRowH ? Math.Min(FxRows.Length, (int)((laneH - BaseHeaderH) / FxRowH)) : 0;
    Rect FxToggleRect(int i, int k) => new(48, LaneY(i) + BaseHeaderH + k * FxRowH, 74, 20);
    Rect FxSliderRect(int i, int k) => new(126, LaneY(i) + BaseHeaderH + k * FxRowH, Math.Max(20, HeaderW - 58 - 126), 20);
    (int Row, bool Slider)? FxHit(int i, Point p)
    {
        for (int k = 0; k < FxRowCount; k++)
        {
            if (FxToggleRect(i, k).Contains(p)) return (k, false);
            if (FxSliderRect(i, k).Inflate(new Thickness(4, 0)).Contains(p)) return (k, true);
        }
        return null;
    }

    static (bool On, double Amount) FxState(Channel ch, int k) => k switch
    {
        0 => (ch.Sat.On, ch.Sat.Amount),
        1 => (ch.Comp.On, ch.Comp.Amount),
        2 => (ch.ReverbOn, ch.ReverbSend),
        _ => (ch.DelayOn, ch.DelaySend),
    };

    IReadOnlyList<LiveTrack> tracks = [];
    double pps = 10; // pixels per second
    double scrollX;
    double laneH = 64;
    const double ShowThumbsAt = 52; // SHOW_THUMBS_AT: lower lanes have no room for the filmstrip
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
    /// <summary>The clip is over another track with Ctrl held: it swaps instead of landing at a new time.</summary>
    bool swapping;
    /// <summary>
    /// The range selected with Shift+drag on one track (native only), to delete what is inside it. It stays after the
    /// mouse is released until another click, Esc, an edit or the delete itself.
    /// </summary>
    string? rangeTrackId;
    double rangeStart, rangeEnd, rangeAnchor;
    KeyModifiers modifiers;
    double originalPan;
    readonly DispatcherTimer dragScroll;
    /// <summary>
    /// Where each track is drawn, in lanes, while it glides to its place during and after a reorder (native only:
    /// the web jumps). Absent = drawn at its slot.
    /// </summary>
    readonly Dictionary<string, double> shownSlot = [];
    readonly DispatcherTimer slide;
    /// <summary>Share of the remaining distance a gliding track covers per 16 ms frame (about 150 ms to settle).</summary>
    const double SlideEase = 0.3;
    /// <summary>The track dragged by its handle, or still gliding into place after the drop: drawn over the others.</summary>
    string? liftedId;
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
    bool showVideo = true;
    /// <summary>False in a solo audio project: clips show only their waveform, without the filmstrip.</summary>
    public bool ShowVideo { get => showVideo; set { if (showVideo == value) return; showVideo = value; waveCache.Clear(); InvalidateVisual(); } }
    IReadOnlySet<int>? recordingLanes;
    /// <summary>
    /// The tracks takes are going into while recording (tracks.Count = an extra row for a new track at the
    /// end); null when not recording. Like the web, the timeline only scrolls and zooms meanwhile.
    /// </summary>
    public IReadOnlySet<int>? RecordingLanes
    {
        get => recordingLanes;
        set { recordingLanes = value; CancelGesture(); ClampScroll(); InvalidateVisual(); }
    }
    bool Recording => recordingLanes != null;
    bool RecordingInto(int lane) => recordingLanes?.Contains(lane) == true;
    int ExtraRows => RecordingInto(tracks.Count) ? 1 : 0;
    IReadOnlySet<int> armed = new HashSet<int>();
    /// <summary>The tracks the next take goes to (armed by hand or picked by the window), tinted red; empty = a new track.</summary>
    public IReadOnlySet<int> ArmedIndices
    {
        get => armed;
        set { if (armed.SetEquals(value)) return; armed = value; InvalidateVisual(); }
    }
    /// <summary>The armed track's input as its header chip shows it ("1-2", "3"); set by the window.</summary>
    public Func<LiveTrack, string>? InputLabel { get; set; }
    /// <summary>The ● of a track header was clicked (native only: the web has no record arming).</summary>
    public event Action<LiveTrack>? ArmRequested;
    /// <summary>The input chip of an armed track was clicked: the window shows the input menu (native only).</summary>
    public event Action<LiveTrack, Point>? InputMenuRequested;
    public event Action<Clip, string?, string?>? ClipEditCommitted;
    public event Action<int, int>? ReorderRequested;
    public event Action<string>? RemoveTrackRequested;
    public event Action<LiveTrack, LiveClip?, Point>? ClipContextRequested;
    public event Action<Point>? NewTrackRequested;
    public event Action<LiveTrack, double?, double?>? ChannelRequested;
    public event Action<double>? TrackHeightChanged;

    /// <summary>The selected range of a track, if any: the window deletes what is inside it on Supr or from the menu.</summary>
    public (string TrackId, double Start, double End)? Range =>
        rangeTrackId != null && rangeEnd - rangeStart >= MinRangeSec ? (rangeTrackId, rangeStart, rangeEnd) : null;
    /// <summary>A Shift+drag shorter than this is a click, not a range.</summary>
    const double MinRangeSec = 0.02;
    /// <summary>The range was finished with the mouse: the window can offer to delete it.</summary>
    public event Action<string, double, double>? RangeSelected;

    public void ClearRange()
    {
        if (rangeTrackId == null) return;
        rangeTrackId = null;
        rangeStart = rangeEnd = 0;
        InvalidateVisual();
    }

    public event Action<double>? SeekRequested;
    public event Action<int>? SelectionChanged;
    /// <summary>Requests a channel edit from the window; true selects solo, false selects mute.</summary>
    public event Action<LiveTrack, bool>? TrackToggleRequested;

    public TimelineView()
    {
        ClipToBounds = true;
        Focusable = true;
        VideoThumbnails.Ready += InvalidateVisual;
        dragScroll = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Input, (_, _) =>
        {
            if (gesture is not ("clip" or "reorder" or "scrub" or "range")) return;
            double oldX = scrollX, oldY = scrollY;
            if (lastPointer.Y < RulerH + 36) scrollY -= Math.Ceiling((RulerH + 36 - lastPointer.Y) / 4);
            else if (lastPointer.Y > Bounds.Height - 36) scrollY += Math.Ceiling((lastPointer.Y - Bounds.Height + 36) / 4);
            if (gesture is "clip" or "scrub" or "range")
            {
                if (lastPointer.X > Bounds.Width - 16) scrollX += Math.Max(2, (lastPointer.X - Bounds.Width + 16) / 2);
                else if (lastPointer.X < HeaderW + 16) scrollX = Math.Max(0, scrollX - Math.Max(2, (HeaderW + 16 - lastPointer.X) / 2));
            }
            ClampScroll();
            if (oldX != scrollX || oldY != scrollY) UpdateGesture();
        });
        slide = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => Slide());
        DetachedFromVisualTree += (_, _) => { dragScroll.Stop(); slide.Stop(); };
        dragScroll.Stop();
        slide.Stop();
        layer = new Layer(this);
        VisualChildren.Add(layer);
    }

    /// <summary>
    /// What moves on every tick while audio runs, the playhead and the header meters, drawn over the timeline. Each
    /// tick repaints only this; repainting the whole timeline per frame (every clip, wave and label) made enough
    /// garbage for GCs that stalled the audio callback. Not hit-testable: the timeline under it takes the pointer.
    /// </summary>
    sealed class Layer : Control
    {
        readonly TimelineView owner;
        public Layer(TimelineView owner) { this.owner = owner; IsHitTestVisible = false; }
        public override void Render(DrawingContext ctx) => owner.RenderLayer(ctx);
    }

    readonly Layer layer;

    /// <summary>Repaints the timeline and its moving layer (the layer's positions follow scroll, zoom and lane
    /// height). Hides Visual.InvalidateVisual so every caller, inside or outside, keeps both in step.</summary>
    public new void InvalidateVisual()
    {
        base.InvalidateVisual();
        layer?.InvalidateVisual();
    }

    /// <summary>The meters move to the background while a header is lifted or sliding, so the lifted card covers its
    /// neighbours' meters as it covers the rest of their header.</summary>
    bool MetersOnLayer => liftedId == null && !slide.IsEnabled;

    public void SetTracks(IReadOnlyList<LiveTrack> list, bool preserveView = false)
    {
        tracks = list;
        waveCache.Clear();
        shownSlot.Clear();
        liftedId = null;
        if (!preserveView) SetFit(true);
        Selected = list.Count > 0 ? 0 : -1;
        if (!list.Any(t => t.Clips.Any(c => c.Clip.Id == SelectedClipId))) SelectedClipId = null;
        ClampScroll();
        InvalidateVisual();
    }

    /// <summary>After an edit applied in place: same view and selection, and only the waveforms of clips that left are dropped.</summary>
    public void UpdateTracks(IReadOnlyList<LiveTrack> list)
    {
        tracks = list;
        var live = list.SelectMany(t => t.Clips).ToHashSet();
        foreach (var gone in waveCache.Keys.Where(c => !live.Contains(c)).ToList()) waveCache.Remove(gone);
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
    /// <summary>Called every tick: repaints only when the playhead or a header meter moved, and then only the moving
    /// <see cref="Layer"/>, unless the view scrolled, a take is recording (its bar grows with the playhead) or the
    /// meters are in the background.</summary>
    public void SetPlayhead(double sec, bool follow)
    {
        bool meters = MetersMoved();
        if (sec == Playhead && !meters) return;
        Playhead = sec;
        double x = X(sec), oldScroll = scrollX;
        if (follow && (x > Bounds.Width - 40 || x < HeaderW)) scrollX = Math.Max(0, sec * pps - 40);
        if (scrollX != oldScroll || Recording || (meters && !MetersOnLayer)) InvalidateVisual();
        else layer.InvalidateVisual();
    }

    /// <summary>The header meters as last drawn, in steps finer than a pixel (see <see cref="DrawMeterBar"/>).</summary>
    int[] meterSteps = [];
    bool MetersMoved()
    {
        if (meterSteps.Length != tracks.Count * 2) meterSteps = new int[tracks.Count * 2];
        bool moved = false;
        for (int i = 0; i < tracks.Count; i++)
            for (int c = 0; c < 2; c++)
            {
                int step = MeterStep(c == 0 ? tracks[i].ShowL : tracks[i].ShowR);
                if (meterSteps[i * 2 + c] == step) continue;
                meterSteps[i * 2 + c] = step;
                moved = true;
            }
        return moved;
    }

    static int MeterStep(float peak)
    {
        double db = Palette.ToDb(peak);
        int step = (int)Math.Round(Math.Clamp((db + 48) / 48, 0, 1) * 200);
        return db > -1 ? -step - 1 : step;
    }

    /// <summary>True while the zoom follows the width ("Ver todo"); the toolbar disables its button then, like the web.</summary>
    public bool FitMode => fitMode;
    public event Action<bool>? FitModeChanged;
    void SetFit(bool on)
    {
        if (fitMode == on) return;
        fitMode = on;
        FitModeChanged?.Invoke(on);
    }

    public void FitAll()
    {
        double w = Bounds.Width - HeaderW - 24;
        if (w <= 0 || LengthSec <= 0) return;
        SetFit(true);
        pps = w / Math.Max(8, LengthSec);
        scrollX = 0;
        InvalidateVisual();
    }

    double X(double sec) => HeaderW + sec * pps - scrollX;

    double Sec(double x) => (x - HeaderW + scrollX) / pps;
    double LaneY(int i) => RulerH + ShownSlot(i) * laneH - scrollY;
    /// <summary>The dragged track follows the pointer, in lanes from the top.</summary>
    double DragSlot => Math.Clamp(fromIndex + (lastPointer.Y - down.Y + scrollY - downScrollY) / laneH, 0, Math.Max(0, tracks.Count - 1));
    /// <summary>Where a track belongs right now: its lane, or the one it makes room in while another is dragged.</summary>
    double TargetSlot(int i)
    {
        if (gesture != "reorder" || !moved || i >= tracks.Count) return i;
        if (i == fromIndex) return DragSlot;
        if (fromIndex < overIndex && i > fromIndex && i <= overIndex) return i - 1;
        if (fromIndex > overIndex && i >= overIndex && i < fromIndex) return i + 1;
        return i;
    }
    double ShownSlot(int i) => i < tracks.Count && shownSlot.TryGetValue(tracks[i].Track.Id, out double s) ? s : TargetSlot(i);

    void Slide()
    {
        var live = new HashSet<string>();
        for (int i = 0; i < tracks.Count; i++)
        {
            string id = tracks[i].Track.Id;
            live.Add(id);
            if (!shownSlot.TryGetValue(id, out double s)) continue;
            double target = TargetSlot(i);
            s += (target - s) * SlideEase;
            if (Math.Abs(target - s) < 0.01 || (gesture == "reorder" && i == fromIndex)) shownSlot.Remove(id);
            else shownSlot[id] = s;
        }
        foreach (var gone in shownSlot.Keys.Where(k => !live.Contains(k)).ToList()) shownSlot.Remove(gone);
        if (shownSlot.Count == 0)
        {
            slide.Stop();
            if (gesture != "reorder") liftedId = null;
        }
        InvalidateVisual();
    }

    /// <summary>The track indices in drawing order: the lifted one last, so it passes over the others.</summary>
    IEnumerable<int> DrawOrder()
    {
        int lifted = -1;
        for (int i = 0; i < tracks.Count; i++)
        {
            if (tracks[i].Track.Id == liftedId) { lifted = i; continue; }
            yield return i;
        }
        if (lifted >= 0) yield return lifted;
    }
    double Grid => ClipEditing.GridSeconds(snapMode, Bpm);
    double ViewSec => Math.Max(8, Math.Max(LengthSec, Playhead + 2));
    double MinPps => Math.Max(0.01, (Bounds.Width - HeaderW) / Math.Max(600, ViewSec * 8));
    void ClampScroll() => scrollY = Math.Clamp(scrollY, 0, Math.Max(0, (tracks.Count + ExtraRows) * laneH + 44 - (Bounds.Height - RulerH)));
    public void SetTrackHeight(double height)
    {
        double next = Math.Clamp(Math.Round(height / 2) * 2, 40, 160); // the web's slider steps by 2 px
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

        BuildGrid(W);
        using (ctx.PushClip(new Rect(HeaderW, RulerH, W - HeaderW, Math.Max(0, H - RulerH))))
        {
            foreach (int i in DrawOrder()) DrawLane(ctx, i, W);
            if (recordingLanes != null)
                foreach (int r in recordingLanes) DrawRecordingBar(ctx, r, W);
        }
        DrawRuler(ctx, W);

        // sticky headers
        ctx.FillRectangle(Palette.Panel, new Rect(0, 0, HeaderW, H));
        ctx.FillRectangle(Palette.Panel, new Rect(0, 0, HeaderW, RulerH));
        ctx.DrawLine(Palette.Pen(Palette.Line), new Point(0, RulerH - 0.5), new Point(W, RulerH - 0.5));
        using (ctx.PushClip(new Rect(0, RulerH, HeaderW, Math.Max(0, H - RulerH))))
            foreach (int i in DrawOrder()) DrawHeader(ctx, i);
        if (ExtraRows > 0) DrawRecordingHeader(ctx, LaneY(tracks.Count), W);
        double addY = LaneY(tracks.Count + ExtraRows);
        if (addY >= RulerH && addY < H)
        {
            ctx.FillRectangle(Palette.App, new Rect(0, addY, W, 44));
            ctx.DrawText("\uE710", 12, Palette.Dim, new Point(12, addY + 13), new Typeface("Segoe Fluent Icons, Segoe MDL2 Assets"));
            ctx.DrawText(Loc.T("timeline.newTrack"), 12, Palette.Dim, new Point(32, addY + 13));
            ctx.DrawLine(Palette.Pen(Palette.Line), new Point(0, addY + 43.5), new Point(W, addY + 43.5));
        }
        if (tracks.Count == 0 && !Recording)
        {
            ctx.DrawText(Loc.T("timeline.empty.title"), 12.5, Palette.Dim, new Point(HeaderW + 24, RulerH + 66), Palette.UiBold);
            ctx.DrawText(Loc.T("timeline.empty.a"), 12,
                Palette.Faint, new Point(HeaderW + 24, RulerH + 90));
            ctx.DrawText(Loc.T("timeline.empty.b"), 12,
                Palette.Faint, new Point(HeaderW + 24, RulerH + 108));
        }
        if (dragged != null && preview != null && overIndex >= 0 && overIndex != fromIndex && !swapping)
            DrawGhost(ctx, W, H);
        else if (dragged != null && overIndex >= 0 && overIndex != fromIndex)
        {
            var r = new Rect(HeaderW + 1, LaneY(overIndex) + 1, Math.Max(1, W - HeaderW - 2), laneH - 2);
            ctx.DrawRectangle(Palette.Alpha(Palette.AccentColor, 0.1), Palette.Pen(Palette.Accent, 2), r);
            string text = swapId == null ? Loc.T("timeline.moveTo", tracks[overIndex].Track.Name) : Loc.T("timeline.swapWith", tracks[overIndex].Track.Name);
            ctx.DrawText(text, 11, Palette.Accent, new Point(Math.Max(HeaderW + 10, W - 280), r.Y + 8), Palette.UiBold);
        }
        ctx.DrawLine(Palette.Pen(Palette.Line), new Point(HeaderW - 0.5, 0), new Point(HeaderW - 0.5, H));
    }

    /// <summary>
    /// The "ghost" of a clip carried to another track (native only): a dashed outline where it will land, on the
    /// lane under the pointer and at the snapped time, with the track and the time it will start at. Drawn over the
    /// lanes and under the sticky headers, so it never covers the header column.
    /// </summary>
    void DrawGhost(DrawingContext ctx, double W, double H)
    {
        var w = Window(dragged!, preview);
        double y = LaneY(overIndex);
        ctx.FillRectangle(Palette.Alpha(Palette.AccentColor, 0.06), new Rect(HeaderW, y, Math.Max(0, W - HeaderW), laneH));
        double x0 = X(w.Start), x1 = X(w.End);
        var box = new Rect(x0, y + 5, Math.Max(2, x1 - x0), laneH - 10);
        using (ctx.PushClip(new Rect(HeaderW, RulerH, Math.Max(0, W - HeaderW), Math.Max(0, H - RulerH))))
        {
            var dashed = new Pen(Palette.Accent, 1.5, new DashStyle([4, 3], 0));
            ctx.DrawRectangle(Palette.Alpha(Palette.AccentColor, 0.18), dashed, box, 5, 5);
            var label = Palette.Text($"{tracks[overIndex].Track.Name} · {w.Start:0.00} s", 11, Palette.Accent, Palette.UiBold);
            double lx = Math.Clamp(box.X + 8, HeaderW + 8, Math.Max(HeaderW + 8, W - label.Width - 8));
            ctx.DrawRectangle(Palette.Alpha(Palette.AppColor, 0.75), null, new Rect(lx - 4, box.Y + 3, label.Width + 8, label.Height + 2), 3, 3);
            ctx.DrawText(label, new Point(lx, box.Y + 4));
        }
    }

    /// <summary>The selected range over its lane: a band of the accent with the length on top.</summary>
    void DrawRange(DrawingContext ctx, double y, double W, double start, double end)
    {
        double x0 = Math.Max(HeaderW, X(start)), x1 = Math.Min(W, X(end));
        if (x1 <= x0) return;
        var band = new Rect(x0, y + 1, x1 - x0, laneH - 2);
        ctx.FillRectangle(Palette.Alpha(Palette.AccentColor, 0.22), band);
        var pen = Palette.Pen(Palette.Accent, 1.5);
        if (X(start) >= HeaderW) ctx.DrawLine(pen, new Point(X(start), y + 1), new Point(X(start), y + laneH - 1));
        if (X(end) <= W) ctx.DrawLine(pen, new Point(X(end), y + 1), new Point(X(end), y + laneH - 1));
        var label = Palette.Text($"{end - start:0.00} s", 10.5, Palette.Accent, Palette.Mono);
        if (band.Width > label.Width + 12)
        {
            ctx.DrawRectangle(Palette.Alpha(Palette.AppColor, 0.75), null, new Rect(band.X + 4, band.Y + 3, label.Width + 8, label.Height + 2), 3, 3);
            ctx.DrawText(label, new Point(band.X + 8, band.Y + 4));
        }
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
        var text = Palette.Text(Loc.T("timeline.recording"), 13, Palette.Rec);
        ctx.DrawText(text, new Point(42, y + (laneH - text.Height) / 2));
        ctx.DrawLine(Palette.Pen(Palette.Line), new Point(0, y + laneH - 0.5), new Point(W, y + laneH - 0.5));
    }

    void DrawRuler(DrawingContext ctx, double W)
    {
        ctx.FillRectangle(Palette.Panel, new Rect(HeaderW, 0, W - HeaderW, RulerH));
        if (!bars)
        {
            double[] steps = [0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800];
            int[] divs = [5, 5, 5, 4, 5, 5, 3, 6, 6, 4, 5, 5, 6];
            int si = Array.FindIndex(steps, s => s * pps >= 64);
            if (si < 0) si = steps.Length - 1;
            double seconds = steps[si];
            int div = divs[si];
            var minorPen = Palette.Pen(Palette.Alpha(Palette.LineColor, 0.7));
            for (double s = Math.Max(0, Math.Floor(Sec(HeaderW) / seconds) * seconds); X(s) <= W; s += seconds)
            {
                double x = X(s);
                if (seconds * pps / div >= 6)
                    for (int k = 1; k < div; k++)
                    {
                        double mx = X(s + k * seconds / div);
                        ctx.DrawLine(minorPen, new Point(mx, RulerH - 6), new Point(mx, RulerH));
                        if (seconds * pps / div >= MinorLabelPx)
                        {
                            double ms = s + k * seconds / div;
                            string ml = ms >= 60 ? $"{(int)(ms / 60)}:{ms % 60:00.##}" : $"{ms:0.##}";
                            ctx.DrawText(ml, 10, Palette.Faint, new Point(mx + 3, 17), Palette.Mono);
                        }
                    }
                ctx.DrawLine(Palette.Pen(Palette.Line), new Point(x, 3), new Point(x, RulerH));
                string label = s >= 60 ? $"{(int)(s / 60)}:{s % 60:00}" : $"{s:0.##}s";
                ctx.DrawText(label, 15, Palette.Fg, new Point(x + 4, 3), Palette.MonoBold);
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
            ctx.DrawLine(tick, new Point(x, 3), new Point(x, RulerH));
            ctx.DrawText((b + 1).ToString(), 15, Palette.Fg, new Point(x + 4, 3), Palette.MonoBold);
            // minor ticks: beats inside a bar, or single bars when the labels skip some
            double sub = step == 1 ? beat : bar;
            int subN = step == 1 ? 4 : step;
            if (sub * pps >= 6)
                for (int k = 1; k < subN; k++)
                {
                    double bx = X(b * bar + k * sub);
                    ctx.DrawLine(beatPen, new Point(bx, RulerH - 6), new Point(bx, RulerH));
                    if (sub * pps >= MinorLabelPx)
                        ctx.DrawText((step == 1 ? k + 1 : b + k + 1).ToString(), 10, Palette.Faint, new Point(bx + 3, 17), Palette.Mono);
                }
        }
    }

    // x of every ruler tick in view (major = labelled step, minor = beat / subdivision), rebuilt each Render so the
    // lanes can draw the same ticks as faint vertical lines behind their clips without allocating per lane
    readonly List<(double X, bool Major)> grid = [];

    /// <summary>Collects the ruler's tick positions (same steps and thresholds as <see cref="DrawRuler"/>) into <see cref="grid"/>.</summary>
    void BuildGrid(double W)
    {
        grid.Clear();
        if (!bars)
        {
            double[] steps = [0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800];
            int[] divs = [5, 5, 5, 4, 5, 5, 3, 6, 6, 4, 5, 5, 6];
            int si = Array.FindIndex(steps, s => s * pps >= 64);
            if (si < 0) si = steps.Length - 1;
            double seconds = steps[si];
            int div = divs[si];
            for (double s = Math.Max(0, Math.Floor(Sec(HeaderW) / seconds) * seconds); X(s) <= W; s += seconds)
            {
                grid.Add((X(s), true));
                if (seconds * pps / div >= 6)
                    for (int k = 1; k < div; k++) grid.Add((X(s + k * seconds / div), false));
            }
            return;
        }
        double beat = 60 / Bpm, bar = 4 * beat;
        int step = 1;
        while (step * bar * pps < 56) step *= 2;
        int first = Math.Max(0, (int)(Sec(HeaderW) / bar) - 1);
        for (int b = first - first % step; ; b += step)
        {
            double x = X(b * bar);
            if (x > W) break;
            grid.Add((x, true));
            double sub = step == 1 ? beat : bar;
            int subN = step == 1 ? 4 : step;
            if (sub * pps >= 6)
                for (int k = 1; k < subN; k++) grid.Add((X(b * bar + k * sub), false));
        }
    }

    void DrawLane(DrawingContext ctx, int i, double W)
    {
        var t = tracks[i];
        double y = LaneY(i);
        if (y + laneH < RulerH || y > Bounds.Height) return;
        bool sel = i == Selected;
        // the lifted lane is opaque so the lanes it passes over don't show through
        if (t.Track.Id == liftedId) ctx.FillRectangle(Palette.App, new Rect(HeaderW, y, W - HeaderW, laneH));
        if (sel) ctx.FillRectangle(Palette.Alpha(Palette.RaisedColor, 0.6), new Rect(HeaderW, y, W - HeaderW, laneH));
        if (armed.Contains(i) && !Recording) ctx.FillRectangle(Palette.Alpha(Palette.RecColor, 0.06), new Rect(HeaderW, y, W - HeaderW, laneH));
        var majorPen = Palette.Pen(Palette.Alpha(Palette.LineColor, 0.55));
        var minorPen = Palette.Pen(Palette.Alpha(Palette.LineColor, 0.22));
        foreach (var (gx, major) in grid)
            if (gx >= HeaderW) ctx.DrawLine(major ? majorPen : minorPen, new Point(gx, y), new Point(gx, y + laneH));
        foreach (var c in t.Clips) DrawClip(ctx, t, c, y, sel, W);
        if (Range is { } r && r.TrackId == t.Track.Id) DrawRange(ctx, y, W, r.Start, r.End);
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
        // the clip being carried to another track stays dimmed where it was until it is dropped
        bool carried = dragged == c && overIndex >= 0 && overIndex != fromIndex && !swapping;
        using var opacity = ctx.PushOpacity((!c.Offline && silent ? 0.5 : 1) * (carried ? 0.35 : 1));
        // the web shows the filmstrip from SHOW_THUMBS_AT; audio-only media (native only) keeps the plain clip
        bool thumbs = showVideo && t.Track.Video && !c.Offline && laneH >= ShowThumbsAt && c.VideoPath != null;
        var fill = c.Offline ? Palette.Alpha(color, silent ? 0.05 : sel ? 0.16 : 0.10) : thumbs ? null : Palette.Alpha(Palette.HoverColor, 0.4);
        var border = Palette.Pen(c.Offline ? Palette.Alpha(color, sel ? 0.9 : 0.45)
            : sel ? Palette.Alpha(Palette.AccentColor, 0.6) : Palette.Line, c.Offline && sel ? 1.5 : 1);
        ctx.DrawRectangle(fill, border, clip, 5, 5);
        using (ctx.PushClip(new RoundedRect(clip, 5)))
        {
            if (c.Offline)
            {
                ctx.DrawText(c.Name, 11, sel ? Palette.Fg : Palette.Dim, new Point(Math.Max(x0, HeaderW) + 8, y + 7), Palette.UiBold);
                ctx.DrawText(Loc.T("clip.offline"), 11, Palette.Faint, new Point(Math.Max(x0, HeaderW) + 8, y + 26), Palette.Ui);
            }
            else
            {
                if (thumbs)
                {
                    DrawFilmstrip(ctx, c, model, clip, W);
                    // the wave sits on a dark gradient over the bottom 55 % of the clip
                    var shade = new Rect(clip.X, clip.Bottom - clip.Height * 0.55, clip.Width, clip.Height * 0.55);
                    ctx.DrawRectangle(new LinearGradientBrush
                    {
                        StartPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                        EndPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                        GradientStops =
                        {
                            new GradientStop(Color.FromArgb(204, 0, 0, 0), 0),
                            new GradientStop(Color.FromArgb(128, 0, 0, 0), 0.5),
                            new GradientStop(Color.FromArgb(0, 0, 0, 0), 1),
                        },
                    }, null, shade);
                    ctx.DrawRectangle(null, border, clip.Deflate(0.5), 4.5, 4.5); // over the frames, inside the clip
                }
                var geo = Waveform(c, model, w, thumbs ? clip.Bottom - clip.Height * 0.55 : y + 6, y + laneH - 6, W);
                var wave = silent ? Palette.Faint : sel ? Palette.Accent : thumbs ? Palette.Alpha(Colors.White, 0.85) : Palette.Alpha(Palette.DimColor, 0.8);
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

    /// <summary>
    /// The web's Filmstrip: the whole source (trims only hide its ends) split into as many 16:9 cells as fit at
    /// the lane's height, each showing the cached frame nearest its middle, cover-fitted and mirrored like the clip.
    /// </summary>
    void DrawFilmstrip(DrawingContext ctx, LiveClip c, Clip model, Rect clip, double W)
    {
        if (VideoThumbnails.Get(c.VideoPath!, c.LengthSec) is not { Length: > 0 } frames) return;
        double x0 = X(model.AtSec), srcW = c.LengthSec * pps;
        double h = laneH - 12;
        int slots = Math.Max(1, (int)Math.Round(srcW / (h * 16 / 9)));
        double cell = srcW / slots;
        double left = Math.Max(clip.Left, HeaderW), right = Math.Min(clip.Right, W);
        int first = Math.Max(0, (int)Math.Floor((left - x0) / cell)), last = Math.Min(slots - 1, (int)Math.Floor((right - x0) / cell));
        var divider = Palette.Pen(Palette.Alpha(Colors.Black, 0.4)); // border-black/40
        for (int i = first; i <= last; i++)
        {
            var img = frames[Math.Min(frames.Length - 1, (int)Math.Floor((i + 0.5) / slots * frames.Length))];
            var dest = new Rect(x0 + i * cell, clip.Top, cell, clip.Height);
            double iw = img.Size.Width, ih = img.Size.Height;
            double scale = Math.Max(dest.Width / iw, dest.Height / ih); // object-cover
            double sw = dest.Width / scale, sh = dest.Height / scale;
            var src = new Rect((iw - sw) / 2, (ih - sh) / 2, sw, sh);
            if (model.Framing.Mirror)
            {
                using (ctx.PushTransform(Matrix.CreateTranslation(-dest.Center.X, 0) * Matrix.CreateScale(-1, 1) * Matrix.CreateTranslation(dest.Center.X, 0)))
                    ctx.DrawImage(img, src, dest);
            }
            else ctx.DrawImage(img, src, dest);
            if (i < slots - 1) ctx.DrawLine(divider, new Point(dest.Right - 0.5, dest.Top), new Point(dest.Right - 0.5, dest.Bottom));
        }
    }

    /// <summary>Min/max per pixel column, cached until the zoom, scroll or size changes.</summary>
    StreamGeometry Waveform(LiveClip c, Clip model, ClipEnvelope.ClipWindow w, double top, double bottom, double W)
    {
        var a = c.Audio!;
        double key = pps * 1e6 + scrollX * 7.3 + W * 0.013 + top * 0.0007 + bottom * 0.00011 + laneH
            + model.AtSec * 1.7e-3 + model.TrimStartSec * 3.1e-3 + model.TrimEndSec * 5.3e-3;
        if (waveCache.TryGetValue(c, out var hit) && hit.Key == key) return hit.Geo;
        var geo = new StreamGeometry();
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

    Rect ArmRect(int i) => new(HeaderW - 122, LaneY(i) + 8, 24, 24);
    Rect InputRect(int i) => new(HeaderW - 166, LaneY(i) + 8, 40, 24);
    Rect MuteRect(int i) => new(HeaderW - 94, LaneY(i) + 8, 24, 24);
    Rect SoloRect(int i) => new(HeaderW - 66, LaneY(i) + 8, 24, 24);
    Rect RemoveRect(int i) => new(HeaderW - 38, LaneY(i) + 8, 24, 24);
    // a wider column gives its extra width to the volume slider and moves the pan knob with it
    double Extra => HeaderW - LayoutHeaderW;
    Rect VolumeRect(int i) => new(52, LaneY(i) + 40, 92 + Extra, 20);
    Rect PanRect(int i) => new(186 + Extra, LaneY(i) + 40, 24, 24);

    void DrawHeader(DrawingContext ctx, int i)
    {
        var t = tracks[i];
        double y = LaneY(i);
        if (y + laneH < RulerH || y > Bounds.Height) return;
        bool sel = i == Selected;
        ctx.FillRectangle(sel ? Palette.Raised : Palette.Panel, new Rect(0, y, HeaderW, laneH));
        bool isArmed = armed.Contains(i);
        if (isArmed || RecordingInto(i)) ctx.FillRectangle(Palette.Alpha(Palette.RecColor, 0.10), new Rect(0, y, HeaderW, laneH));
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
        // an armed track shows its input left of the ●, so the name gets shorter
        bool chip = isArmed && !Recording && InputLabel != null;
        double nameW = chip ? InputRect(i).X - 56 : HeaderW - 180;
        if (RecordingInto(i))
        {
            ctx.DrawText(Loc.T("timeline.recording"), 13, Palette.Rec, new Point(52, y + 11), Palette.Ui, HeaderW - 180);
            ctx.DrawEllipse(Palette.Rec, null, new Point(RemoveRect(i).Center.X, y + 20), 4, 4);
        }
        else ctx.DrawText(t.Track.Name, 12.5, Palette.Fg, new Point(52, y + 11), Palette.UiBold, nameW);
        if (chip) DrawInputChip(ctx, InputRect(i), InputLabel!(t));

        if (!RecordingInto(i) && lastPointer.Y >= y && lastPointer.Y < y + laneH)
            ctx.DrawText("×", 16, Palette.Faint, new Point(RemoveRect(i).X + 6, y + 7));
        ctx.DrawLine(Palette.Pen(Palette.Line), new Point(0, y + laneH - 0.5), new Point(HeaderW, y + laneH - 0.5));
        DrawArm(ctx, ArmRect(i), isArmed || RecordingInto(i));

        var ch = t.Channel;
        string pan = Math.Abs(ch.Pan) < 0.01 ? "C" : ch.Pan < 0 ? $"L{-ch.Pan * 100:0}" : $"R{ch.Pan * 100:0}";
        if (laneH >= 62)
        {
            var v = VolumeRect(i);
            double px = v.X + Math.Clamp(ch.Volume / 1.5, 0, 1) * v.Width;
            ctx.DrawLine(Palette.Pen(Palette.Line, 3), new Point(v.X, v.Center.Y), new Point(v.Right, v.Center.Y));
            ctx.DrawLine(Palette.Pen(Palette.Accent, 3), new Point(v.X, v.Center.Y), new Point(px, v.Center.Y));
            ctx.DrawEllipse(Palette.Accent, null, new Point(px, v.Center.Y), 4, 4);
            ctx.DrawText($"{ch.Volume * 100:0}", 10.5, Palette.Faint, new Point(148 + Extra, y + 43), Palette.Mono);
            var pr = PanRect(i);
            ctx.DrawEllipse(Palette.Raised, Palette.Pen(Palette.Line), pr.Center, 10, 10);
            double angle = ch.Pan * Math.PI * 0.75 - Math.PI / 2;
            ctx.DrawLine(Palette.Pen(Palette.Accent, 2), pr.Center,
                new Point(pr.Center.X + Math.Cos(angle) * 8, pr.Center.Y + Math.Sin(angle) * 8));
            ctx.DrawText(pan, 10.5, Palette.Faint, new Point(216 + Extra, y + 43), Palette.Mono);
        }

        // effect rows: as many as the lane height has room for, empty tracks included (they are channel settings)
        for (int k = 0; k < FxRowCount; k++)
        {
            var (on, amount) = FxState(ch, k);
            var tg = FxToggleRect(i, k);
            var brush = on ? Palette.Accent : Palette.Faint;
            FxIcon.Draw(ctx, FxRows[k].Key, new Rect(tg.X, tg.Center.Y - 7, 14, 14), brush);
            var label = Palette.Text(FxRows[k].Label, 10.5, on ? Palette.Fg : Palette.Faint, Palette.Ui);
            ctx.DrawText(label, new Point(tg.X + 20, tg.Center.Y - label.Height / 2));
            var s = FxSliderRect(i, k);
            double fx = s.X + Math.Clamp(amount, 0, 1) * s.Width;
            ctx.DrawLine(Palette.Pen(Palette.Line, 3), new Point(s.X, s.Center.Y), new Point(s.Right, s.Center.Y));
            ctx.DrawLine(Palette.Pen(on ? Palette.Accent : Palette.Faint, 3), new Point(s.X, s.Center.Y), new Point(fx, s.Center.Y));
            ctx.DrawEllipse(on ? Palette.Accent : Palette.Faint, null, new Point(fx, s.Center.Y), 4, 4);
            ctx.DrawText($"{amount * 100:0}", 10.5, Palette.Faint, new Point(s.Right + 10, s.Center.Y - 7), Palette.Mono);
        }

        DrawToggle(ctx, MuteRect(i), "M", ch.Muted, Palette.Brush("#f59e0b"));
        DrawToggle(ctx, SoloRect(i), "S", ch.Solo, Palette.Accent);

        if (!MetersOnLayer) DrawMeters(ctx, i);
    }

    /// <summary>The header's meter: two thin bars at the right edge.</summary>
    void DrawMeters(DrawingContext ctx, int i)
    {
        double y = LaneY(i);
        if (y + laneH < RulerH || y > Bounds.Height) return;
        double mh = laneH - 20, mx = HeaderW - 10;
        DrawMeterBar(ctx, new Rect(mx, y + 10, 4, mh), tracks[i].ShowL);
        DrawMeterBar(ctx, new Rect(mx + 6, y + 10, 4, mh), tracks[i].ShowR);
    }

    /// <summary>Draws the <see cref="Layer"/>: the header meters (unless they are in the background) and the playhead,
    /// which stops at the "Nueva pista" row like it did when the row was painted over it.</summary>
    void RenderLayer(DrawingContext ctx)
    {
        double W = Bounds.Width, H = Bounds.Height;
        if (MetersOnLayer)
            using (ctx.PushClip(new Rect(0, RulerH, HeaderW, Math.Max(0, H - RulerH))))
                for (int i = 0; i < tracks.Count; i++) DrawMeters(ctx, i);

        using (ctx.PushClip(new Rect(HeaderW, 0, Math.Max(0, W - HeaderW), H)))
        {
            double px = X(Playhead);
            var playColor = Recording ? Palette.Rec : Palette.Accent;
            var pen = Palette.Pen(playColor, 1.5);
            double addY = LaneY(tracks.Count + ExtraRows);
            if (addY >= RulerH && addY < H)
            {
                ctx.DrawLine(pen, new Point(px, 0), new Point(px, addY));
                if (addY + 44 < H) ctx.DrawLine(pen, new Point(px, addY + 44), new Point(px, H));
            }
            else ctx.DrawLine(pen, new Point(px, 0), new Point(px, H));
            using (ctx.PushTransform(Matrix.CreateTranslation(px, 0)))
                ctx.DrawGeometry(playColor, null, PlayheadTriangle);
        }
    }

    /// <summary>The playhead's head in the ruler, at x = 0 (moved into place when drawn).</summary>
    static readonly Geometry PlayheadTriangle = Geometry.Parse("M -6,0 L 6,0 L 0,8 Z");

    /// <summary>The record-arm toggle: a red dot, lit red while the track takes the next take.</summary>
    static void DrawArm(DrawingContext ctx, Rect r, bool on)
    {
        ctx.DrawRectangle(on ? Palette.Rec : Palette.Hover, on ? null : Palette.Pen(Palette.Line), r, 4, 4);
        ctx.DrawEllipse(on ? Palette.Fg : Palette.Alpha(Palette.RecColor, 0.7), null, r.Center, 4.5, 4.5);
    }

    /// <summary>The armed track's input: a small outlined chip with the input and a caret, like a select.</summary>
    static void DrawInputChip(DrawingContext ctx, Rect r, string label)
    {
        ctx.DrawRectangle(Palette.Hover, Palette.Pen(Palette.Alpha(Palette.RecColor, 0.45)), r, 4, 4);
        var t = Palette.Text(label, 10.5, Palette.Fg, Palette.Mono);
        double x = r.X + 6;
        ctx.DrawText(t, new Point(x, r.Center.Y - t.Height / 2));
        var caret = Palette.Text("\uE70D", 8, Palette.Dim, new Typeface("Segoe Fluent Icons, Segoe MDL2 Assets"));
        ctx.DrawText(caret, new Point(r.Right - caret.Width - 5, r.Center.Y - caret.Height / 2));
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
        if (Recording) { e.Handled = true; return; }
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
        ClearRange();
        if (p.X >= HeaderW - 3 && p.X <= HeaderW + 4)
        {
            originalHeaderW = HeaderW;
            StartGesture("header", e);
            return;
        }
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
        // any touch on a header (its handle or a control) selects that track first, except the remove button
        if (p.X < HeaderW && Selected != i && !RemoveRect(i).Contains(p)) Select(i);
        if (p.X < 24)
        {
            liftedId = tracks[i].Track.Id;
            shownSlot.Remove(liftedId);
            StartGesture("reorder", e);
            return;
        }
        if (p.X < HeaderW && RemoveRect(i).Contains(p))
        {
            RemoveTrackRequested?.Invoke(tracks[i].Track.Id);
            return;
        }
        if (p.X < HeaderW && ArmRect(i).Contains(p))
        {
            ArmRequested?.Invoke(tracks[i]);
            e.Handled = true;
            return;
        }
        if (p.X < HeaderW && armed.Contains(i) && InputLabel != null && InputRect(i).Contains(p))
        {
            Select(i);
            InputMenuRequested?.Invoke(tracks[i], InputRect(i).BottomLeft + new Point(0, 4));
            e.Handled = true;
            return;
        }
        if (p.X < HeaderW && MuteRect(i).Contains(p))
        {
            TrackToggleRequested?.Invoke(tracks[i], false);
        }
        else if (p.X < HeaderW && SoloRect(i).Contains(p))
        {
            TrackToggleRequested?.Invoke(tracks[i], true);
        }
        else if (p.X < HeaderW && FxHit(i, p) is { } hit)
        {
            fxRow = hit.Row;
            string fx = FxRows[hit.Row].Key;
            if (!hit.Slider) EffectRequested?.Invoke(tracks[i], fx, true, null);
            else if (e.ClickCount == 2) EffectRequested?.Invoke(tracks[i], fx, false, FxRows[hit.Row].Default);
            else { StartGesture("fx", e); UpdateGesture(); }
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
        else if (p.X >= HeaderW && (modifiers & KeyModifiers.Shift) != 0 && e.ClickCount == 1)
        {
            // Shift+drag over a lane selects a range of that track (native only)
            Select(i);
            rangeTrackId = tracks[i].Track.Id;
            rangeAnchor = rangeStart = rangeEnd = Math.Max(0, ClipEditing.Snap(Sec(p.X), Grid, (modifiers & KeyModifiers.Alt) != 0));
            StartGesture("range", e);
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
        if (kind is "clip" or "reorder" or "scrub" or "range") dragScroll.Start();
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
        else if (gesture == "header")
            SetHeaderWidth(originalHeaderW + lastPointer.X - down.X);
        else if (gesture == "fx")
        {
            var r = FxSliderRect(fromIndex, fxRow);
            EffectRequested?.Invoke(tracks[fromIndex], FxRows[fxRow].Key, false, Math.Clamp((lastPointer.X - r.X) / r.Width, 0, 1));
        }
        else if (gesture == "pan-knob")
        {
            double pan = Math.Clamp(originalPan - (lastPointer.Y - down.Y) / 80, -1, 1);
            ChannelRequested?.Invoke(tracks[fromIndex], null, Math.Abs(pan) < 0.04 ? 0 : pan);
        }
        else if (gesture == "range")
        {
            double at = Math.Max(0, ClipEditing.Snap(Sec(lastPointer.X), Grid, (modifiers & KeyModifiers.Alt) != 0));
            (rangeStart, rangeEnd) = (Math.Min(rangeAnchor, at), Math.Max(rangeAnchor, at));
        }
        else
        {
            if (!moved && Math.Abs(dxPx) < 3 && Math.Abs(lastPointer.Y - down.Y) < 3) return;
            moved = true;
            if (gesture == "reorder")
            {
                int over = (int)Math.Floor(DragSlot + 0.5);
                if (over != overIndex)
                {
                    // the tracks that make room glide from where they are drawn now
                    for (int k = 0; k < tracks.Count; k++)
                        if (k != fromIndex) shownSlot.TryAdd(tracks[k].Track.Id, TargetSlot(k));
                    overIndex = over;
                    slide.Start();
                }
            }
            else if (dragged != null && original != null)
            {
                int lane = (int)Math.Floor((lastPointer.Y - RulerH + scrollY) / laneH);
                overIndex = dragMode == ClipEditMode.Move && lane >= 0 && lane < tracks.Count ? lane : fromIndex;
                // Ctrl over another track swaps with the clip under the pointer, as in the web (the time stays); without
                // it the clip lands on that track at the time under the pointer, shown by a ghost (native only).
                swapping = overIndex != fromIndex && (modifiers & KeyModifiers.Control) != 0;
                swapId = swapping ? ClipAt(overIndex, lastPointer.X)?.Clip.Id : null;
                if (swapping) preview = ClipEditing.Copy(original);
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
        if (kind == "range")
        {
            if (Range is { } r) RangeSelected?.Invoke(r.TrackId, r.Start, r.End);
            else ClearRange();
        }
        if (kind == "clip" && changed && value != null) ClipEditCommitted?.Invoke(value, target, swap);
        if (kind == "reorder" && changed && from != to) ReorderRequested?.Invoke(from, to);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        ClearGesture();
    }

    public void CancelGesture()
    {
        ClearRange();
        ClearGesture();
    }
    void ClearGesture()
    {
        if (gesture == "reorder" && moved)
        {
            // keep every track where it is drawn; once the window applies the new order they glide to their lanes
            for (int k = 0; k < tracks.Count; k++) shownSlot[tracks[k].Track.Id] = ShownSlot(k);
            slide.Start();
        }
        else if (gesture == "reorder") liftedId = null;
        gesture = null;
        var pointer = capturedPointer;
        capturedPointer = null;
        dragged = null;
        original = preview = null;
        swapId = null;
        swapping = false;
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
        if (lastPointer.X >= HeaderW - 3 && lastPointer.X <= HeaderW + 4)
        {
            tip = Loc.T("tl.tip.headerWidth");
            cursor = StandardCursorType.SizeWestEast;
        }
        else if (lastPointer.Y < RulerH && lastPointer.X >= HeaderW)
            tip = Loc.T("tl.tip.ruler");
        else if (i >= 0 && i < tracks.Count)
        {
            if (lastPointer.X < 24) { tip = Loc.T("tl.tip.reorder"); cursor = StandardCursorType.Hand; }
            else if (ArmRect(i).Contains(lastPointer))
                tip = armed.Contains(i) ? Loc.T("tl.tip.armed", Shortcuts.Paren("record"))
                    : tracks[i].Clips.Count == 0 ? Loc.T("tl.tip.arm.empty") + Shortcuts.Note("arm", Loc.T("tl.note.arm"))
                    : Loc.T("tl.tip.arm.replace") + Shortcuts.Note("arm", Loc.T("tl.note.arm"));
            else if (armed.Contains(i) && !Recording && InputLabel != null && InputRect(i).Contains(lastPointer))
                tip = Loc.T("tl.tip.input");
            else if (RemoveRect(i).Contains(lastPointer)) tip = tracks[i].Clips.Count == 0 ? Shortcuts.Tip(Loc.T("tl.removeTrack"), "delete") : Loc.T("tl.tip.removeTrack");
            else if (MuteRect(i).Contains(lastPointer)) tip = tracks[i].Channel.Muted ? Loc.T("menu.unmute") : Loc.T("tl.tip.mute");
            else if (SoloRect(i).Contains(lastPointer)) tip = tracks[i].Channel.Solo ? Loc.T("menu.unsolo") : Loc.T("tl.tip.solo");
            else if (lastPointer.X < HeaderW && FxHit(i, lastPointer) is { } hit)
            {
                var (on, amount) = FxState(tracks[i].Channel, hit.Row);
                string name = FxRows[hit.Row].Label;
                tip = hit.Slider ? Loc.T("tl.tip.fxSlider", name, $"{amount * 100:0}", $"{FxRows[hit.Row].Default * 100:0}")
                    : on ? Loc.T("tl.tip.fxOn", name) : Loc.T("tl.tip.fxOff", name);
            }
            else if (laneH >= 62 && VolumeRect(i).Contains(lastPointer)) tip = Loc.T("tl.tip.volume", $"{tracks[i].Channel.Volume * 100:0}");
            else if (laneH >= 62 && PanRect(i).Contains(lastPointer))
            {
                double pan = tracks[i].Channel.Pan;
                string value = Math.Abs(pan) < 0.01 ? "C" : $"{(pan < 0 ? "L" : "R")}{Math.Abs(pan) * 100:0}";
                tip = Loc.T("tl.tip.pan", value);
            }
            else if (lastPointer.X >= HeaderW && ClipAt(i, lastPointer.X) is { } clip)
            {
                var mode = HitHandle(clip, lastPointer);
                var w = Window(clip);
                var f = ClipEnvelope.FitFades(clip.Clip.FadeInSec, clip.Clip.FadeOutSec, w.End - w.Start);
                tip = mode switch
                {
                    ClipEditMode.TrimStart => Loc.T("tl.tip.trimStart"), ClipEditMode.TrimEnd => Loc.T("tl.tip.trimEnd"),
                    ClipEditMode.FadeIn => Loc.T("tl.tip.fadeIn", $"{f.FadeIn:0.00}"), ClipEditMode.FadeOut => Loc.T("tl.tip.fadeOut", $"{f.FadeOut:0.00}"),
                    _ => Loc.T("tl.tip.clip")
                        + Shortcuts.Note("split", Loc.T("tl.note.split")) + Shortcuts.Note("duplicate", Loc.T("tl.note.duplicate")) + Shortcuts.Note("delete", Loc.T("tl.note.delete")) + Loc.T("tl.tip.rightClick"),
                };
                if (w.AnchorCut > 0.0005 && X(w.End) - X(w.Start) > 60
                    && new Rect(X(w.Start) + 10, LaneY(i) + laneH - 24,
                        Palette.Text($"{w.AnchorCut:0.00} s", 10.5, Palette.Accent, Palette.Mono).Width + 22, 16).Contains(lastPointer))
                    tip = Loc.T("tl.tip.beforeZero");
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
        if (laneH >= 62 && p.Y >= RulerH && i >= 0 && i < tracks.Count && PanRect(i).Contains(p))
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
            SetFit(Math.Abs(pps / fit - 1) < 0.02);
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
