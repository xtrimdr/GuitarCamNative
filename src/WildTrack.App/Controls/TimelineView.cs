using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using WildTrack.App.Model;
using WildTrack.App.Video;
using WildTrack.Projects;
using WildTrack.Audio;
using WildTrack.Audio.Dsp;

namespace WildTrack.App.Controls;

/// <summary>
/// The timeline: bar ruler, one lane per track with its clips and waveforms, sticky track headers
/// Port of GuitarCam/src/components/Timeline.tsx gestures, handles, snap and lane controls.
/// Native differences: an empty lane is just empty (the user asked to drop the web's dashed box and its "Apretá R" hint);
/// drag previews are local and commit on release (the window applies them in place, still playing);
/// multiple clips per track swap only with the clip under the pointer. Thumbnails are not ported yet.
/// The selected clip shows its volume line (clip gain + <see cref="Clip.GainCurve"/>, native only): a click on it
/// adds a point, a point drags in time and level, a double click removes it.
/// Track folders (native only): a folder has its own shorter row with a summary of its children's clips, its children
/// are indented inside a thin frame of its color and fold away with it. Rows are laid out in pixels (<see cref="Relayout"/>), so a dragged
/// track can drop into a folder, out of it or between folders; the rows make room where it will land and the folder it
/// goes into lights up.
/// </summary>
public sealed partial class TimelineView : Control
{
    /// <summary>The width the header rows are laid out for; a wider column gives the rest to the volume slider.</summary>
    const double LayoutHeaderW = 264;
    public const double MinHeaderW = 324, MaxHeaderW = 640;
    /// <summary>The marker strip (<see cref="MarkerH"/>) and the ruler's numbers under it.</summary>
    public const double RulerH = MarkerH + 32;
    /// <summary>Minimum px between minor ruler ticks before they get their own small number.</summary>
    const double MinorLabelPx = 30;
    /// <summary>Deepest horizontal zoom: two pixels per sample at 48 kHz, so single samples can be seen (native only).</summary>
    public const double MaxPps = 96000;
    /// <summary>Track height limits; once the header's effect knobs reach their biggest size a taller lane only gives the wave more room.</summary>
    public const double MinTrackH = 40, MaxTrackH = 320;
    /// <summary>The zoom bar under the track headers (MainWindow.axaml) covers the column's bottom: the rows scroll above it.</summary>
    public const double FooterH = 40;
    const double BaseHeaderH = 64; // the header's fixed part (name, volume, pan); taller lanes add the effect knobs under it
    /// <summary>The effect knobs' row: smallest and biggest diameter, the space above them and their label line under them.</summary>
    const double FxKnobMin = 20, FxKnobMax = 64, FxPadTop = 4, FxLabelH = 16, FxPadBottom = 6;
    /// <summary>Room kept between the knobs' row and the header meters at the right edge.</summary>
    const double FxMeterGap = 16;
    /// <summary>Knob drag: px of travel for the whole range (Shift = fine, ×0.2), as the mixer's big knobs.</summary>
    const double FxDragPx = 160;
    /// <summary>Width of the track-header column; dragged from its grip at the right edge (native only).</summary>
    public double HeaderW { get; private set; } = MinHeaderW;
    /// <summary>The column's resize grip: a strip inside the header, so it never shares pixels with a clip at 0 s.</summary>
    const double GripW = 8;
    /// <summary>Where the header's right-aligned controls end, left of the grip.</summary>
    double HeaderRight => HeaderW - GripW;
    bool OnGrip(Point p) => p.Y >= RulerH && p.X >= HeaderRight && p.X < HeaderW;
    public event Action<double>? HeaderWidthChanged;
    double originalHeaderW;
    int fxRow;
    /// <summary>The dragged knob's value and the pointer's last height: the drag is relative, so Shift mid-drag doesn't jump.</summary>
    double fxDragValue, fxDragY;
    /// <summary>The key <see cref="SetKnobs"/> takes for the sends' knobs, and the prefix of a send knob's <see cref="EffectRequested"/> key (then the bus id).</summary>
    public const string SendsKey = "sends", SendPrefix = "send:";
    /// <summary>Whether the headers show the sends' knobs (the user's pick, Configuración → Vista).</summary>
    bool sendsShown = true;
    /// <summary>
    /// The header's knobs (asked by the user: all of them dynamic, no fixed ones): one per send in the track's list, in
    /// the buses' order, with the bus's name (the label) and icon. Set by the window, which keeps the arrays until
    /// something is edited: it is asked while drawing and must not allocate.
    /// </summary>
    public Func<LiveTrack, SendKnob[]>? SendKnobs { get; set; }
    /// <summary>The level and ON of a track's send to a bus (off and 0 without one). Set by the window; it must not allocate.</summary>
    public Func<LiveTrack, string, (bool On, double Level)>? SendState { get; set; }
    /// <summary>How far a track's transpose is being made (0 to 1), null when it plays what it should; set by the window.</summary>
    public Func<LiveTrack, float?>? TransposeProgress { get; set; }
    /// <summary>How far "Detectar tempo" is on a clip (0 to 1), or null when it isn't listening to it.</summary>
    public Func<LiveClip, float?>? TempoProgress { get; set; }
    /// <summary>How far "Extraer MIDI" is on a clip (0..1), null when it isn't taking notes out of it.</summary>
    public Func<LiveClip, float?>? ToMidiProgress { get; set; }
    /// <summary>"-12" to "+12", made once so a header with a transposed track allocates nothing per frame.</summary>
    static readonly string[] TransposeTags = [.. Enumerable.Range(-12, 25).Select(v => $"{v:+0;-0;0}")];
    /// <summary>"0" to "100", the knobs' readouts, made once so drawing allocates nothing.</summary>
    static readonly string[] Hundreds = [.. Enumerable.Range(0, 101).Select(v => $"{v}")];
    /// <summary>A send knob's edit from a header: track, <see cref="SendPrefix"/> + bus, toggle on/off, new level (a level above 0 also turns it on).</summary>
    public event Action<LiveTrack, string, bool, double?>? EffectRequested;

    /// <summary>Shows the sends' knobs in the headers when <paramref name="keys"/> has <see cref="SendsKey"/>; none hides the row.</summary>
    public void SetKnobs(IReadOnlyCollection<string> keys)
    {
        sendsShown = keys.Contains(SendsKey);
        if (gesture == "fx") gesture = null;
        InvalidateVisual();
    }

    public void SetHeaderWidth(double w)
    {
        w = Math.Clamp(Math.Round(w), MinHeaderW, MaxHeaderW);
        if (w == HeaderW) return;
        HeaderW = w;
        ClampScroll();
        waveCache.Clear(); // the waves are cached in window pixels, which shift with the column
        HeaderWidthChanged?.Invoke(w);
        InvalidateVisual();
    }

    /// <summary>Left edge of the effect knobs' row (past the handle column) and how far it stays from the meters.</summary>
    double FxLeft(int i) => 48 + Indent(i);
    double FxCellW(int i) => (HeaderRight - FxMeterGap - FxLeft(i)) / Math.Max(1, KnobCount(i));

    SendKnob[] SendsOf(LiveTrack t) => sendsShown ? SendKnobs?.Invoke(t) ?? [] : [];

    /// <summary>How many knobs track <paramref name="i"/>'s header shows: one per send in its list.</summary>
    int KnobCount(int i) => SendsOf(tracks[i]).Length;

    /// <summary>The key a knob's edits go out with: <see cref="SendPrefix"/> and the bus (only on a gesture: it allocates).</summary>
    string KnobKey(LiveTrack t, int k) => SendsOf(t) is var s && k < s.Length ? SendPrefix + s[k].BusId : "";

    /// <summary>The knobs' diameter: as big as the lane height and the column width allow (asked by the user), 0 when
    /// even <see cref="FxKnobMin"/> doesn't fit and the row is hidden.</summary>
    double FxKnobD(int i)
    {
        if (KnobCount(i) == 0) return 0;
        double d = Math.Floor(Math.Min(FxKnobMax, Math.Min(FxCellW(i) - 10, LaneH(i) - BaseHeaderH - FxPadTop - FxLabelH - FxPadBottom)));
        return d >= FxKnobMin ? d : 0;
    }
    Rect FxKnobRect(int i, int k, double d) => new(FxLeft(i) + FxCellW(i) * (k + 0.5) - d / 2, LaneY(i) + BaseHeaderH + FxPadTop, d, d);
    Rect FxLabelRect(int i, int k, double d) => new(FxLeft(i) + FxCellW(i) * k, LaneY(i) + BaseHeaderH + FxPadTop + d + 2, FxCellW(i), FxLabelH - 2);
    /// <summary>The knob under the point: its place in the track's sends, and whether it is the dial or its label.</summary>
    (int Knob, bool Dial)? FxHit(int i, Point p)
    {
        double d = FxKnobD(i);
        if (d == 0) return null;
        int n = KnobCount(i);
        for (int k = 0; k < n; k++)
        {
            if (FxKnobRect(i, k, d).Inflate(4).Contains(p)) return (k, true);
            if (FxLabelRect(i, k, d).Contains(p)) return (k, false);
        }
        return null;
    }

    /// <summary>A send knob's state: lit while it is on with some level.</summary>
    (bool On, double Amount) FxState(LiveTrack t, int k)
    {
        var sends = SendsOf(t);
        return k < sends.Length && SendState?.Invoke(t, sends[k].BusId) is var (on, level) ? (on && level > 0, level) : (false, 0);
    }

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
    /// The clips caught by the selection box (a drag over the empty part of the lanes; with Ctrl it adds to them), by id
    /// (asked by the user, to move several at once). Moving one of them moves them all by the same time, each on its own
    /// track, and Delete removes them all. Any plain selection clears it.
    /// </summary>
    readonly HashSet<string> boxed = [];
    /// <summary>What was boxed before a Ctrl+box started: the box adds to it.</summary>
    readonly HashSet<string> boxBase = [];
    /// <summary>Where the box started: seconds, and px from the top of the content (it stays put while the view scrolls).</summary>
    double boxSec, boxTop;
    /// <summary>The other boxed clips carried along with the grabbed one: each one as it was, and where it would land.</summary>
    readonly List<(LiveClip Clip, Clip Original)> carriedAlong = [];
    readonly Dictionary<LiveClip, Clip> carriedPreview = [];
    /// <summary>The other clip's edge the dragged one is stuck to (snap to other clips), drawn as a guide; null = none.</summary>
    double? stuckTo;
    /// <summary>
    /// The range selected with Rango or Shift+drag over one or more tracks (native only, <see cref="rangeTracks"/>), to
    /// delete what is inside it. It stays after the mouse is released until another click, Esc, an edit or the delete itself.
    /// </summary>
    double rangeStart, rangeEnd, rangeAnchor;
    KeyModifiers modifiers;
    double originalPan;
    readonly DispatcherTimer dragScroll;
    /// <summary>
    /// Where each row (track or folder, by id) is drawn, in px from the top of the content, while it glides to its place
    /// during and after a reorder or a fold (native only: the web jumps). Absent = drawn at its place.
    /// </summary>
    readonly Dictionary<string, double> shownTop = [];
    readonly DispatcherTimer slide;
    /// <summary>Share of the remaining distance a gliding track covers per 16 ms frame (about 150 ms to settle).</summary>
    const double SlideEase = 0.3;
    /// <summary>The track dragged by its handle, or still gliding into place after the drop: drawn over the others.</summary>
    string? liftedId;
    /// <summary>The other picked tracks carried with it: drawn over the others too.</summary>
    readonly HashSet<string> liftedAlso = [];
    bool fitMode = true;
    readonly Dictionary<LiveClip, (StreamGeometry Geo, WaveKey Key, ClipGainCurve.Point[] Curve, double GainDb)> waveCache = [];
    /// <summary>
    /// What a cached wave was drawn for, each value on its own. It was one sum (pps × 1e6 + … + AtSec × 1.7e-3): at the
    /// deepest zoom the first term is ~1e11, where a double cannot see a small move, so a nudged clip kept its old wave.
    /// </summary>
    readonly record struct WaveKey(double Pps, double ScrollX, double W, double Top, double Bottom, double At, double TrimStart, double TrimEnd);

    public double Bpm { get; set; } = 120;
    public double LengthSec { get; set; }
    public double Playhead { get; private set; }
    public int Selected { get; private set; } = -1;
    /// <summary>The folder selected by a click on its row (then no track is selected), or null.</summary>
    public string? SelectedFolderId { get; private set; }
    /// <summary>The track or folder whose name is being edited in place (its drawn name is left out), or null.</summary>
    public string? EditingNameId { get => editingNameId; set { editingNameId = value; InvalidateVisual(); } }
    string? editingNameId;
    public string? SelectedClipId { get; private set; }
    public string SnapMode { get => snapMode; set { snapMode = value; InvalidateVisual(); } }
    /// <summary>
    /// How the bars-mode grid is drawn (asked by the user): "auto" splits beats finer as you zoom in, "snap" draws the
    /// snap's steps and a subdivision ("1/16"…) keeps that spacing at any zoom. Lines closer than
    /// <see cref="MinGridPx"/> are not drawn; with the snap off, "snap" works as "auto".
    /// </summary>
    public string GridMode { get => gridMode; set { gridMode = value; InvalidateVisual(); } }
    string gridMode = "auto";
    /// <summary>A right click on the ruler's numbers: the window offers the grid's spacing there.</summary>
    public event Action<Point>? RulerMenuRequested;
    /// <summary>Which edges of a moved clip stick to the edges of the others (the selector next to the snap; Alt still moves free).</summary>
    public SnapEdges SnapToClips { get; set; }
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
    /// <summary>Where the take being recorded starts (the playhead when recording started): its red bar grows from there.</summary>
    public double RecordingStartSec { get; set; }
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
    /// <summary>The track's badge was clicked: the window shows the color and icon picker there (native only).</summary>
    public event Action<LiveTrack, Point>? LookRequested;
    public event Action<Clip, string?, string?>? ClipEditCommitted;
    /// <summary>Tracks dropped by a handle (the grabbed one, or all the picked ones): their ids in order, their place in
    /// the folders-and-tracks sequence without them (<see cref="TrackFolders.Move"/>) and the folder they land in (null = the root).</summary>
    public event Action<IReadOnlyList<string>, int, string?>? TrackMoveRequested;
    /// <summary>A folder dropped by its handle: its id and its place in the sequence without it and its children.</summary>
    public event Action<string, int>? FolderMoveRequested;
    public event Action<Folder>? FolderFoldRequested;
    /// <summary>The folder's × was clicked: the window asks there whether its tracks go too.</summary>
    public event Action<Folder, Point>? FolderRemoveRequested;
    public event Action<Folder, Point>? FolderContextRequested;
    /// <summary>The folder's icon was clicked: the window opens its color picker below that point.</summary>
    public event Action<Folder, Point>? FolderLookRequested;
    /// <summary>A double click on a track's or a folder's name: the window edits it in place over that rect.</summary>
    public event Action<string, Rect>? NameEditRequested;
    /// <summary>A track's trash can was clicked: the window asks there before removing it.</summary>
    public event Action<string, Point>? RemoveTrackRequested;
    public event Action<LiveTrack, LiveClip?, Point>? ClipContextRequested;
    /// <summary>A take's clip dropped on a MIDI track: the window takes its notes out there (the clip stays put).</summary>
    public event Action<LiveClip, LiveTrack>? ExtractMidiRequested;
    /// <summary>A locked clip was grabbed to move or trim it (<see cref="ClipEditing.IsLocked"/>): the window says why nothing happens.</summary>
    public event Action<LiveTrack, LiveClip>? LockedTouched;
    /// <summary>The chip on a join between two clips was clicked: the track, the clip coming in and where (native only).</summary>
    public event Action<LiveTrack, LiveClip, Point>? TransitionRequested;
    public event Action<Point>? NewTrackRequested;
    public event Action<LiveTrack, double?, double?>? ChannelRequested;
    public event Action<double>? TrackHeightChanged;

    /// <summary>The selected range, if any: its tracks (a live set: copy it before clearing) and its times. The window
    /// deletes what is inside it on Supr or from the menu.</summary>
    public (IReadOnlyCollection<string> TrackIds, double Start, double End)? Range =>
        rangeTracks.Count > 0 && rangeEnd - rangeStart >= MinRangeSec ? (rangeTracks, rangeStart, rangeEnd) : null;
    /// <summary>A range drag shorter than this is a click, not a range.</summary>
    const double MinRangeSec = 0.02;

    public void ClearRange()
    {
        if (rangeTracks.Count == 0) return;
        rangeTracks.Clear();
        rangeStart = rangeEnd = 0;
        InvalidateVisual();
    }

    public event Action<double>? SeekRequested;
    /// <summary>A drag on the ruler started (true) or ended (false): the window holds playback meanwhile.</summary>
    public event Action<bool>? ScrubChanged;
    bool scrubHeld, scrubSought;
    /// <summary>Pixels the pointer moves on the ruler before a click becomes a drag.</summary>
    const double ScrubSlop = 3;
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
            if (gesture is not ("clip" or "reorder" or "scrub" or "range" or "box")) return;
            double oldX = scrollX, oldY = scrollY;
            if (lastPointer.Y < RulerH + 36) scrollY -= Math.Ceiling((RulerH + 36 - lastPointer.Y) / 4);
            else if (lastPointer.Y > Bounds.Height - 36) scrollY += Math.Ceiling((lastPointer.Y - Bounds.Height + 36) / 4);
            if (gesture is "clip" or "scrub" or "range" or "box")
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

    static readonly Dictionary<StandardCursorType, Cursor> cursors = [];

    /// <summary>A cursor per shape, made once: a new Cursor on every pointer move was a native handle each time.</summary>
    static Cursor CursorOf(StandardCursorType type) =>
        cursors.TryGetValue(type, out var c) ? c : cursors[type] = new Cursor(type);

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

    public void SetTracks(IReadOnlyList<LiveTrack> list, IReadOnlyList<Folder> folderList, bool preserveView = false)
    {
        tracks = list;
        SetFolders(folderList);
        waveCache.Clear();
        shownTop.Clear();
        liftedId = null;
        liftedAlso.Clear();
        picked.Clear();
        boxed.Clear();
        pickAnchor = null;
        Relayout();
        if (!preserveView) SetFit(true);
        Selected = list.Count > 0 ? 0 : -1;
        if (!list.Any(t => t.Clips.Any(c => c.Clip.Id == SelectedClipId))) SelectedClipId = null;
        ClampScroll();
        InvalidateVisual();
    }

    /// <summary>After an edit applied in place: same view and selection, and only the waveforms of clips that left are dropped.</summary>
    public void UpdateTracks(IReadOnlyList<LiveTrack> list, IReadOnlyList<Folder> folderList)
    {
        // rows that move (a reorder dropped, a folder folded) glide from where they are drawn now
        foreach (var r in rows) { string id = RowIdOf(r); shownTop.TryAdd(id, Top(id)); }
        tracks = list;
        SetFolders(folderList);
        picked.RemoveWhere(id => !list.Any(t => t.Track.Id == id));
        boxed.RemoveWhere(id => !list.Any(t => t.Clips.Any(c => c.Clip.Id == id) || t.Track.MidiClips?.Any(c => c.Id == id) == true));
        Relayout();
        if (shownTop.Count > 0) slide.Start();
        var live = list.SelectMany(t => t.Clips).ToHashSet();
        foreach (var gone in waveCache.Keys.Where(c => !live.Contains(c)).ToList()) waveCache.Remove(gone);
        ClampScroll();
        InvalidateVisual();
    }

    /// <summary>
    /// The tracks picked together with Ctrl (one by one) or Shift (a range) on their headers, by id, the selected one
    /// included (asked by the user, to move several into or out of a folder at once): dragging one of them by its
    /// handle carries them all. Empty or one = a plain selection; any plain selection clears it.
    /// </summary>
    readonly HashSet<string> picked = [];
    /// <summary>Where a Shift range starts: the last track clicked without Shift.</summary>
    string? pickAnchor;
    /// <summary>The picked tracks in the order they are drawn, or just the selected one.</summary>
    public IReadOnlyList<string> PickedTrackIds => picked.Count > 1
        ? Seq().OfType<Track>().Where(t => picked.Contains(t.Id)).Select(t => t.Id).ToList()
        : Selected >= 0 && Selected < tracks.Count ? [tracks[Selected].Track.Id] : [];
    bool Picked(int i) => i == Selected || picked.Contains(tracks[i].Track.Id);

    /// <summary>Picks these tracks together again (after the window moved them), keeping the selected one.</summary>
    public void PickTracks(IReadOnlyCollection<string> ids)
    {
        picked.Clear();
        if (ids.Count > 1) foreach (var id in ids) picked.Add(id);
        InvalidateVisual();
    }

    /// <summary>The clips caught by the selection box, by id (empty or one = a plain selection).</summary>
    public IReadOnlyCollection<string> BoxedClipIds => boxed.Count > 1 ? boxed : [];
    /// <summary>The boxed clips were dragged: each take as it lands (the grabbed one first) and each MIDI clip's new start.</summary>
    public event Action<IReadOnlyList<Clip>, IReadOnlyList<(string Id, double AtSec)>>? ClipsMoved;

    /// <summary>Boxes these clips together again (after the window moved them), keeping the selected one.</summary>
    public void BoxClips(IReadOnlyCollection<string> ids)
    {
        boxed.Clear();
        if (ids.Count > 1) boxed.UnionWith(ids);
        InvalidateVisual();
    }

    /// <summary>A press on the empty part of the lanes: a drag from here draws the selection box.</summary>
    void StartBox(PointerPressedEventArgs e)
    {
        boxBase.Clear();
        if ((modifiers & KeyModifiers.Control) != 0)
        {
            boxBase.UnionWith(boxed);
            if (SelectedClipId != null) boxBase.Add(SelectedClipId);
            if (SelectedMidiId != null) boxBase.Add(SelectedMidiId);
        }
        boxSec = Sec(Math.Max(HeaderW, lastPointer.X));
        boxTop = lastPointer.Y - RulerH + scrollY;
        StartGesture("box", e);
    }

    /// <summary>The selection box in view px, from where it started to the pointer.</summary>
    Rect BoxRect()
    {
        double x0 = X(boxSec), y0 = boxTop + RulerH - scrollY, x1 = Math.Max(HeaderW, lastPointer.X), y1 = lastPointer.Y;
        return new Rect(Math.Min(x0, x1), Math.Min(y0, y1), Math.Abs(x1 - x0), Math.Abs(y1 - y0));
    }

    /// <summary>Boxes what was boxed before (with Ctrl) plus every clip the box touches on a visible track.</summary>
    void UpdateBox()
    {
        var b = BoxRect();
        boxed.Clear();
        boxed.UnionWith(boxBase);
        for (int i = 0; i < tracks.Count; i++)
        {
            if (hidden.Contains(tracks[i].Track.Id)) continue;
            foreach (var c in tracks[i].Clips)
                if (ClipRect(i, Window(c)).Intersects(b)) boxed.Add(c.Clip.Id);
            BoxMidi(i, b);
        }
    }

    /// <summary>The box was let go: the first boxed clip (top track, earliest) becomes the selected one, for the inspector.</summary>
    void EndBox()
    {
        foreach (var r in rows)
        {
            if (r.Folder != null) continue;
            if (FirstBoxedMidi(r.Track) is { } firstMidi)
            {
                if (boxed.Count == 1) { SelectMidiClip(r.Track, firstMidi.Id); return; }
                SelectedMidiId = firstMidi.Id;
                SelectOne(r.Track);
                return;
            }
            var first = tracks[r.Track].Clips.Where(c => boxed.Contains(c.Clip.Id)).MinBy(c => Window(c).Start);
            if (first == null) continue;
            if (boxed.Count == 1) { SelectClip(r.Track, first.Clip.Id); return; }
            SelectedClipId = first.Clip.Id;
            SelectOne(r.Track);
            return;
        }
        InvalidateVisual();
    }

    /// <summary>Grabbing a boxed clip by its body carries the other boxed ones with it, takes and MIDI clips alike
    /// (<paramref name="grabbed"/> = null when the one grabbed is a MIDI clip).</summary>
    void StartCarry(LiveClip? grabbed)
    {
        carriedAlong.Clear();
        carriedPreview.Clear();
        foreach (var t in tracks)
            foreach (var c in t.Clips)
                if (c != grabbed && boxed.Contains(c.Clip.Id) && !ClipEditing.IsLocked(t.Track, c.Clip)) carriedAlong.Add((c, ClipEditing.Copy(c.Clip)));
        StartMidiCarry();
    }

    /// <summary>The grabbed clip moved (and snapped): the carried ones move by the same time, none ending before 0.</summary>
    void DragCarried()
    {
        if (dragged == null || original == null || preview == null) return;
        double shift = preview.AtSec - original.AtSec;
        foreach (var (c, o) in carriedAlong) shift = Math.Max(shift, ClipEnvelope.MinClipSec - Window(c, o).End);
        foreach (double from in midiCarried.Values) shift = Math.Max(shift, -from);
        carryShift = shift;
        preview.AtSec = ClipEditing.ToSample(original.AtSec + shift);
        foreach (var (c, o) in carriedAlong)
        {
            var v = ClipEditing.Copy(o);
            v.AtSec = ClipEditing.ToSample(o.AtSec + shift);
            carriedPreview[c] = v;
        }
    }

    /// <summary>A Ctrl or Shift click on a track's header: adds it or takes it out, or picks the range from the anchor.</summary>
    void Pick(int i, bool range)
    {
        string id = tracks[i].Track.Id;
        if (picked.Count == 0 && Selected >= 0 && Selected < tracks.Count) picked.Add(tracks[Selected].Track.Id);
        pickAnchor ??= Selected >= 0 && Selected < tracks.Count ? tracks[Selected].Track.Id : id;
        int primary = i;
        if (range)
        {
            // every track on screen between the anchor and this one, in the order they are drawn
            var order = rows.Where(r => r.Folder == null).Select(r => tracks[r.Track].Track.Id).ToList();
            int a = order.IndexOf(pickAnchor), b = order.IndexOf(id);
            picked.Clear();
            if (a < 0) picked.Add(id);
            else for (int k = Math.Min(a, b); k <= Math.Max(a, b); k++) picked.Add(order[k]);
        }
        else
        {
            pickAnchor = id;
            if (!picked.Add(id))
            {
                picked.Remove(id);
                primary = i != Selected ? Selected : tracks.ToList().FindIndex(t => picked.Contains(t.Track.Id));
            }
        }
        SelectOne(primary);
    }

    public void Select(int i)
    {
        picked.Clear();
        boxed.Clear();
        pickAnchor = i >= 0 && i < tracks.Count ? tracks[i].Track.Id : null;
        SelectOne(i);
    }

    /// <summary>Makes a track the selected one (the inspector's) without touching the picked ones.</summary>
    void SelectOne(int i)
    {
        Selected = i;
        SelectedFolderId = null;
        if (i < 0 || i >= tracks.Count) SelectedClipId = null;
        else if (!tracks[i].Clips.Any(c => c.Clip.Id == SelectedClipId)) SelectedClipId = tracks[i].Clips.FirstOrDefault()?.Clip.Id;
        if (i < 0 || i >= tracks.Count || tracks[i].Track.MidiClips?.Any(c => c.Id == SelectedMidiId) != true) SelectedMidiId = null;
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

    /// <summary>Beats across the view for the zoom presets (native only, asked by the user; no key by default since 1–7
    /// pick the tools): from the closest, a beat, to a bar, 4 bars, 16 bars and the whole song (0). Counted in beats so a
    /// preset shows the same music at any tempo.</summary>
    public static readonly double[] ZoomPresetBeats = [1, 4, 16, 64, 0];

    /// <summary>Zoom preset <paramref name="n"/> (0..4) of <see cref="ZoomPresetBeats"/>, keeping the playhead in place.</summary>
    public void ZoomPreset(int n)
    {
        n = Math.Clamp(n, 0, ZoomPresetBeats.Length - 1);
        if (ZoomPresetBeats[n] == 0) { FitAll(); return; }
        ZoomTo((Bounds.Width - HeaderW - 24) / (ZoomPresetBeats[n] * 60 / Math.Max(1, Bpm)));
    }

    public void FitAll()
    {
        double w = Bounds.Width - HeaderW - 24;
        if (w <= 0 || LengthSec <= 0) return;
        SetFit(true);
        pps = w / Math.Max(8, LengthSec);
        scrollX = 0;
        ZoomChanged?.Invoke();
        InvalidateVisual();
    }

    /// <summary>Horizontal zoom as 0..1 on a log scale from the whole song to <see cref="MaxPps"/>, for the slider under the headers.</summary>
    public double Zoom => Math.Clamp(Math.Log(pps / MinPps) / Math.Log(MaxPps / MinPps), 0, 1);
    /// <summary>The horizontal zoom changed (wheel, slider, "ver todo" or a resize while fitting).</summary>
    public event Action? ZoomChanged;

    public void SetZoom(double z) => ZoomTo(MinPps * Math.Pow(MaxPps / MinPps, Math.Clamp(z, 0, 1)));

    /// <summary>Zooms keeping the playhead where it is on screen, or the view's middle when it is out of sight.</summary>
    void ZoomTo(double next)
    {
        next = Math.Clamp(next, MinPps, MaxPps);
        if (Math.Abs(next - pps) < pps * 1e-6) return;
        double px = Playhead * pps - scrollX;
        double at = Playhead;
        if (px < 0 || px > Bounds.Width - HeaderW)
        {
            px = (Bounds.Width - HeaderW) / 2;
            at = (scrollX + px) / pps;
        }
        ZoomAround(next, at, px);
    }

    double X(double sec) => HeaderW + sec * pps - scrollX;

    double Sec(double x) => (x - HeaderW + scrollX) / pps;
    /// <summary>Where track <paramref name="i"/>'s row is drawn; past the last track, the extra rows under them
    /// (a take going to a new track, "Nueva pista"). A track folded away is far above the view, so nothing draws it.</summary>
    double LaneY(int i) => i >= tracks.Count ? RulerH + contentH + (i - tracks.Count) * laneH - scrollY
        : hidden.Contains(tracks[i].Track.Id) && !IsDragged(tracks[i].Track.Id) ? -1e9 : RulerH + Top(tracks[i].Track.Id) - scrollY;
    double FolderY(Folder f) => RulerH + Top(f.Id) - scrollY;
    /// <summary>A track's lane height: the one being dragged, or the shared one times its <see cref="Track.HeightScale"/>.</summary>
    double RowH(Track t) => heightDrag is { } d && d.Id == t.Id ? d.H
        : t.HeightScale is { } k ? Math.Clamp(Math.Round(laneH * k / 2) * 2, Track.MinHeight, Track.MaxHeight) : laneH;
    /// <summary>Lane height of track <paramref name="i"/>; the extra rows under the tracks use the shared one.</summary>
    double LaneH(int i) => i >= 0 && i < tracks.Count ? RowH(tracks[i].Track) : laneH;
    /// <summary>A folder's row height. Open: its own (<see cref="Folder.Height"/>) or <see cref="FolderH"/>. Folded: the
    /// shared lane height times its <see cref="Folder.HeightScale"/>, so its mini map follows the vertical zoom.</summary>
    double FolderRowH(Folder f) => f.Separator ? (f.HiddenInTimeline ? 0 : SeparatorH)
        : heightDrag is { } d && d.Id == f.Id ? d.H
        : f.Collapsed ? Math.Clamp(Math.Round(laneH * (f.HeightScale ?? 1) / 2) * 2, Folder.MinHeight, Track.MaxHeight)
        : f.Height ?? FolderH;
    /// <summary>The row whose bottom edge is being dragged and its height so far (applied on release).</summary>
    (string Id, double H)? heightDrag;
    /// <summary>Px above and below a row's bottom edge (in the header) that grab it to change that row's height.</summary>
    const double RowEdgeGrab = 3;
    double heightFrom;
    /// <summary>A row's own height changed by dragging its bottom edge (null = back to the shared one, a double click on the edge).</summary>
    public event Action<string, double?>? RowHeightRequested;

    /// <summary>The track or folder whose bottom edge is under the pointer, in the header column; null elsewhere.</summary>
    string? RowEdgeAt(Point p)
    {
        // the pinned "Nueva pista" and the zoom bar cover the rows' edges there
        if (p.Y < RulerH || p.X >= HeaderRight || p.Y >= AddRect.Y) return null;
        foreach (var r in rows)
        {
            // a separator keeps its height
            if (r.Folder is { Separator: true }) continue;
            double bottom = RulerH + r.Top - scrollY + r.H;
            if (Math.Abs(p.Y - bottom) <= RowEdgeGrab) return RowIdOf(r);
        }
        return null;
    }

    // --- rows and folders (native only) ----------------------------------------------------------

    /// <summary>Height of a folder's own row when open: like a track's, so its fader shows under the name.</summary>
    public const double FolderH = 64;
    /// <summary>Height of a separator's row (a hidden one takes none: it keeps its place in the order, out of sight).</summary>
    public const double SeparatorH = 22;
    IReadOnlyList<Folder> folders = [];
    readonly Dictionary<string, Folder> folderById = [];
    /// <summary>A row on screen: a track (its index) or a folder, with its place in px from the top of the content.</summary>
    readonly record struct Row(int Track, Folder? Folder, double Top, double H);
    /// <summary>The rows in order as they belong now (during a reorder: with room made where the dragged one lands).</summary>
    readonly List<Row> rows = [];
    readonly Dictionary<string, double> targetTop = [];
    /// <summary>Tracks folded away in their folder.</summary>
    readonly HashSet<string> hidden = [];
    double contentH;

    string RowIdOf(Row r) => r.Folder?.Id ?? tracks[r.Track].Track.Id;

    void SetFolders(IReadOnlyList<Folder> list)
    {
        folders = list;
        folderById.Clear();
        foreach (var f in list) folderById[f.Id] = f;
        if (SelectedFolderId != null && !folderById.ContainsKey(SelectedFolderId)) SelectedFolderId = null;
    }

    public Folder? FolderOf(LiveTrack t) => t.Track.FolderId is { } id ? folderById.GetValueOrDefault(id) : null;
    public static Color FolderColor(Folder f) => f.Color != null && Color.TryParse(f.Color, out var c) ? c : Palette.AccentColor;

    /// <summary>Folders and tracks in order, as <see cref="TrackFolders.Sequence"/> keeps them.</summary>
    List<object> Seq() => TrackFolders.Sequence(tracks.Select(t => t.Track).ToList(), folders);

    /// <summary>Stacks a sequence: each visible item's top in <paramref name="tops"/>, folded tracks in <paramref name="folded"/>; returns the height.</summary>
    double Stack(List<object> seq, Dictionary<string, double> tops, HashSet<string> folded, bool landing)
    {
        double y = 0;
        foreach (var x in seq)
        {
            if (x is Folder f) { tops[f.Id] = y; y += FolderRowH(f); continue; }
            var t = (Track)x;
            string? fid = landing && dragIds.Contains(t.Id) && drop is { } d ? d.Folder : t.FolderId;
            // a folder being dragged travels folded
            if (fid != null && folderById.TryGetValue(fid, out var parent) && (parent.Collapsed || parent == dragFolder && moved))
            {
                folded.Add(t.Id);
                continue;
            }
            tops[t.Id] = y;
            y += RowH(t);
        }
        return y;
    }

    /// <summary>Lays the rows out again: as they are, or, while a reorder has a place to drop, as they will be.</summary>
    void Relayout()
    {
        targetTop.Clear();
        hidden.Clear();
        rows.Clear();
        var seq = Seq();
        bool landing = gesture == "reorder" && moved && drop != null;
        if (landing)
        {
            var block = Dragged(seq);
            seq.RemoveAll(block.Contains);
            seq.InsertRange(Math.Clamp(drop!.Value.Pos, 0, seq.Count), block);
        }
        contentH = Stack(seq, targetTop, hidden, landing);
        var index = new Dictionary<string, int>();
        for (int i = 0; i < tracks.Count; i++) index[tracks[i].Track.Id] = i;
        foreach (var x in seq)
        {
            if (x is Folder f) rows.Add(new Row(-1, f, targetTop[f.Id], FolderRowH(f)));
            else if (x is Track t && targetTop.TryGetValue(t.Id, out double top) && index.TryGetValue(t.Id, out int i)) rows.Add(new Row(i, null, top, RowH(t)));
        }
        ClampScroll();
    }

    /// <summary>The dragged item: the carried tracks, or a folder with its children.</summary>
    List<object> Dragged(List<object> seq) => dragFolder is { } df
        ? seq.Where(x => x == df || x is Track t && t.FolderId == df.Id).ToList()
        : seq.Where(x => x is Track t && dragIds.Contains(t.Id)).ToList();

    bool IsDragged(string id) => gesture == "reorder" && moved && (dragFolder != null ? id == dragFolder.Id : dragIds.Contains(id));

    /// <summary>Where the dragged block's top is, following the pointer.</summary>
    double BlockTop() => Math.Max(0, dragTop + lastPointer.Y - down.Y + scrollY - downScrollY);

    /// <summary>Where a dragged row goes in the block: the carried tracks stack one under the other.</summary>
    double BlockOffset(string id) => dragOffset.GetValueOrDefault(id);

    /// <summary>Where a row is drawn now, in px from the top of the content: the dragged one follows the pointer, and
    /// the other carried tracks glide to their place under it.</summary>
    double Top(string id)
    {
        if (IsDragged(id) && (id == liftedId || !shownTop.ContainsKey(id))) return BlockTop() + BlockOffset(id);
        return shownTop.TryGetValue(id, out double s) ? s : targetTop.GetValueOrDefault(id);
    }

    /// <summary>The row under a y in the view, or null (the ruler, the space under the rows).</summary>
    Row? HitRow(double y)
    {
        if (y < RulerH) return null;
        double cy = y - RulerH + scrollY;
        foreach (var r in rows)
            if (cy >= r.Top && cy < r.Top + r.H) return r;
        return null;
    }

    int HitTrack(double y) => HitRow(y) is { Folder: null } r ? r.Track : -1;

    // the reorder gesture: a track or a folder, where it was and where it will land
    string? dragTrackId;
    /// <summary>The tracks carried (the grabbed one, or every picked one when it is picked), in the order they are drawn.</summary>
    readonly List<string> dragIds = [];
    readonly Dictionary<string, double> dragOffset = [];
    /// <summary>The carried tracks were apart or in different folders: dropping them even where they were gathers them.</summary>
    bool dragGathers;
    Folder? dragFolder;
    double dragTop;
    /// <summary>Where the dragged item lands: its place in the sequence without it, and the folder (tracks only).</summary>
    (int Pos, string? Folder)? drop;
    (int Pos, string? Folder) dropFrom;

    /// <summary>Px the dragged row has to pass the line between its current place and another before it takes that
    /// one: without it, a row resting on an edge (the last track of a folder, say) flickered in and out.</summary>
    const double DropStick = 8;
    /// <summary>Px that separate the two places at a folder's bottom edge (its last one inside, the first one after it),
    /// so the dragged row's top passing that edge is what takes it in or out.</summary>
    const double EdgeSplit = 1;

    /// <summary>
    /// Where the dragged row lands: the place whose gap is nearest to the row's top, measured on the rows without it,
    /// where a gap's y is exactly where the row's top would sit once dropped (so making room never moves the target,
    /// and a row resting in its gap stays there). A track can land between any two rows; at a folder's bottom edge
    /// there are two places, the last inside (just above the edge) and the first after it (just below), and over a
    /// folded folder's row it goes in last. A folder lands only between items at the root.
    /// </summary>
    (int Pos, string? Folder) FindDrop()
    {
        var seq = Seq();
        seq.RemoveAll(Dragged(seq).Contains);
        var tops = new Dictionary<string, double>();
        double end = Stack(seq, tops, [], false);
        double at = BlockTop();
        bool tracksOnly = dragFolder == null;
        var slots = new List<(double Y, int Pos, string? Folder)>();
        for (int k = 0; k < seq.Count; k++)
        {
            if (seq[k] is Track { FolderId: { } fid } child)
            {
                // after a track in a folder, still in it (a folded one's tracks have no row)
                if (tracksOnly && tops.TryGetValue(child.Id, out double ct)) slots.Add((ct + RowH(child) - EdgeSplit, k + 1, fid));
                continue;
            }
            // a row at the root: before it, out of any folder above
            string id = seq[k] is Folder rf ? rf.Id : ((Track)seq[k]).Id;
            double top = tops[id];
            slots.Add((top + EdgeSplit, k, null));
            if (tracksOnly && seq[k] is Folder { Separator: false } f)
            {
                if (f.Collapsed)
                {
                    int children = seq.Count(x => x is Track t && t.FolderId == f.Id);
                    slots.Add((top + FolderRowH(f) / 2, k + 1 + children, f.Id));
                }
                else slots.Add((top + FolderRowH(f) - EdgeSplit, k + 1, f.Id));
            }
        }
        slots.Add((end + EdgeSplit, seq.Count, null));
        // measured DropStick px back towards the current place, so leaving it takes passing the line by that much
        var now = drop ?? dropFrom;
        foreach (var s in slots)
            if (s.Pos == now.Pos && s.Folder == now.Folder)
            {
                at = Math.Abs(at - s.Y) <= DropStick ? s.Y : at - Math.Sign(at - s.Y) * DropStick;
                break;
            }
        (int Pos, string? Folder) best = (seq.Count, null);
        double bestD = double.MaxValue;
        foreach (var s in slots)
        {
            double d = Math.Abs(at - s.Y);
            if (d < bestD) { bestD = d; best = (s.Pos, s.Folder); }
        }
        return best;
    }

    void Slide()
    {
        string? dragId = gesture == "reorder" ? dragFolder?.Id ?? dragTrackId : null;
        foreach (var id in shownTop.Keys.ToList())
        {
            // a carried track glides to its place in the block, the others to their place in the rows
            double target = 0;
            bool carried = id != dragId && IsDragged(id);
            if (carried) target = BlockTop() + BlockOffset(id);
            if (id == dragId || !carried && !targetTop.TryGetValue(id, out target)) { shownTop.Remove(id); continue; }
            double s = shownTop[id];
            s += (target - s) * SlideEase;
            if (Math.Abs(target - s) < 0.5) shownTop.Remove(id);
            else shownTop[id] = s;
        }
        if (shownTop.Count == 0)
        {
            slide.Stop();
            if (gesture != "reorder")
            {
                liftedId = null;
                liftedAlso.Clear();
            }
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
            if (!liftedAlso.Contains(tracks[i].Track.Id)) yield return i;
        }
        if (liftedAlso.Count > 0)
            for (int i = 0; i < tracks.Count; i++)
                if (liftedAlso.Contains(tracks[i].Track.Id)) yield return i;
        if (lifted >= 0) yield return lifted;
    }

    /// <summary>The folders in drawing order: the lifted one last.</summary>
    IEnumerable<Folder> FolderOrder() => folders.Where(f => f.Id != liftedId).Concat(folders.Where(f => f.Id == liftedId));
    double Grid => ClipEditing.GridSeconds(snapMode, Bpm);
    double ViewSec => Math.Max(8, Math.Max(LengthSec, Playhead + 2));
    double MinPps => Math.Max(0.01, (Bounds.Width - HeaderW) / Math.Max(600, ViewSec * 8));
    void ClampScroll() => scrollY = Math.Clamp(scrollY, 0, Math.Max(0, contentH + ExtraRows * laneH + AddH + FooterH - (Bounds.Height - RulerH)));
    public void SetTrackHeight(double height)
    {
        double next = Math.Clamp(Math.Round(height / 2) * 2, MinTrackH, MaxTrackH); // the web's slider steps by 2 px
        if (Math.Abs(next - laneH) < 0.001) return;
        laneH = next;
        Relayout();
        waveCache.Clear();
        TrackHeightChanged?.Invoke(laneH);
        InvalidateVisual();
    }
    Clip DisplayClip(LiveClip clip) => dragged == clip && preview != null && overIndex == fromIndex ? preview
        : carriedPreview.TryGetValue(clip, out var along) ? along : clip.Clip;
    ClipEnvelope.ClipWindow Window(LiveClip clip, Clip? value = null)
    {
        var c = value ?? DisplayClip(clip);
        return ClipEditing.Window(c, clip.Media, clip.LengthSec);
    }
    LiveClip? ClipAt(int i, double x) => i >= 0 && i < tracks.Count
        ? tracks[i].Clips.LastOrDefault(c => { var w = Window(c); return x >= X(w.Start) && x <= X(w.End); }) : null;

    // --- clip volume curve (native only) -----------------------------------------------------------

    /// <summary>The point being dragged in a "curve" gesture.</summary>
    int curvePoint = -1;
    /// <summary>Pixels around a curve point or the line that still grab it.</summary>
    const double CurveGrab = 5;

    Rect ClipRect(int lane, ClipEnvelope.ClipWindow w) => new(X(w.Start), LaneY(lane) + 5, Math.Max(2, X(w.End) - X(w.Start)), LaneH(lane) - 10);
    static double CurveY(Rect r, double db) => r.Top + 3 + (ClipGainCurve.MaxDb - Math.Clamp(db, ClipGainCurve.MinDb, ClipGainCurve.MaxDb))
        / (ClipGainCurve.MaxDb - ClipGainCurve.MinDb) * (r.Height - 6);
    static double CurveDb(Rect r, double y) => Math.Clamp(ClipGainCurve.MaxDb - (y - r.Top - 3) / Math.Max(1, r.Height - 6)
        * (ClipGainCurve.MaxDb - ClipGainCurve.MinDb), ClipGainCurve.MinDb, ClipGainCurve.MaxDb);
    /// <summary>The live clip's curve, or the preview's while it is dragged.</summary>
    static ClipGainCurve.Point[] CurveOf(LiveClip c, Clip model) => ReferenceEquals(model, c.Clip) ? c.Curve : model.CurvePoints();
    /// <summary>What the line shows at a timeline time: the clip gain plus the curve.</summary>
    static double LevelAt(Clip model, ClipGainCurve.Point[] pts, ClipEnvelope.ClipWindow w, double sec) => model.GainDb + ClipGainCurve.DbAt(pts, sec - w.Origin);
    bool CurveEditable(int lane, LiveClip c) => lane == Selected && c.Clip.Id == SelectedClipId && !c.Offline && !Recording;

    void DrawCurve(DrawingContext ctx, LiveClip c, Clip model, ClipEnvelope.ClipWindow w, Rect r, bool editable)
    {
        var pts = CurveOf(c, model);
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(r.Left, CurveY(r, LevelAt(model, pts, w, w.Start))), false);
            foreach (var pt in pts)
                if (w.Origin + pt.Sec > w.Start && w.Origin + pt.Sec < w.End)
                    g.LineTo(new Point(X(w.Origin + pt.Sec), CurveY(r, model.GainDb + pt.Db)));
            g.LineTo(new Point(r.Right, CurveY(r, LevelAt(model, pts, w, w.End))));
            g.EndFigure(false);
        }
        // over an accent wave the selected line goes in the text color, its points in the accent
        ctx.DrawGeometry(null, editable ? Palette.Pen(Palette.Fg, 1.5) : Palette.Pen(Palette.Alpha(Palette.DimColor, 0.7), 1), geo);
        if (!editable) return;
        foreach (var pt in pts)
        {
            double x = X(w.Origin + pt.Sec);
            if (x < r.Left - 4 || x > r.Right + 4) continue;
            ctx.DrawRectangle(Palette.Accent, Palette.Pen(Palette.App), new Rect(x - 4, CurveY(r, model.GainDb + pt.Db) - 4, 8, 8), 2, 2);
        }
    }

    /// <summary>The curve point under the pointer on an editable clip, or -1.</summary>
    int CurvePointAt(int lane, LiveClip c, Point p)
    {
        if (!CurveEditable(lane, c)) return -1;
        var w = Window(c);
        var r = ClipRect(lane, w);
        var pts = c.Curve;
        for (int k = pts.Length - 1; k >= 0; k--)
            if (Math.Abs(p.X - X(w.Origin + pts[k].Sec)) <= CurveGrab && Math.Abs(p.Y - CurveY(r, c.Clip.GainDb + pts[k].Db)) <= CurveGrab) return k;
        return -1;
    }

    /// <summary>True when the pointer is on the volume line of an editable clip (away from its trim edges).</summary>
    bool OnCurve(int lane, LiveClip c, Point p)
    {
        if (!CurveEditable(lane, c)) return false;
        var w = Window(c);
        var r = ClipRect(lane, w);
        return p.X > r.Left + 8 && p.X < r.Right - 8 && Math.Abs(p.Y - CurveY(r, LevelAt(c.Clip, c.Curve, w, Sec(p.X)))) <= CurveGrab;
    }

    /// <summary>Starts dragging a point of the selected clip's curve; a new one is added where the line was clicked.</summary>
    void StartCurve(int lane, LiveClip c, int point, PointerPressedEventArgs e)
    {
        dragged = c;
        original = ClipEditing.Copy(c.Clip);
        preview = ClipEditing.Copy(c.Clip);
        bool added = point < 0;
        if (added)
        {
            double sec = Sec(lastPointer.X) - Window(c).Origin;
            var point0 = new GainPoint { Sec = Math.Round(sec, 3), Db = Math.Round(ClipGainCurve.DbAt(c.Curve, sec), 1) };
            preview.GainCurve = [.. preview.GainCurve.Append(point0).OrderBy(g => g.Sec)];
            point = preview.GainCurve.IndexOf(point0);
        }
        curvePoint = point;
        StartGesture("curve", e);
        fromIndex = overIndex = lane;
        moved = added; // a new point is an edit even if it does not move
    }

    void DragCurve()
    {
        if (dragged == null || original == null || preview == null || curvePoint < 0 || curvePoint >= preview.GainCurve.Count) return;
        var w = Window(dragged, original);
        var r = ClipRect(fromIndex, w);
        var list = preview.GainCurve.Select(g => new GainPoint { Sec = g.Sec, Db = g.Db, Extra = g.Extra }).ToList();
        double lo = (curvePoint > 0 ? list[curvePoint - 1].Sec : w.Start - w.Origin) + 0.001;
        double hi = (curvePoint < list.Count - 1 ? list[curvePoint + 1].Sec : w.End - w.Origin) - 0.001;
        double sec = Sec(lastPointer.X) - w.Origin;
        list[curvePoint].Sec = Math.Round(hi > lo ? Math.Clamp(sec, lo, hi) : lo, 3);
        // the point holds the curve's part: the line shows it on top of the clip gain
        list[curvePoint].Db = Math.Round(Math.Clamp(CurveDb(r, lastPointer.Y) - preview.GainDb, ClipGainCurve.MinDb, ClipGainCurve.MaxDb), 1);
        preview.GainCurve = list;
    }

    /// <summary>Double click on a point: the curve without it, committed like any clip edit.</summary>
    void RemoveCurvePoint(LiveClip c, int point)
    {
        var value = ClipEditing.Copy(c.Clip);
        value.GainCurve.RemoveAt(point);
        ClipEditCommitted?.Invoke(value, null, null);
    }

    public override void Render(DrawingContext ctx)
    {
        // only with room past the 24 px margin: a timeline barely wider than its headers (mid-layout, at startup) would
        // give a zero or negative zoom, and the ruler's loops would never reach the right edge
        if (fitMode && Bounds.Width - HeaderW - 24 > 0)
        {
            double fit = (Bounds.Width - HeaderW - 24) / Math.Max(8, LengthSec);
            // the slider follows after this frame: setting it from inside Render would re-enter layout
            if (fit != pps) Dispatcher.UIThread.Post(() => ZoomChanged?.Invoke());
            pps = fit;
        }
        double W = Bounds.Width, H = Bounds.Height;
        ctx.FillRectangle(Palette.App, new Rect(0, 0, W, H));

        BuildGrid(W);
        using (ctx.PushClip(new Rect(HeaderW, RulerH, W - HeaderW, Math.Max(0, H - RulerH))))
        {
            foreach (var f in folders) if (f.Id != liftedId) DrawFolderLane(ctx, f, W);
            DrawDropHole(ctx, W);
            foreach (int i in DrawOrder()) DrawLane(ctx, i, W);
            if (liftedId != null && folderById.TryGetValue(liftedId, out var liftedFolder)) DrawFolderLane(ctx, liftedFolder, W);
            if (recordingLanes != null)
                foreach (int r in recordingLanes) DrawRecordingBar(ctx, r, W);
            DrawMidiCarried(ctx, W);
            DrawStripGuides(ctx, W, H);
            DrawSocialMarks(ctx, W);
            // the snap guide: the edge the dragged clip is stuck to, across every lane (asked by the user)
            if (gesture == "clip" && stuckTo is { } guide && X(guide) >= HeaderW)
            {
                double gx = Math.Round(X(guide)) + 0.5;
                ctx.DrawLine(Palette.Pen(Palette.Accent), new Point(gx, RulerH), new Point(gx, H));
            }
            if (gesture == "box" && moved)
                ctx.DrawRectangle(Palette.Alpha(Palette.AccentColor, 0.08), Palette.Pen(Palette.Alpha(Palette.AccentColor, 0.7)), BoxRect());
            DrawZoomBand(ctx, H);
        }
        DrawRuler(ctx, W);
        DrawStrip(ctx, W);

        // sticky headers
        ctx.FillRectangle(Palette.Panel, new Rect(0, 0, HeaderW, H));
        ctx.FillRectangle(Palette.Panel, new Rect(0, 0, HeaderW, RulerH));
        ctx.DrawLine(Palette.Pen(Palette.Line), new Point(0, RulerH - 0.5), new Point(W, RulerH - 0.5));
        using (ctx.PushClip(new Rect(0, RulerH, HeaderW, Math.Max(0, H - RulerH))))
        {
            foreach (var f in folders) if (f.Id != liftedId) DrawFolderHeader(ctx, f);
            foreach (int i in DrawOrder()) DrawHeader(ctx, i);
            if (liftedId != null && folderById.TryGetValue(liftedId, out var liftedFolder)) DrawFolderHeader(ctx, liftedFolder);
        }
        using (ctx.PushClip(new Rect(0, RulerH, W, Math.Max(0, H - RulerH))))
        {
            DrawFolderFrames(ctx, W);
            DrawDropTarget(ctx, W);
        }
        if (ExtraRows > 0) DrawRecordingHeader(ctx, LaneY(tracks.Count), W);
        DrawAddButton(ctx);
        if (tracks.Count == 0 && folders.Count == 0 && !Recording)
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
            var r = new Rect(HeaderW + 1, LaneY(overIndex) + 1, Math.Max(1, W - HeaderW - 2), LaneH(overIndex) - 2);
            ctx.DrawRectangle(Palette.Alpha(Palette.AccentColor, 0.1), Palette.Pen(Palette.Accent, 2), r);
            string text = swapId == null ? Loc.T("timeline.moveTo", tracks[overIndex].Track.Name) : Loc.T("timeline.swapWith", tracks[overIndex].Track.Name);
            ctx.DrawText(text, 11, Palette.Accent, new Point(Math.Max(HeaderW + 10, W - 280), r.Y + 8), Palette.UiBold);
        }
        DrawRowEdge(ctx);
        DrawColumnGrip(ctx, H);
        ctx.DrawLine(Palette.Pen(Palette.Line), new Point(HeaderW - 0.5, 0), new Point(HeaderW - 0.5, H));
    }

    /// <summary>The bottom edge of the row whose height is being changed (or that the pointer would grab), lit across the header.</summary>
    void DrawRowEdge(DrawingContext ctx)
    {
        string? id = gesture == "row-height" ? heightDrag?.Id : gesture == null ? RowEdgeAt(lastPointer) : null;
        if (id == null) return;
        foreach (var r in rows)
        {
            if (RowIdOf(r) != id) continue;
            double y = RulerH + r.Top - scrollY + r.H;
            ctx.FillRectangle(Palette.Accent, new Rect(0, y - 1, HeaderRight, 2));
            return;
        }
    }

    /// <summary>
    /// The column's resize grip: one strip for every track, the whole height under the ruler, with two short lines
    /// in the middle of the view. It lights up while the pointer is on it or dragging it.
    /// </summary>
    /// <summary>Height of the "Nueva pista" button pinned at the foot of the header column.</summary>
    const double AddH = 36;
    /// <summary>
    /// "Nueva pista", pinned at the foot of the header column right above the zoom bar (asked by the user: it used to
    /// follow the last track and got lost with many rows); the rows scroll under it.
    /// </summary>
    Rect AddRect => new(0, Bounds.Height - FooterH - AddH, HeaderRight, AddH);

    void DrawAddButton(DrawingContext ctx)
    {
        var r = AddRect;
        if (r.Y < RulerH) return;
        bool hover = gesture == null && r.Contains(lastPointer);
        ctx.FillRectangle(hover ? Palette.Hover : Palette.Panel, r);
        ctx.DrawLine(Palette.Pen(Palette.Line), new Point(0, r.Y + 0.5), new Point(r.Right, r.Y + 0.5));
        var fg = hover ? Palette.Fg : Palette.Dim;
        ctx.DrawText("", 12, fg, new Point(12, r.Y + 11), new Typeface("Segoe Fluent Icons, Segoe MDL2 Assets"));
        ctx.DrawText(Loc.T("timeline.newTrack"), 12, fg, new Point(32, r.Y + 10));
    }

    void DrawColumnGrip(DrawingContext ctx, double H)
    {
        bool lit = gesture == "header" || gesture == null && OnGrip(lastPointer);
        var strip = new Rect(HeaderRight, RulerH, GripW, Math.Max(0, H - RulerH));
        ctx.FillRectangle(lit ? Palette.Hover : Palette.Panel, strip);
        var grip = lit ? Palette.Accent : Palette.Faint;
        double gx = HeaderW - GripW / 2, gy = Math.Round(strip.Center.Y);
        ctx.FillRectangle(grip, new Rect(gx - 2, gy - 10, 1, 20));
        ctx.FillRectangle(grip, new Rect(gx + 1, gy - 10, 1, 20));
    }

    /// <summary>
    /// The "ghost" of a clip carried to another track (native only): a dashed outline where it will land, on the
    /// lane under the pointer and at the snapped time, with the track and the time it will start at. Drawn over the
    /// lanes and under the sticky headers, so it never covers the header column.
    /// </summary>
    void DrawGhost(DrawingContext ctx, double W, double H)
    {
        // over a MIDI track it shows where the notes will land: under the take, where it is now
        bool toMidi = tracks[overIndex].Track.IsMidi;
        var w = Window(dragged!, toMidi ? original : preview);
        double y = LaneY(overIndex);
        ctx.FillRectangle(Palette.Alpha(Palette.AccentColor, 0.06), new Rect(HeaderW, y, Math.Max(0, W - HeaderW), LaneH(overIndex)));
        double x0 = X(w.Start), x1 = X(w.End);
        var box = new Rect(x0, y + 5, Math.Max(2, x1 - x0), LaneH(overIndex) - 10);
        using (ctx.PushClip(new Rect(HeaderW, RulerH, Math.Max(0, W - HeaderW), Math.Max(0, H - RulerH))))
        {
            var dashed = new Pen(Palette.Accent, 1.5, new DashStyle([4, 3], 0));
            ctx.DrawRectangle(Palette.Alpha(Palette.AccentColor, 0.18), dashed, box, 5, 5);
            var label = Palette.Text(toMidi ? Loc.T("toMidi.drop", tracks[overIndex].Track.Name) : $"{tracks[overIndex].Track.Name} · {w.Start:0.00} s",
                11, Palette.Accent, Palette.UiBold);
            double lx = Math.Clamp(box.X + 8, HeaderW + 8, Math.Max(HeaderW + 8, W - label.Width - 8));
            ctx.DrawRectangle(Palette.Alpha(Palette.AppColor, 0.75), null, new Rect(lx - 4, box.Y + 3, label.Width + 8, label.Height + 2), 3, 3);
            ctx.DrawText(label, new Point(lx, box.Y + 4));
        }
    }

    /// <summary>The selected range over its lane: a band of the accent with the length on top.</summary>
    void DrawRange(DrawingContext ctx, double y, double lh, double W, double start, double end)
    {
        double x0 = Math.Max(HeaderW, X(start)), x1 = Math.Min(W, X(end));
        if (x1 <= x0) return;
        var band = new Rect(x0, y + 1, x1 - x0, lh - 2);
        ctx.FillRectangle(Palette.Alpha(Palette.AccentColor, 0.22), band);
        var pen = Palette.Pen(Palette.Accent, 1.5);
        if (X(start) >= HeaderW) ctx.DrawLine(pen, new Point(X(start), y + 1), new Point(X(start), y + lh - 1));
        if (X(end) <= W) ctx.DrawLine(pen, new Point(X(end), y + 1), new Point(X(end), y + lh - 1));
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
        double x0 = X(RecordingStartSec), x1 = X(Playhead);
        if (x1 > x0) ctx.DrawRectangle(Palette.Alpha(Palette.RecColor, 0.2), Palette.Pen(Palette.Alpha(Palette.RecColor, 0.5)), new Rect(x0, y + 6, x1 - x0, LaneH(lane) - 12), 4, 4);
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

    static readonly Typeface Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    /// <summary>Px of a folded folder's mini map strip below which its clips show as plain bars, without their wave.</summary>
    const double MiniWaveAt = 6;

    /// <summary>
    /// A folder's lane, tinted with its color. Open, its children show below it and the lane stays empty (asked by the
    /// user: a block per clip there only repeated them). Folded, it is a mini map: one strip per child track, in order,
    /// with its clips in the track's color and their wave once the strip is tall enough; the row follows the vertical zoom.
    /// </summary>
    void DrawFolderLane(DrawingContext ctx, Folder f, double W)
    {
        double y = FolderY(f), fh = FolderRowH(f);
        if (fh <= 0 || y + fh < RulerH || y > Bounds.Height) return;
        if (f.Separator)
        {
            // a separator: a line across the lanes, at the middle of its row
            if (f.Id == liftedId) ctx.FillRectangle(Palette.App, new Rect(HeaderW, y, Math.Max(0, W - HeaderW), fh));
            ctx.DrawLine(Palette.Pen(Palette.Alpha(FolderColor(f), 0.45)), new Point(HeaderW, Math.Round(y + fh / 2) + 0.5), new Point(W, Math.Round(y + fh / 2) + 0.5));
            return;
        }
        var c = FolderColor(f);
        var row = new Rect(HeaderW, y, Math.Max(0, W - HeaderW), fh);
        if (f.Id == liftedId) ctx.FillRectangle(Palette.App, row);
        ctx.FillRectangle(Palette.Alpha(c, 0.07), row);
        // a lifted folder travels folded, so it shows its mini map while dragged too
        if (f.Collapsed || f.Id == liftedId) DrawMiniMap(ctx, f, y, fh, W);
        ctx.DrawLine(Palette.Pen(Palette.Line), new Point(HeaderW, y + fh - 0.5), new Point(W, y + fh - 0.5));
    }

    /// <summary>The folded folder's mini map (<see cref="DrawFolderLane"/>): a strip per child track over the row's height.</summary>
    void DrawMiniMap(DrawingContext ctx, Folder f, double y, double fh, double W)
    {
        int n = 0;
        bool anySolo = false;
        foreach (var t in tracks)
        {
            if (t.Track.FolderId == f.Id) n++;
            if (t.Soloed) anySolo = true;
        }
        if (n == 0) return;
        double top = y + 4, strip = (fh - 8) / n;
        int k = 0;
        foreach (var t in tracks)
        {
            if (t.Track.FolderId != f.Id) continue;
            double sy = top + k++ * strip;
            bool silent = t.Silenced || anySolo && !t.Soloed;
            var tint = TrackIcons.ColorOf(t.Track) ?? Palette.AccentColor;
            foreach (var clip in t.Clips)
            {
                var w = Window(clip);
                double x0 = X(w.Start), x1 = X(w.End);
                if (x1 < HeaderW || x0 > W) continue;
                var color = clip.Offline ? Color.Parse("#71717a") : tint;
                var bar = new Rect(x0, sy + 0.5, Math.Max(2, x1 - x0), Math.Max(1, strip - 1));
                ctx.DrawRectangle(Palette.Alpha(color, silent ? 0.08 : 0.2), strip >= MiniWaveAt ? Palette.Pen(Palette.Alpha(color, silent ? 0.2 : 0.45)) : null,
                    bar, Math.Min(3, bar.Height / 2), Math.Min(3, bar.Height / 2));
                if (strip < MiniWaveAt || clip.Offline || clip.Audio == null) continue;
                var geo = Waveform(clip, clip.Clip, w, bar.Top + 1, bar.Bottom - 1, W);
                using (ctx.PushClip(bar))
                    ctx.DrawGeometry(null, Palette.Pen(silent ? Palette.Faint : Palette.Alpha(color, 0.8), 1), geo);
            }
        }
    }

    Rect FoldRect(Folder f) => new(20, FolderY(f) + 5, 17, 24);
    Rect FolderIconRect(Folder f) => new(37, FolderY(f) + 5, 22, 24);
    /// <summary>Where a folder's name sits (and its in-place editor goes).</summary>
    public Rect FolderNameRect(Folder f) => new(60, FolderY(f) + 5, Math.Max(40, FolderRemoveRect(f).X - 66), 24);
    /// <summary>Just the drawn name of a folder (a double click there renames it; elsewhere on the row it folds).</summary>
    Rect FolderTitleRect(Folder f)
    {
        double w = Math.Min(Palette.Text(f.Name, 12.5, Palette.Fg, Palette.UiBold).Width, Math.Max(20, FolderRemoveRect(f).X - 62 - 64));
        return new(58, FolderY(f) + 5, Math.Max(20, w) + 8, 24);
    }
    Rect FolderRemoveRect(Folder f) => new(HeaderRight - 38, FolderY(f) + 5, 24, 24);
    /// <summary>Px of row from which a track's header shows its volume and pan.</summary>
    const double ControlsAt = 62;

    /// <summary>A folder's header: its tint, handle, fold arrow and icon, name and track count, and × (a folder only holds tracks: no M, S or fader).</summary>
    void DrawFolderHeader(DrawingContext ctx, Folder f)
    {
        double y = FolderY(f), fh = FolderRowH(f);
        if (fh <= 0 || y + fh < RulerH || y > Bounds.Height) return;
        if (f.Separator) { DrawSeparatorHeader(ctx, f, y, fh); return; }
        var c = FolderColor(f);
        var row = new Rect(0, y, HeaderW, fh);
        bool sel = f.Id == SelectedFolderId;
        ctx.FillRectangle(sel ? Palette.Raised : Palette.Panel, row);
        ctx.FillRectangle(Palette.Alpha(c, sel ? 0.18 : 0.12), row);
        if (sel) ctx.FillRectangle(Palette.Accent, new Rect(0, y, 2, fh));
        for (int k = 0; k < 3; k++)
        {
            ctx.FillRectangle(Palette.Faint, new Rect(8, y + 12 + k * 5, 2, 2));
            ctx.FillRectangle(Palette.Faint, new Rect(13, y + 12 + k * 5, 2, 2));
        }
        bool overFold = gesture == null && FoldRect(f).Contains(lastPointer);
        if (overFold) ctx.DrawRectangle(Palette.Hover, null, FoldRect(f), 4, 4);
        if (gesture == null && FolderIconRect(f).Contains(lastPointer)) ctx.DrawRectangle(Palette.Hover, null, FolderIconRect(f), 4, 4);
        ctx.DrawText(f.Collapsed ? "" : "", 10, Palette.Dim, new Point(24, y + 12), Icons);
        ctx.DrawText(f.Collapsed ? "" : "", 15, Palette.Alpha(c, 1), new Point(38, y + 9), Icons);
        int n = 0;
        foreach (var t in tracks) if (t.Track.FolderId == f.Id) n++;
        double maxW = Math.Max(20, FolderRemoveRect(f).X - 62 - 64);
        var name = Palette.Text(f.Name, 12.5, Palette.Fg, Palette.UiBold);
        if (editingNameId != f.Id)
        {
            ctx.DrawText(f.Name, 12.5, Palette.Fg, new Point(62, y + 9), Palette.UiBold, maxW);
            string count = n == 0 ? Loc.T("folder.empty") : n == 1 ? Loc.T("folder.count.one") : Loc.T("folder.count", n);
            ctx.DrawText(count, 11, Palette.Faint, new Point(62 + Math.Min(name.Width, maxW) + 8, y + 11), Palette.Ui);
        }
        if (lastPointer.Y >= y && lastPointer.Y < y + fh && lastPointer.X < HeaderW)
            DrawTrash(ctx, FolderRemoveRect(f), Hot(FolderRemoveRect(f)));
        ctx.DrawLine(Palette.Pen(Palette.Line), new Point(0, y + fh - 0.5), new Point(HeaderW, y + fh - 0.5));
    }

    /// <summary>A separator's header: its handle, its name in its color and a line to the right edge.</summary>
    void DrawSeparatorHeader(DrawingContext ctx, Folder f, double y, double fh)
    {
        var c = FolderColor(f);
        ctx.FillRectangle(Palette.Panel, new Rect(0, y, HeaderW, fh));
        for (int k = 0; k < 2; k++)
        {
            ctx.FillRectangle(Palette.Faint, new Rect(8, y + fh / 2 - 3 + k * 5, 2, 2));
            ctx.FillRectangle(Palette.Faint, new Rect(13, y + fh / 2 - 3 + k * 5, 2, 2));
        }
        double mid = Math.Round(y + fh / 2) + 0.5;
        double x = 24;
        if (editingNameId != f.Id && f.Name.Length > 0)
        {
            // the name as written: an upper-cased copy would be a new string every frame
            var name = Palette.Text(f.Name, 10.5, Palette.Alpha(c, 1), Palette.UiBold);
            double maxW = Math.Max(20, HeaderRight - x - 16);
            ctx.DrawText(f.Name, 10.5, Palette.Alpha(c, 1), new Point(x, y + (fh - name.Height) / 2), Palette.UiBold, maxW);
            x += Math.Min(name.Width, maxW) + 8;
        }
        ctx.DrawLine(Palette.Pen(Palette.Alpha(c, 0.45)), new Point(x, mid), new Point(HeaderW, mid));
    }

    /// <summary>The folder the dragged track would land in, or the folder a track is in.</summary>
    Folder? RailFolder(int i)
    {
        var t = tracks[i];
        if (IsDragged(t.Track.Id) && drop is { } d) return d.Folder is { } id ? folderById.GetValueOrDefault(id) : null;
        return FolderOf(t);
    }

    /// <summary>Where the dragged row will land: a tinted place with a dashed outline, under everything else.</summary>
    void DrawDropHole(DrawingContext ctx, double W)
    {
        if (gesture != "reorder" || !moved || drop == null) return;
        string id = dragFolder?.Id ?? dragIds[0];
        if (!targetTop.TryGetValue(id, out double top)) return;
        double h = dragFolder != null ? FolderRowH(dragFolder) : tracks.Where(t => dragIds.Contains(t.Track.Id)).Sum(t => RowH(t.Track));
        var r = new Rect(1, RulerH + top - scrollY + 1, Math.Max(0, W - 2), h - 2);
        var color = dragFolder != null ? FolderColor(dragFolder) : RailFolder(tracks.ToList().FindIndex(t => t.Track.Id == id)) is { } f ? FolderColor(f) : Palette.AccentColor;
        ctx.DrawRectangle(Palette.Alpha(color, 0.08), new Pen(Palette.Alpha(color, 0.7), 1.5, new DashStyle([4, 3], 0)), r, 5, 5);
    }

    /// <summary>
    /// A thin frame in the folder's color around its row and its tracks, header and lanes (asked by the user: a rail and a
    /// tint on every child looked like they were all selected). Left out while a reorder drags rows around: the drop
    /// target draws its own frame then.
    /// </summary>
    void DrawFolderFrames(DrawingContext ctx, double W)
    {
        if (gesture == "reorder" && moved) return;
        foreach (var f in folders)
        {
            if (f.Separator) continue;
            double top = FolderY(f), bottom = top + FolderRowH(f);
            for (int i = 0; i < tracks.Count; i++)
            {
                if (tracks[i].Track.FolderId != f.Id) continue;
                double y = LaneY(i);
                if (y > -1e8) bottom = Math.Max(bottom, y + LaneH(i));
            }
            if (bottom < RulerH || top > Bounds.Height) continue;
            var pen = Palette.Pen(Palette.Alpha(FolderColor(f), 0.8));
            ctx.DrawRectangle(null, pen, new Rect(0.5, top + 0.5, Math.Max(0, W - 1), Math.Max(0, bottom - top - 1)), 5, 5);
        }
    }

    /// <summary>
    /// What a dragged track does to folders, drawn over the rows: the folder it goes into gets an outline in its color
    /// around its row and children, and a label next to the track says "Entra en …" or "Sale de …".
    /// </summary>
    void DrawDropTarget(DrawingContext ctx, double W)
    {
        if (gesture != "reorder" || !moved || drop is not { } d || dragFolder != null || dragTrackId == null) return;
        int i = tracks.ToList().FindIndex(t => t.Track.Id == dragTrackId);
        if (i < 0) return;
        var from = FolderOf(tracks[i]);
        var into = d.Folder is { } id ? folderById.GetValueOrDefault(id) : null;
        if (into != null)
        {
            double top = FolderY(into), bottom = top + FolderRowH(into);
            foreach (var r in rows)
                if (r.Folder == null && (tracks[r.Track].Track.FolderId == into.Id || IsDragged(tracks[r.Track].Track.Id)) && targetTop.ContainsKey(tracks[r.Track].Track.Id))
                {
                    double ry = RulerH + r.Top - scrollY;
                    if (ry >= top) bottom = Math.Max(bottom, ry + r.H);
                }
            var c = FolderColor(into);
            ctx.DrawRectangle(null, Palette.Pen(Palette.Alpha(c, 1), 2), new Rect(1, top + 1, Math.Max(0, W - 2), bottom - top - 2), 6, 6);
        }
        string? text = into != null && into != from ? Loc.T("tl.drop.into", into.Name) : into == null && from != null ? Loc.T("tl.drop.out", from.Name) : null;
        if (text == null) return;
        var color = into != null ? FolderColor(into) : Palette.FgColor;
        var label = Palette.Text(text, 11, Palette.Alpha(color, 1), Palette.UiBold);
        double y = LaneY(i) + 8, x = HeaderW + 12;
        ctx.DrawRectangle(Palette.Alpha(Palette.AppColor, 0.85), Palette.Pen(Palette.Alpha(color, 0.6)), new Rect(x - 6, y - 3, label.Width + 12, label.Height + 6), 4, 4);
        ctx.DrawText(label, new Point(x, y));
    }

    // seconds between labels in the time ruler (down to 1 ms for the deepest zoom) and minor ticks inside each
    internal static readonly double[] TimeSteps = [0.001, 0.002, 0.005, 0.01, 0.02, 0.05, 0.1, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800];
    internal static readonly int[] TimeDivs = [5, 4, 5, 5, 4, 5, 5, 5, 5, 5, 4, 5, 5, 3, 6, 6, 4, 5, 5, 6];
    /// <summary>Px between the finest bars-mode ticks: zoomed in, beats split in halves until they get this close.</summary>
    const double FineTickPx = 12;

    /// <summary>Px under which a fixed grid's lines are too close together to draw.</summary>
    const double MinGridPx = 6;
    /// <summary>The grid's fixed spacing in seconds (a subdivision, or the snap's when linked), or 0 when it follows the zoom.</summary>
    double FixedGrid => gridMode switch { "auto" => 0, "snap" => Grid, _ => ClipEditing.GridSeconds(gridMode, Bpm) };

    /// <summary>Bars mode, one label per bar: the length of the finest minor tick (a beat, or a half of a half…).</summary>
    double FineTick(double beat)
    {
        double g = FixedGrid;
        if (g > 0) return g < beat && g * pps >= MinGridPx ? g : beat;
        double tick = beat;
        while (tick / 2 * pps >= FineTickPx * 2 && tick > 1e-4) tick /= 2;
        return tick;
    }

    /// <summary>Bars mode: the lines between two labelled bars (beats, or single bars when labels skip some; a fixed
    /// grid of a beat or more draws its own spacing, down to none for "bar").</summary>
    (double Sub, int N) BarSubs(int step, double beat, double bar)
    {
        if (step != 1) return (bar, step);
        double g = FixedGrid;
        return g >= beat ? (g, (int)Math.Round(bar / g)) : (beat, 4);
    }

    void DrawRuler(DrawingContext ctx, double W)
    {
        // the ruler's numbers sit under the marker strip
        const double rulerH = RulerH - MarkerH;
        using var _ = ctx.PushTransform(Matrix.CreateTranslation(0, MarkerH));
        ctx.FillRectangle(Palette.Panel, new Rect(HeaderW, 0, W - HeaderW, rulerH));
        if (!bars)
        {
            double[] steps = TimeSteps;
            int[] divs = TimeDivs;
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
                        ctx.DrawLine(minorPen, new Point(mx, rulerH - 6), new Point(mx, rulerH));
                        if (seconds * pps / div >= MinorLabelPx)
                        {
                            double ms = s + k * seconds / div;
                            string ml = ms >= 60 ? $"{(int)(ms / 60)}:{ms % 60:00.####}" : $"{ms:0.####}";
                            ctx.DrawText(ml, 10, Palette.Faint, new Point(mx + 3, 17), Palette.Mono);
                        }
                    }
                ctx.DrawLine(Palette.Pen(Palette.Line), new Point(x, 3), new Point(x, rulerH));
                string label = s >= 60 ? (seconds < 1 ? $"{(int)(s / 60)}:{s % 60:00.###}" : $"{(int)(s / 60)}:{s % 60:00}") : $"{s:0.###}s";
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
            ctx.DrawLine(tick, new Point(x, 3), new Point(x, rulerH));
            ctx.DrawText((b + 1).ToString(), 15, Palette.Fg, new Point(x + 4, 3), Palette.MonoBold);
            // minor ticks: beats inside a bar, or single bars when the labels skip some; zoomed in, see below
            if (step == 1 && FineTick(beat) < beat) continue;
            var (sub, subN) = BarSubs(step, beat, bar);
            if (sub * pps >= 6)
                for (int k = 1; k < subN; k++)
                {
                    double bx = X(b * bar + k * sub);
                    ctx.DrawLine(beatPen, new Point(bx, rulerH - 6), new Point(bx, rulerH));
                    if (sub * pps >= MinorLabelPx)
                        ctx.DrawText((step == 1 ? (int)Math.Round(k * sub / beat) + 1 : b + k + 1).ToString(), 10, Palette.Faint, new Point(bx + 3, 17), Palette.Mono);
                }
        }
        // zoomed in past the beats (native only): every half, quarter… of a beat, the beats labelled "bar.beat"
        double fine = FineTick(beat);
        if (step != 1 || fine >= beat) return;
        int perBeat = (int)Math.Round(beat / fine);
        for (long n = Math.Max(0, (long)Math.Floor(Sec(HeaderW) / fine)); ; n++)
        {
            double tx = X(n * fine);
            if (tx > W) break;
            if (n % (perBeat * 4) == 0) continue;
            bool onBeat = n % perBeat == 0;
            ctx.DrawLine(beatPen, new Point(tx, onBeat ? rulerH - 9 : rulerH - 5), new Point(tx, rulerH));
            if (onBeat) ctx.DrawText($"{n / (perBeat * 4) + 1}.{n / perBeat % 4 + 1}", 10, Palette.Faint, new Point(tx + 3, 17), Palette.Mono);
        }
    }

    // x of every ruler tick in view (major = labelled step, minor = beat / subdivision), rebuilt each Render so the
    // lanes can draw the same ticks as faint vertical lines behind their clips without allocating per lane
    readonly List<(double X, bool Major)> grid = [];

    /// <summary>Collects the ruler's tick positions (same steps and thresholds as <see cref="DrawRuler"/>) into <see cref="grid"/>.</summary>
    void BuildGrid(double W)
    {
        grid.Clear();
        // a zoom that isn't a positive number would make the loops below run forever
        if (!(pps > 0) || !double.IsFinite(pps)) return;
        if (!bars)
        {
            double[] steps = TimeSteps;
            int[] divs = TimeDivs;
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
            if (step == 1 && FineTick(beat) < beat) continue;
            var (sub, subN) = BarSubs(step, beat, bar);
            if (sub * pps >= 6)
                for (int k = 1; k < subN; k++) grid.Add((X(b * bar + k * sub), false));
        }
        double fine = FineTick(beat);
        if (step != 1 || fine >= beat) return;
        int perBeat = (int)Math.Round(beat / fine);
        for (long n = Math.Max(0, (long)Math.Floor(Sec(HeaderW) / fine)); ; n++)
        {
            double tx = X(n * fine);
            if (tx > W) break;
            if (n % (perBeat * 4) != 0) grid.Add((tx, false));
        }
    }

    void DrawLane(DrawingContext ctx, int i, double W)
    {
        var t = tracks[i];
        double y = LaneY(i), lh = LaneH(i);
        if (y + lh < RulerH || y > Bounds.Height) return;
        bool sel = i == Selected;
        // the lifted lane is opaque so the lanes it passes over don't show through
        if (t.Track.Id == liftedId || liftedAlso.Contains(t.Track.Id)) ctx.FillRectangle(Palette.App, new Rect(HeaderW, y, W - HeaderW, lh));
        if (Picked(i)) ctx.FillRectangle(Palette.Alpha(Palette.RaisedColor, 0.6), new Rect(HeaderW, y, W - HeaderW, lh));
        if (armed.Contains(i) && !Recording) ctx.FillRectangle(Palette.Alpha(Palette.RecColor, 0.06), new Rect(HeaderW, y, W - HeaderW, lh));
        var majorPen = Palette.Pen(Palette.Alpha(Palette.LineColor, 0.55));
        var minorPen = Palette.Pen(Palette.Alpha(Palette.LineColor, 0.22));
        foreach (var (gx, major) in grid)
            if (gx >= HeaderW) ctx.DrawLine(major ? majorPen : minorPen, new Point(gx, y), new Point(gx, y + lh));
        foreach (var c in t.Clips) DrawClip(ctx, t, c, y, sel, W);
        if (t.Track.IsMidi) DrawMidiClips(ctx, i, y, W);
        DrawJoins(ctx, i, W);
        if (Range is { } r && rangeTracks.Contains(t.Track.Id)) DrawRange(ctx, y, lh, W, r.Start, r.End);
        DrawSplitHover(ctx, i, y, lh);
        ctx.DrawLine(Palette.Pen(Palette.Line), new Point(HeaderW, y + lh - 0.5), new Point(W, y + lh - 0.5));
    }

    void DrawClip(DrawingContext ctx, LiveTrack t, LiveClip c, double y, bool sel, double W)
    {
        var model = DisplayClip(c);
        double lh = RowH(t.Track);
        var w = Window(c, model);
        double x0 = X(w.Start), x1 = X(w.End);
        sel = sel && (SelectedClipId == null || c.Clip.Id == SelectedClipId) || boxed.Contains(c.Clip.Id);
        if (x1 < HeaderW || x0 > W) return;
        var clip = new Rect(x0, y + 5, Math.Max(2, x1 - x0), lh - 10);
        // a track with a color tints its clips with it (native only); without one they stay as in the web
        var tint = TrackIcons.ColorOf(t.Track);
        var color = c.Offline ? Color.Parse("#71717a") : tint ?? Palette.AccentColor;
        bool silent = t.Silenced || tracks.Any(o => o.Soloed) && !t.Soloed;
        // the clip being carried to another track stays dimmed where it was until it is dropped
        bool carried = dragged == c && overIndex >= 0 && overIndex != fromIndex && !swapping;
        using var opacity = ctx.PushOpacity((!c.Offline && silent ? 0.5 : 1) * (carried ? 0.35 : 1) * (erasing.Contains(c.Clip.Id) ? 0.25 : 1));
        // the web shows the filmstrip from SHOW_THUMBS_AT; audio-only media (native only) keeps the plain clip
        bool thumbs = showVideo && t.Track.Video && !c.Offline && lh >= ShowThumbsAt && c.VideoPath != null;
        var fill = c.Offline ? Palette.Alpha(color, silent ? 0.05 : sel ? 0.16 : 0.10) : thumbs ? null
            : tint != null ? Palette.Alpha(color, sel ? 0.24 : 0.14) : Palette.Alpha(Palette.HoverColor, 0.4);
        var border = Palette.Pen(c.Offline ? Palette.Alpha(color, sel ? 0.9 : 0.45)
            : sel ? Palette.Alpha(color, tint != null ? 0.95 : 0.6) : tint != null ? Palette.Alpha(color, 0.45) : Palette.Line,
            (c.Offline || tint != null) && sel ? 1.5 : 1);
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
                var geo = Waveform(c, model, w, thumbs ? clip.Bottom - clip.Height * 0.55 : y + 6, y + lh - 6, W);
                // a tinted track keeps its color over the frames too (full strength there, the shade dims it); untinted goes white
                var wave = silent ? Palette.Faint : tint != null ? Palette.Alpha(color, sel || thumbs ? 1 : 0.8)
                    : thumbs && !sel ? Palette.Alpha(Colors.White, 0.85) : sel ? Palette.Accent : Palette.Alpha(Palette.DimColor, 0.8);
                ctx.DrawGeometry(null, Palette.Pen(wave, 1), geo);
                if (TransposeProgress?.Invoke(t) is { } progress) DrawWorking(ctx, clip, Math.Max(x0, HeaderW), progress, TransposingTexts, null);
                else if (TempoProgress?.Invoke(c) is { } listened) DrawWorking(ctx, clip, Math.Max(x0, HeaderW), listened, TempoTexts, TempoGlyph);
                else if (ToMidiProgress?.Invoke(c) is { } heard) DrawWorking(ctx, clip, Math.Max(x0, HeaderW), heard, ToMidiTexts, ToMidiGlyph);
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
            bool editable = sel && Selected >= 0 && Selected < tracks.Count && tracks[Selected] == t && CurveEditable(Selected, c);
            if (!c.Offline && (editable || model.GainDb != 0 || model.GainCurve.Count > 0)) DrawCurve(ctx, c, model, w, clip, editable);
            bool locked = ClipEditing.IsLocked(t.Track, c.Clip);
            if (sel || clip.Contains(lastPointer)) DrawEdgeHandles(ctx, c, clip, x0 + f.FadeIn * pps, x1 - f.FadeOut * pps, locked);
            if (locked && clip.Width > 24) DrawClipLock(ctx, clip, !c.Offline && MediaBadge(clip) != null);
            if (!c.Offline && MediaBadge(clip) is { } badge) DrawMediaBadge(ctx, badge, c.VideoPath != null);
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
    /// A clip whose transpose is being made (asked by the user: you couldn't tell whether it worked), or whose tempo is
    /// being detected: the wave dims, a bar fills along its bottom and a label (<paramref name="texts"/>, by percent)
    /// says how far it is, after <paramref name="glyph"/> (a Fluent icon; null = the transpose's). It keeps sounding meanwhile.
    /// </summary>
    void DrawWorking(DrawingContext ctx, Rect clip, double left, float progress, string[] texts, string? glyph)
    {
        ctx.FillRectangle(Palette.Alpha(Colors.Black, 0.45), clip);
        var track = new Rect(clip.X, clip.Bottom - 4, clip.Width, 4);
        ctx.FillRectangle(Palette.Alpha(Colors.White, 0.08), track);
        ctx.FillRectangle(Palette.Accent, new Rect(track.X, track.Y, track.Width * Math.Clamp(progress, 0, 1), track.Height));
        int pct = Math.Clamp((int)(progress * 100), 0, 100);
        var label = Palette.Text(texts[pct], 11, Palette.Fg, Palette.Ui);
        var pill = new Rect(left + 6, clip.Y + 6, label.Width + 30, 20);
        if (pill.Right > clip.Right - 4) return; // a short clip keeps only the bar
        ctx.DrawRectangle(Palette.Alpha(Colors.Black, 0.7), Palette.Pen(Palette.Alpha(Palette.AccentColor, 0.5)), pill, 4, 4);
        if (glyph == null) FxIcon.Draw(ctx, FxIcon.Transpose, new Rect(pill.X + 7, pill.Center.Y - 6, 12, 12), Palette.Accent);
        else
        {
            var icon = Palette.Text(glyph, 12, Palette.Accent, Icons);
            ctx.DrawText(icon, new Point(pill.X + 7, pill.Center.Y - icon.Height / 2));
        }
        ctx.DrawText(label, new Point(pill.X + 24, pill.Center.Y - label.Height / 2));
    }

    // --- transitions (native only) ------------------------------------------------------------------

    /// <summary>More clips than this on one lane and its joins aren't looked for (their windows go on the stack).</summary>
    const int MaxJoinClips = 256;
    /// <summary>The join chip's side: a square big enough to see over the frames, like CapCut's.</summary>
    const double JoinChipSize = 20;

    /// <summary>Fills <paramref name="w"/> with the lane's clip windows as drawn (a drag shows where the clip goes).</summary>
    void JoinWindows(int lane, Span<ClipEnvelope.ClipWindow> w)
    {
        var t = tracks[lane];
        for (int k = 0; k < w.Length; k++) w[k] = Window(t.Clips[k]);
    }

    /// <summary>The out clip's index when clip <paramref name="k"/> starts where another ends and both have their media, or -1.</summary>
    static int JoinOut(LiveTrack t, ReadOnlySpan<ClipEnvelope.ClipWindow> w, int k)
    {
        int o = ClipTransitions.FindOut(w, k);
        return o >= 0 && !t.Clips[k].Offline && !t.Clips[o].Offline ? o : -1;
    }

    /// <summary>The chip of a join at <paramref name="x"/>: centered on the join, halfway down the clips.</summary>
    Rect JoinChip(int lane, double x) =>
        new(Math.Round(x - JoinChipSize / 2), Math.Round(LaneY(lane) + LaneH(lane) / 2 - JoinChipSize / 2), JoinChipSize, JoinChipSize);

    /// <summary>
    /// The joins between clips of a track (native only, asked by the user): a square chip on each, centered on it
    /// halfway down the clips, so it shows over the frames. White with a bar (|) while it is a cut; of the accent
    /// with an X (crossfade: a fade out crossing a fade in, like the clips' fade lines) or a V (dip) when it has a transition,
    /// with arrows out of it to both ends of the stretch it covers (<see cref="DrawJoinSpan"/>). It sits over the edge handles there and wins their clicks. A click opens its menu.
    /// </summary>
    void DrawJoins(DrawingContext ctx, int lane, double W)
    {
        var t = tracks[lane];
        int n = t.Clips.Count;
        if (n < 2 || n > MaxJoinClips || LaneH(lane) < JoinChipSize + 12) return;
        Span<ClipEnvelope.ClipWindow> w = stackalloc ClipEnvelope.ClipWindow[n];
        JoinWindows(lane, w);
        for (int k = 0; k < n; k++)
        {
            if (JoinOut(t, w, k) < 0) continue;
            var model = DisplayClip(t.Clips[k]);
            var join = ClipTransitions.Find(w, k, ClipTransitions.Parse(model.Transition), model.TransitionSec);
            double x = X(w[k].Start);
            if (x < HeaderW || x > W) continue;
            var chip = JoinChip(lane, x);
            bool over = gesture == null && chip.Inflate(2).Contains(lastPointer);
            if (join is { } j) DrawJoinSpan(ctx, chip, Math.Max(HeaderW, X(j.Start)), X(j.End));
            var fill = join != null ? Palette.AccentColor : Palette.FgColor;
            ctx.DrawRectangle(Palette.Alpha(fill, over || join != null ? 1 : 0.9), Palette.Pen(Palette.Alpha(Colors.Black, 0.6)), chip.Deflate(0.5), 5, 5);
            var pen = Palette.Pen(Palette.Alpha(Palette.AppColor, 1), 1.5);
            double gx = chip.X + 5.5, gy = chip.Y + 6, gs = 8;
            switch (join?.Kind)
            {
                case TransitionKind.Crossfade:
                    ctx.DrawLine(pen, new Point(gx, gy), new Point(gx + gs, gy + gs));
                    ctx.DrawLine(pen, new Point(gx, gy + gs), new Point(gx + gs, gy));
                    break;
                case TransitionKind.Dip:
                    ctx.DrawLine(pen, new Point(gx, gy), new Point(gx + gs / 2, gy + gs));
                    ctx.DrawLine(pen, new Point(gx + gs / 2, gy + gs), new Point(gx + gs, gy));
                    break;
                default:
                    ctx.DrawLine(pen, new Point(gx + gs / 2, gy - 1), new Point(gx + gs / 2, gy + gs + 1));
                    break;
            }
        }
    }

    /// <summary>
    /// How long a transition lasts (asked by the user: a line along the clips' top barely showed): an accent line out
    /// of each side of the chip, at its height, to an arrowhead at that end, over a dark outline so it reads over the frames.
    /// </summary>
    static void DrawJoinSpan(DrawingContext ctx, Rect chip, double left, double right)
    {
        double y = chip.Center.Y, a = 4;
        var shade = Palette.Pen(Palette.Alpha(Colors.Black, 0.7), 3.5, PenLineCap.Round, PenLineJoin.Round);
        var pen = Palette.Pen(Palette.Accent, 1.5, PenLineCap.Round, PenLineJoin.Round);
        Arrows(shade);
        Arrows(pen);
        void Arrows(IPen p)
        {
            if (chip.Left - left > a + 2)
            {
                ctx.DrawLine(p, new Point(left, y), new Point(chip.Left, y));
                ctx.DrawLine(p, new Point(left, y), new Point(left + a, y - a));
                ctx.DrawLine(p, new Point(left, y), new Point(left + a, y + a));
            }
            if (right - chip.Right > a + 2)
            {
                ctx.DrawLine(p, new Point(chip.Right, y), new Point(right, y));
                ctx.DrawLine(p, new Point(right, y), new Point(right - a, y - a));
                ctx.DrawLine(p, new Point(right, y), new Point(right - a, y + a));
            }
        }
    }

    /// <summary>The clip coming in at the join chip under <paramref name="p"/>, or null.</summary>
    LiveClip? JoinChipAt(int lane, Point p)
    {
        if (lane < 0 || lane >= tracks.Count || p.X < HeaderW) return null;
        var t = tracks[lane];
        int n = t.Clips.Count;
        if (n < 2 || n > MaxJoinClips || LaneH(lane) < JoinChipSize + 12) return null;
        Span<ClipEnvelope.ClipWindow> w = stackalloc ClipEnvelope.ClipWindow[n];
        JoinWindows(lane, w);
        for (int k = 0; k < n; k++)
            if (JoinOut(t, w, k) >= 0 && JoinChip(lane, X(w[k].Start)).Inflate(2).Contains(p)) return t.Clips[k];
        return null;
    }

    /// <summary>
    /// The badge in a clip's top right corner (native only): a camera when its media has video, a note when it is
    /// audio only, so you can tell them apart without opening anything. It sits left of the edge handles and only
    /// says something; hovering it gives the tooltip (<see cref="UpdateMediaTip"/>).
    /// </summary>
    static Rect? MediaBadge(Rect clip) => clip.Width >= 48 && clip.Height >= 24 ? new Rect(clip.Right - 30, clip.Top + 4, 18, 16) : null;

    void DrawMediaBadge(DrawingContext ctx, Rect badge, bool video)
    {
        bool over = badge.Contains(lastPointer);
        ctx.DrawRectangle(Palette.Alpha(Colors.Black, over ? 0.75 : 0.55), null, badge, 4, 4);
        ctx.DrawText(video ? "" : "", 10, over ? Palette.Fg : Palette.Dim, new Point(badge.X + 4, badge.Y + 3), Icons);
    }

    /// <summary>The tooltip the timeline shows right now: the media badge's under the pointer, or none.</summary>
    string? mediaTip;

    void UpdateMediaTip()
    {
        string? tip = null;
        if (gesture == null && lastPointer.X >= HeaderW && HitTrack(lastPointer.Y) is var i and >= 0
            && ClipAt(i, lastPointer.X) is { Offline: false } c && MediaBadge(ClipRect(i, Window(c))) is { } badge && badge.Contains(lastPointer))
            tip = c.VideoPath == null ? "tl.media.audio" : tracks[i].Track.Video ? "tl.media.video" : "tl.media.videoOff";
        if (tip == mediaTip) return;
        mediaTip = tip;
        ToolTip.SetTip(this, tip == null ? null : Loc.T(tip));
    }

    /// <summary>"Transponiendo… 0%" to "100%", and the knob's bare "0%" to "100%", made once (nothing is built per frame).</summary>
    static readonly string[] TransposingTexts = [.. Enumerable.Range(0, 101).Select(p => Loc.T("tl.transposing", p))],
        PercentTexts = [.. Enumerable.Range(0, 101).Select(p => $"{p}%")],
        TempoTexts = [.. Enumerable.Range(0, 101).Select(p => Loc.T("tl.tempo", p))],
        ToMidiTexts = [.. Enumerable.Range(0, 101).Select(p => Loc.T("tl.toMidi", p))];
    /// <summary>The music note (Fluent's MusicNote) "Extraer MIDI" uses in the clip menu and while it listens.</summary>
    public const string ToMidiGlyph = "";
    /// <summary>The metronome-ish note "Detectar tempo" uses in the clip menu.</summary>
    public const string TempoGlyph = "";

    /// <summary>
    /// The clip's edge handles (native only): on each side a small tab split at the clip's middle. The top one sits
    /// where the fade ends and drags the fade; the bottom one sits on the edge and trims. The one under the pointer
    /// (or being dragged) lights up, so the two gestures never look alike.
    /// </summary>
    void DrawEdgeHandles(DrawingContext ctx, LiveClip c, Rect clip, double fadeInX, double fadeOutX, bool locked)
    {
        double mid = clip.Center.Y, quarter = clip.Height / 4;
        var lit = dragged == c ? dragMode : gesture == null && clip.Contains(lastPointer) ? HitHandle(c, lastPointer) : ClipEditMode.Move;
        // a small dark tab with two short lines, like the column's grip, centered in its half; the one you would grab lights up
        void Tick(double x, double cy, ClipEditMode mode, Color color)
        {
            bool on = lit == mode;
            double h = Math.Round(Math.Min(16, quarter * 2 - 4));
            if (h < 8) return;
            var tab = new Rect(Math.Round(x) - 3.5, Math.Round(cy - h / 2) + 0.5, 7, h);
            ctx.DrawRectangle(Palette.Alpha(on ? Palette.HoverColor : Palette.AppColor, 0.85), Palette.Pen(on ? Palette.Alpha(color, 0.7) : Palette.Line), tab, 2, 2);
            var line = Palette.Alpha(color, on ? 1 : 0.6);
            ctx.FillRectangle(line, new Rect(tab.X + 1.5, tab.Y + 2.5, 1, h - 5));
            ctx.FillRectangle(line, new Rect(tab.X + 3.5, tab.Y + 2.5, 1, h - 5));
        }
        Tick(Math.Max(clip.Left + 5, fadeInX), mid - quarter, ClipEditMode.FadeIn, Palette.AccentColor);
        Tick(Math.Min(clip.Right - 5, fadeOutX), mid - quarter, ClipEditMode.FadeOut, Palette.AccentColor);
        // a locked clip keeps its length: no trim tabs
        if (locked) return;
        Tick(clip.Left + 5, mid + quarter, ClipEditMode.TrimStart, Palette.FgColor);
        Tick(clip.Right - 5, mid + quarter, ClipEditMode.TrimEnd, Palette.FgColor);
    }

    /// <summary>A small padlock in a locked clip's top right corner, on a dark tab so it reads over the frames.</summary>
    static void DrawClipLock(DrawingContext ctx, Rect clip, bool besideBadge)
    {
        // left of the media badge when there is one
        var tab = new Rect(Math.Round(clip.Right - (besideBadge ? 52 : 22)) + 0.5, Math.Round(clip.Top + 4) + 0.5, 17, 16);
        ctx.DrawRectangle(Palette.Alpha(Palette.AppColor, 0.75), null, tab, 4, 4);
        ctx.DrawText("\uE72E", 10, Palette.Dim, new Point(tab.X + 3.5, tab.Y + 3), Icons);
    }

    /// <summary>
    /// The web's Filmstrip: the whole source (trims only hide its ends) split into as many 16:9 cells as fit at
    /// the lane's height, each showing the cached frame nearest its middle, cover-fitted and mirrored like the clip.
    /// </summary>
    void DrawFilmstrip(DrawingContext ctx, LiveClip c, Clip model, Rect clip, double W)
    {
        if (VideoThumbnails.Get(c.VideoPath!, c.LengthSec) is not { Length: > 0 } frames) return;
        double x0 = X(model.AtSec), srcW = c.LengthSec * pps;
        double h = clip.Height - 2;
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

    /// <summary>
    /// Min/max per pixel column with the clip gain and its curve applied, so the wave shows what is heard; cached
    /// until the zoom, scroll, size or gain changes (every curve edit assigns a new array, see <see cref="LiveClip.Curve"/>).
    /// </summary>
    StreamGeometry Waveform(LiveClip c, Clip model, ClipEnvelope.ClipWindow w, double top, double bottom, double W)
    {
        var a = c.Audio!;
        var pts = CurveOf(c, model);
        var key = new WaveKey(pps, scrollX, W, top, bottom, model.AtSec, model.TrimStartSec, model.TrimEndSec);
        if (waveCache.TryGetValue(c, out var hit) && hit.Key == key && hit.Curve == pts && hit.GainDb == model.GainDb) return hit.Geo;
        double clipGain = Math.Pow(10, model.GainDb / 20);
        var geo = new StreamGeometry();
        double mid = (top + bottom) / 2, half = (bottom - top) / 2;
        double binsPerSec = a.SampleRate / (double)MediaAudio.PeakBin;
        int xa = (int)Math.Max(HeaderW, X(w.Start));
        int xb = (int)Math.Min(W, X(w.End));
        double lo = model.TrimStartSec * binsPerSec, hi = (a.LengthSec - model.TrimEndSec) * binsPerSec;
        double perPx = a.SampleRate / pps;
        using (var g = geo.Open())
        {
            // zoomed in past the peaks (native only): the samples themselves, as min/max per column or, closer
            // than a sample per pixel, a line through them
            if (perPx < MediaAudio.PeakBin)
            {
                SampleWave(g, a, model, w, pts, clipGain, xa, xb, mid, half, perPx < 1.5);
                waveCache[c] = (geo, key, pts, model.GainDb);
                return geo;
            }
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
                double gain = clipGain * ClipGainCurve.GainAt(pts, Sec(px + 0.5) - w.Origin);
                double ya = mid - Math.Min(1, mx * gain) * half, yb = mid - Math.Max(-1, mn * gain) * half;
                if (yb - ya < 1) yb = ya + 1;
                g.BeginFigure(new Point(px + 0.5, ya), false);
                g.LineTo(new Point(px + 0.5, yb));
                g.EndFigure(false);
            }
        }
        waveCache[c] = (geo, key, pts, model.GainDb);
        return geo;
    }

    /// <summary>The wave from the samples (both channels mixed, like the peaks) for the deepest zoom levels.</summary>
    void SampleWave(StreamGeometryContext g, MediaAudio a, Clip model, ClipEnvelope.ClipWindow w, ClipGainCurve.Point[] pts,
        double clipGain, int xa, int xb, double mid, double half, bool line)
    {
        double sr = a.SampleRate;
        long lo = (long)(model.TrimStartSec * sr), hi = Math.Min(a.L.Length, (long)((a.LengthSec - model.TrimEndSec) * sr));
        double Y(double v, double gain) => mid - Math.Clamp(v * gain, -1, 1) * half;
        if (line)
        {
            bool open = false;
            for (int px = xa; px <= xb; px++)
            {
                double s = (Sec(px + 0.5) - model.AtSec) * sr;
                long i0 = (long)Math.Floor(s);
                if (i0 < lo || i0 + 1 >= hi) continue;
                double f = s - i0;
                double v = 0.5 * ((a.L[i0] + a.R[i0]) * (1 - f) + (a.L[i0 + 1] + a.R[i0 + 1]) * f);
                var pt = new Point(px + 0.5, Y(v, clipGain * ClipGainCurve.GainAt(pts, Sec(px + 0.5) - w.Origin)));
                if (!open) { g.BeginFigure(pt, false); open = true; }
                else g.LineTo(pt);
            }
            if (open) g.EndFigure(false);
            return;
        }
        for (int px = xa; px < xb; px++)
        {
            long s0 = Math.Max(lo, (long)((Sec(px) - model.AtSec) * sr));
            long s1 = Math.Min(hi, Math.Max(s0 + 1, (long)Math.Ceiling((Sec(px + 1) - model.AtSec) * sr)));
            if (s0 >= s1) continue;
            float mn = float.MaxValue, mx = float.MinValue;
            for (long i = s0; i < s1; i++)
            {
                float v = 0.5f * (a.L[i] + a.R[i]);
                if (v < mn) mn = v;
                if (v > mx) mx = v;
            }
            double gain = clipGain * ClipGainCurve.GainAt(pts, Sec(px + 0.5) - w.Origin);
            double ya = Y(mx, gain), yb = Y(mn, gain);
            if (yb - ya < 1) yb = ya + 1;
            g.BeginFigure(new Point(px + 0.5, ya), false);
            g.LineTo(new Point(px + 0.5, yb));
            g.EndFigure(false);
        }
    }

    /// <summary>How far a track in a folder moves its handle, badge, name and sliders right, so what is inside shows at a glance.</summary>
    const double FolderIndent = 24;
    double Indent(int i) => RailFolder(i) != null ? FolderIndent : 0;
    /// <summary>The badge at the narrowest column; a wider one grows it (asked by the user) by <see cref="BadgeGrowth"/>
    /// px per px of column, up to <see cref="MaxBadge"/> and to what the lane's height leaves. The cap keeps it beside the
    /// name and volume rows: grown into the effect rows it pushed them around and looked off.</summary>
    const double MinBadge = 28, MaxBadge = BaseHeaderH - 12, BadgeGrowth = 0.25;
    double BadgeSize(int i) => Math.Max(MinBadge, Math.Min(Math.Min(MaxBadge, MinBadge + (HeaderW - MinHeaderW) * BadgeGrowth), LaneH(i) - 12));
    /// <summary>How far a grown badge pushes the name and the volume right.</summary>
    double BadgeShift(int i) => BadgeSize(i) - MinBadge;
    /// <summary>The name and the volume also open a gap from a grown badge, which reaches down beside them: at the
    /// narrowest column the volume sits under the badge, so the gap grows with the first px of badge growth instead
    /// of jumping in.</summary>
    const double NameGap = 4, VolumeGap = 14;
    double NameShift(int i) => BadgeShift(i) + Math.Min(BadgeShift(i), NameGap);
    double VolumeShift(int i) => BadgeShift(i) + Math.Min(BadgeShift(i), VolumeGap);
    Rect BadgeRect(int i) { double s = BadgeSize(i); return new(25 + Indent(i), LaneY(i) + 6, s, s); }
    Rect ArmRect(int i) => new(HeaderRight - 122, LaneY(i) + 8, 24, 24);
    Rect InputRect(int i) => new(HeaderRight - 166, LaneY(i) + 8, 40, 24);
    Rect MuteRect(int i) => new(HeaderRight - 94, LaneY(i) + 8, 24, 24);
    Rect SoloRect(int i) => new(HeaderRight - 66, LaneY(i) + 8, 24, 24);
    Rect RemoveRect(int i) => new(HeaderRight - 38, LaneY(i) + 8, 24, 24);
    /// <summary>Room for the name: an armed track shows its input left of the ●, so the name gets shorter.</summary>
    double NameW(int i) => (i >= 0 && i < tracks.Count && tracks[i].Track.IsMidi ? InstrumentRect(i).X - 62
        : armed.Contains(i) && !Recording && InputLabel != null ? InputRect(i).X - 62 : HeaderRight - 186) - Indent(i) - NameShift(i);
    /// <summary>Where a track's name sits (and its in-place editor goes).</summary>
    Rect NameRect(int i) => new(54 + Indent(i) + NameShift(i), LaneY(i) + 7, NameW(i) + 8, 26);
    /// <summary>Just the drawn name of a track (a double click there renames it), not the empty space after it.</summary>
    Rect TitleRect(int i)
    {
        double w = Math.Min(Palette.Text(tracks[i].Track.Name, 12.5, Palette.Fg, Palette.UiBold).Width, NameW(i));
        return new(54 + Indent(i) + NameShift(i), LaneY(i) + 7, Math.Max(20, w) + 8, 26);
    }
    /// <summary>The handle that drags a track, left of its badge.</summary>
    bool OnHandle(int i, Point p) => p.X < 24 + Indent(i);
    /// <summary>The name rect of a track or a folder by id, or null when it is not on screen.</summary>
    public Rect? NameRectOf(string id)
    {
        if (folderById.TryGetValue(id, out var f))
            return !rows.Any(r => r.Folder == f) || f.Separator && f.HiddenInTimeline ? null
                : f.Separator ? new Rect(22, FolderY(f), Math.Max(60, HeaderRight - 30), FolderRowH(f)) : FolderNameRect(f);
        int i = tracks.ToList().FindIndex(t => t.Track.Id == id);
        return i >= 0 && rows.Any(r => r.Folder == null && r.Track == i) ? NameRect(i) : null;
    }
    // a wider column gives its extra width to the volume slider and moves the pan knob with it
    double Extra => HeaderW - LayoutHeaderW;
    Rect VolumeRect(int i) => new(52 + Indent(i) + VolumeShift(i), LaneY(i) + 40, 92 + Extra - Indent(i) - VolumeShift(i), 20);
    Rect PanRect(int i) => new(186 + Extra, LaneY(i) + 40, 24, 24);

    /// <summary>A header's volume fader: a thick track with a mark at 100 and a fader cap (tracks and folders).</summary>
    static void DrawVolume(DrawingContext ctx, Rect v, double volume, IBrush fill)
    {
        double cy = v.Center.Y, px = v.X + Math.Clamp(volume / 1.5, 0, 1) * v.Width, unity = Math.Round(v.X + v.Width / 1.5) + 0.5;
        ctx.DrawRectangle(Palette.Hover, null, new Rect(v.X, cy - 2.5, v.Width, 5), 2.5, 2.5);
        ctx.DrawRectangle(fill, null, new Rect(v.X, cy - 2.5, Math.Max(0, px - v.X), 5), 2.5, 2.5);
        ctx.DrawLine(Palette.Pen(Palette.Faint), new Point(unity, cy - 7), new Point(unity, cy - 4));
        ctx.DrawLine(Palette.Pen(Palette.Faint), new Point(unity, cy + 4), new Point(unity, cy + 7));
        var cap = new Rect(px - 5, cy - 8, 10, 16);
        ctx.DrawRectangle(Palette.Fg, Palette.Pen(Palette.Panel), cap, 3, 3);
        ctx.DrawLine(Palette.Pen(Palette.Panel, 1.5), new Point(cap.X + 2.5, cy), new Point(cap.Right - 2.5, cy));
    }

    void DrawHeader(DrawingContext ctx, int i)
    {
        var t = tracks[i];
        double y = LaneY(i), lh = LaneH(i);
        if (y + lh < RulerH || y > Bounds.Height) return;
        bool sel = Picked(i);
        ctx.FillRectangle(sel ? Palette.Raised : Palette.Panel, new Rect(0, y, HeaderW, lh));
        bool isArmed = armed.Contains(i);
        if (isArmed || RecordingInto(i)) ctx.FillRectangle(Palette.Alpha(Palette.RecColor, 0.10), new Rect(0, y, HeaderW, lh));
        // a track in a folder is indented; the folder's color goes on the frame around the group (DrawFolderFrames)
        double ind = Indent(i);
        if (sel) ctx.FillRectangle(Palette.Accent, new Rect(0, y, 2, lh));

        for (int k = 0; k < 3; k++)
        {
            ctx.FillRectangle(Palette.Faint, new Rect(8 + ind, y + 13 + k * 5, 2, 2));
            ctx.FillRectangle(Palette.Faint, new Rect(13 + ind, y + 13 + k * 5, 2, 2));
        }
        var badge = BadgeRect(i);
        TrackIcons.DrawBadge(ctx, badge, t.Track, i + 1, false, Math.Round(badge.Height * 0.18));
        double nameX = 58 + ind + NameShift(i);
        bool chip = isArmed && !Recording && InputLabel != null;
        double nameW = NameW(i);
        // a locked track says so with a padlock after its name, which gives it the room
        if (t.Track.Locked && editingNameId != t.Track.Id && !RecordingInto(i))
        {
            nameW = Math.Max(0, nameW - 16);
            double shownW = Math.Min(Palette.Text(t.Track.Name, 12.5, Palette.Fg, Palette.UiBold).Width, nameW);
            ctx.DrawText("\uE72E", 11, Palette.Faint, new Point(nameX + shownW + 5, y + 13), Icons);
        }
        if (RecordingInto(i))
        {
            ctx.DrawText(Loc.T("timeline.recording"), 13, Palette.Rec, new Point(nameX, y + 11), Palette.Ui, nameW);
            ctx.DrawEllipse(Palette.Rec, null, new Point(RemoveRect(i).Center.X, y + 20), 4, 4);
        }
        else if (editingNameId != t.Track.Id)
        {
            // a transposed track says so after its name ("+2", asked by the user), dim while the transpose is made
            if (t.Track.Transpose != 0)
            {
                var progress = TransposeProgress?.Invoke(t);
                var tag = Palette.Text(TransposeTags[Math.Clamp((int)Math.Round(t.Track.Transpose), -12, 12) + 12], 10.5, progress == null ? Palette.Accent : Palette.Faint, Palette.Mono);
                double tw = tag.Width + 22;
                double shown = Math.Min(Palette.Text(t.Track.Name, 12.5, Palette.Fg, Palette.UiBold).Width, Math.Max(0, nameW - tw - 6));
                ctx.DrawText(t.Track.Name, 12.5, Palette.Fg, new Point(nameX, y + 11), Palette.UiBold, Math.Max(0, nameW - tw - 6));
                var r = new Rect(nameX + shown + 6, y + 10, tw, 18);
                ctx.DrawRectangle(progress == null ? Palette.AccentSoft : Palette.Hover, Palette.Pen(progress == null ? Palette.Alpha(Palette.AccentColor, 0.4) : Palette.Line), r, 4, 4);
                FxIcon.Draw(ctx, FxIcon.Transpose, new Rect(r.X + 5, r.Center.Y - 5, 10, 10), progress == null ? Palette.Accent : Palette.Faint);
                ctx.DrawText(tag, new Point(r.X + 18, r.Center.Y - tag.Height / 2));
            }
            else ctx.DrawText(t.Track.Name, 12.5, Palette.Fg, new Point(nameX, y + 11), Palette.UiBold, nameW);
        }
        if (chip) DrawInputChip(ctx, InputRect(i), InputLabel!(t));

        if (!RecordingInto(i) && lastPointer.Y >= y && lastPointer.Y < y + lh)
            DrawTrash(ctx, RemoveRect(i), Hot(RemoveRect(i)));
        ctx.DrawLine(Palette.Pen(Palette.Line), new Point(0, y + lh - 0.5), new Point(HeaderW, y + lh - 0.5));
        DrawArm(ctx, ArmRect(i), isArmed || RecordingInto(i), Hot(ArmRect(i)));
        if (tracks[i].Track.IsMidi) DrawInstrumentChip(ctx, i);

        var ch = t.Channel;
        string pan = Math.Abs(ch.Pan) < 0.01 ? "C" : ch.Pan < 0 ? $"L{-ch.Pan * 100:0}" : $"R{ch.Pan * 100:0}";
        if (lh >= ControlsAt)
        {
            // the volume stands out from the effect sliders (asked by the user): a thicker track with a mark at 100
            // and a fader cap instead of a dot
            DrawVolume(ctx, VolumeRect(i), ch.Volume, Palette.Accent);
            ctx.DrawText($"{ch.Volume * 100:0}", 11, Palette.Fg, new Point(148 + Extra, y + 42), Palette.Mono);
            var pr = PanRect(i);
            ctx.DrawEllipse(Palette.Raised, Palette.Pen(Palette.Line), pr.Center, 10, 10);
            double angle = ch.Pan * Math.PI * 0.75 - Math.PI / 2;
            ctx.DrawLine(Palette.Pen(Palette.Accent, 2), pr.Center,
                new Point(pr.Center.X + Math.Cos(angle) * 8, pr.Center.Y + Math.Sin(angle) * 8));
            ctx.DrawText(pan, 10.5, Palette.Faint, new Point(216 + Extra, y + 43), Palette.Mono);
        }

        // the sends' knobs in one row, as big as the lane allows, empty tracks included (they are channel settings)
        double kd = FxKnobD(i);
        int knobs = kd > 0 ? KnobCount(i) : 0;
        var sendKnobs = knobs > 0 ? SendsOf(t) : [];
        for (int k = 0; k < knobs; k++)
            {
                var (on, amount) = FxState(t, k);
                var kr = FxKnobRect(i, k, kd);
                bool dragging = gesture == "fx" && fromIndex == i && fxRow == k;
                bool hot = dragging || (gesture == null && kr.Inflate(4).Contains(lastPointer));
                DrawFxKnob(ctx, kr, amount, on, hot);
                // the amount sits inside a big knob; a small one shows it in place of its label while touched
                string value = Hundreds[Math.Clamp((int)Math.Round(amount * 100), 0, 100)];
                bool inside = kd >= 36;
                if (inside)
                {
                    var vt = Palette.Text(value, kd >= 50 ? 12 : 10.5, on ? Palette.Fg : Palette.Faint, Palette.Mono);
                    ctx.DrawText(vt, new Point(kr.Center.X - vt.Width / 2, kr.Center.Y - vt.Height / 2));
                }
                var lr = FxLabelRect(i, k, kd);
                if (!inside && hot)
                {
                    var vt = Palette.Text(value, 10.5, Palette.Fg, Palette.Mono);
                    ctx.DrawText(vt, new Point(lr.Center.X - vt.Width / 2, lr.Center.Y - vt.Height / 2));
                    continue;
                }
                var brush = on ? Palette.Accent : Palette.Faint;
                var label = Palette.Text(sendKnobs[k].Name, 10.5, on ? Palette.Fg : Palette.Faint, Palette.Ui);
                // a narrow cell keeps only the icon
                bool withText = 16 + label.Width <= lr.Width - 4;
                double lx = lr.Center.X - (withText ? 16 + label.Width : 12) / 2;
                FxIcon.Draw(ctx, sendKnobs[k].Icon, new Rect(lx, lr.Center.Y - 6, 12, 12), brush);
                if (withText) ctx.DrawText(label, new Point(lx + 16, lr.Center.Y - label.Height / 2));
            }

        DrawToggle(ctx, MuteRect(i), "M", ch.Muted, Palette.Brush("#f59e0b"), Hot(MuteRect(i)));
        DrawToggle(ctx, SoloRect(i), "S", ch.Solo, Palette.Accent, Hot(SoloRect(i)));

        if (!MetersOnLayer) DrawMeters(ctx, i);
    }

    /// <summary>An effect knob, drawn like the mixer's big <see cref="Knob"/>: a cap, a 270° track lit from zero to the
    /// amount (accent when on, grey when off) and a pointer. The arcs are short round-capped lines instead of a
    /// geometry, so a redraw (every pointer move) allocates nothing.</summary>
    static void DrawFxKnob(DrawingContext ctx, Rect r, double amount, bool on, bool hot, bool bipolar = false)
    {
        double d = r.Width, stroke = Math.Max(2.5, d / 12), rad = d / 2 - stroke;
        var c = r.Center;
        Point Polar(double deg, double len)
        {
            double a = (deg - 90) * Math.PI / 180;
            return new Point(c.X + len * Math.Cos(a), c.Y + len * Math.Sin(a));
        }
        void Arc(IPen pen, double from, double to)
        {
            int n = Math.Max(1, (int)Math.Ceiling((to - from) / 6));
            for (int s = 0; s < n; s++) ctx.DrawLine(pen, Polar(from + (to - from) * s / n, rad), Polar(from + (to - from) * (s + 1) / n, rad));
        }
        double angle = -135 + Math.Clamp(amount, 0, 1) * 270;
        ctx.DrawEllipse(Palette.Raised, Palette.Pen(hot ? Palette.Faint : Palette.Line), c, rad - stroke * 1.1, rad - stroke * 1.1);
        Arc(Palette.Pen(hot ? Palette.Faint : Palette.Line, stroke, PenLineCap.Round), -135, 135);
        // a bipolar knob (Transponer) lights from the top, where its middle value is, to either side
        if (bipolar) { if (Math.Abs(angle) > 0.5) Arc(Palette.Pen(on ? Palette.Accent : Palette.Faint, stroke, PenLineCap.Round), Math.Min(0, angle), Math.Max(0, angle)); }
        else if (angle > -134.5) Arc(Palette.Pen(on ? Palette.Accent : Palette.Faint, stroke, PenLineCap.Round), -135, angle);
        // a big knob keeps its centre for the amount, so its pointer is a short tick at the rim
        ctx.DrawLine(Palette.Pen(on ? Palette.Fg : Palette.Dim, d >= 36 ? 2.5 : 2, PenLineCap.Round),
            Polar(angle, d >= 36 ? rad * 0.62 : rad * 0.3), Polar(angle, rad - stroke * 1.6));
    }

    /// <summary>The header's meter: two thin bars at the right edge.</summary>
    void DrawMeters(DrawingContext ctx, int i)
    {
        double y = LaneY(i);
        if (y + LaneH(i) < RulerH || y > Bounds.Height) return;
        double mh = LaneH(i) - 20, mx = HeaderRight - 10;
        DrawMeterBar(ctx, new Rect(mx, y + 10, 4, mh), tracks[i].ShowL);
        DrawMeterBar(ctx, new Rect(mx + 6, y + 10, 4, mh), tracks[i].ShowR);
    }

    /// <summary>Draws the <see cref="Layer"/>: the header meters (unless they are in the background) and the playhead.</summary>
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
            ctx.DrawLine(pen, new Point(px, 0), new Point(px, H));
            using (ctx.PushTransform(Matrix.CreateTranslation(px, MarkerH)))
                ctx.DrawGeometry(playColor, null, PlayheadTriangle);
        }
    }

    /// <summary>The playhead's head in the ruler, at x = 0 (moved into place when drawn).</summary>
    static readonly Geometry PlayheadTriangle = Geometry.Parse("M -6,0 L 6,0 L 0,8 Z");

    /// <summary>True while the pointer rests on this header button with nothing dragged, so it lights up like the
    /// window's buttons do on hover.</summary>
    bool Hot(Rect r) => gesture == null && !Recording && r.Contains(lastPointer);

    /// <summary>The hover glow of a drawn button: two soft rings of its color around it, like HoverGlow on the
    /// window's buttons (an effect can't be drawn per button here, and these brushes are cached).</summary>
    static void DrawGlow(DrawingContext ctx, Rect r, Color c)
    {
        ctx.DrawRectangle(Palette.Alpha(c, 0.10), null, r.Inflate(3), 7, 7);
        ctx.DrawRectangle(Palette.Alpha(c, 0.20), null, r.Inflate(1.5), 5.5, 5.5);
    }

    /// <summary>The remove button of a header: a trash can that turns red, on a box with the red glow, on hover.</summary>
    static void DrawTrash(DrawingContext ctx, Rect r, bool hot)
    {
        if (hot)
        {
            DrawGlow(ctx, r, Palette.RecColor);
            ctx.DrawRectangle(Palette.Hover, Palette.Pen(Palette.Alpha(Palette.RecColor, 0.45)), r, 4, 4);
        }
        ctx.DrawText("", 12, hot ? Palette.Rec : Palette.Faint, r.TopLeft + new Point(6, 6), Icons);
    }

    /// <summary>The record-arm toggle: a red dot, lit red while the track takes the next take.</summary>
    static void DrawArm(DrawingContext ctx, Rect r, bool on, bool hot)
    {
        if (hot) DrawGlow(ctx, r, Palette.RecColor);
        if (hot && !on)
        {
            ctx.DrawRectangle(Palette.Hover, Palette.Pen(Palette.Alpha(Palette.RecColor, 0.45)), r, 4, 4);
            ctx.DrawEllipse(Palette.Rec, null, r.Center, 4.5, 4.5);
            return;
        }
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

    /// <summary>An M / S toggle; on hover it glows in the accent and, while off, its letter and border brighten.</summary>
    static void DrawToggle(DrawingContext ctx, Rect r, string label, bool on, IBrush onBrush, bool hot)
    {
        if (hot) DrawGlow(ctx, r, Palette.AccentColor);
        var border = on ? null : Palette.Pen(hot ? Palette.Alpha(Palette.AccentColor, 0.5) : Palette.Line);
        ctx.DrawRectangle(on ? onBrush : Palette.Hover, border, r, 4, 4);
        var t = Palette.Text(label, 10.5, on ? Palette.App : hot ? Palette.Fg : Palette.Dim, Palette.UiBold);
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
        if (AddRect.Contains(p) && AddRect.Y >= RulerH)
        {
            if (buttons.IsLeftButtonPressed) NewTrackRequested?.Invoke(p);
            e.Handled = true;
            return;
        }
        var row = HitRow(p.Y);
        int i = row is { Folder: null } h ? h.Track : -1;
        if (buttons.IsLeftButtonPressed || buttons.IsRightButtonPressed)
            if (StripPressed(p, e, buttons.IsRightButtonPressed)) return;
        if (buttons.IsRightButtonPressed)
        {
            if (p.Y < RulerH && p.X > HeaderW) RulerMenuRequested?.Invoke(p);
            else if (row?.Folder is { } folder) FolderContextRequested?.Invoke(folder, p);
            // the empty part of the header, under the last row, offers a new track
            else if (row == null && p.X < HeaderW && p.Y >= RulerH) NewTrackRequested?.Invoke(p);
            else if (i >= 0 && i < tracks.Count && p.Y >= RulerH)
            {
                if (p.X > HeaderW && tracks[i].Track.IsMidi && MidiContext(i, p)) { e.Handled = true; return; }
                if (p.X < HeaderW && tracks[i].Track.IsMidi && InstrumentRect(i).Contains(p)) { Select(i); InstrumentChipPressed(i, p, true); e.Handled = true; return; }
                // The header asks for the track menu (no clip); the lane, for the clip under the pointer.
                var clip = p.X > HeaderW ? ClipAt(i, p.X) : null;
                SelectedClipId = clip?.Clip.Id ?? tracks[i].Clips.FirstOrDefault(c => c.Clip.Id == SelectedClipId)?.Clip.Id;
                // on a picked track the others stay picked, so the menu's folder entries move them all; on a boxed
                // clip the others stay boxed, so the menu acts on them all
                if (picked.Count > 1 && picked.Contains(tracks[i].Track.Id) || clip != null && boxed.Count > 1 && boxed.Contains(clip.Clip.Id)) SelectOne(i);
                else Select(i);
                ClipContextRequested?.Invoke(tracks[i], clip, p);
            }
            e.Handled = true;
            return;
        }
        if (!buttons.IsLeftButtonPressed) return;
        ClearRange();
        if (OnGrip(p))
        {
            originalHeaderW = HeaderW;
            StartGesture("header", e);
            return;
        }
        if (RowEdgeAt(p) is { } edge)
        {
            if (e.ClickCount == 2) { RowHeightRequested?.Invoke(edge, null); e.Handled = true; return; }
            var r = rows.First(x => RowIdOf(x) == edge);
            heightFrom = r.H;
            heightDrag = (edge, r.H);
            StartGesture("row-height", e);
            return;
        }
        if (p.Y < RulerH)
        {
            if (p.X > HeaderW) { StartGesture("scrub", e); UpdateGesture(); }
            return;
        }
        if (row?.Folder is { } pressed)
        {
            FolderPressed(pressed, p, e);
            return;
        }
        if (JoinChipAt(i, p) is { } incoming)
        {
            TransitionRequested?.Invoke(tracks[i], incoming, p);
            e.Handled = true;
            return;
        }
        if (tool != TimelineTool.Select && p.X >= HeaderW && ToolPressed(i, p, e)) return;
        if (i < 0 || i >= tracks.Count)
        {
            // under the last track a drag also draws the selection box
            if ((modifiers & KeyModifiers.Control) == 0 || p.X < HeaderW) Select(-1);
            if (p.X >= HeaderW && row == null) StartBox(e);
            return;
        }
        // Ctrl on a header picks or drops that track, Shift picks the range up to it (to move them together)
        if (p.X < HeaderW && (modifiers & (KeyModifiers.Control | KeyModifiers.Shift)) != 0 && !RemoveRect(i).Contains(p))
        {
            Pick(i, (modifiers & KeyModifiers.Shift) != 0);
            e.Handled = true;
            return;
        }
        fromIndex = overIndex = i;
        // any touch on a header (its handle or a control) selects that track first, except the remove button; the
        // handle of a picked track keeps the others picked, to carry them all
        bool carry = picked.Count > 1 && picked.Contains(tracks[i].Track.Id) && OnHandle(i, p);
        if (p.X < HeaderW && !carry && (Selected != i || picked.Count > 1) && !RemoveRect(i).Contains(p)) Select(i);
        if (OnHandle(i, p))
        {
            StartReorder(tracks[i].Track.Id, null, e);
            return;
        }
        if (p.X < HeaderW && RemoveRect(i).Contains(p))
        {
            RemoveTrackRequested?.Invoke(tracks[i].Track.Id, RemoveRect(i).BottomLeft + new Point(0, 4));
            return;
        }
        if (p.X < HeaderW && BadgeRect(i).Contains(p))
        {
            LookRequested?.Invoke(tracks[i], BadgeRect(i).BottomLeft + new Point(0, 4));
            e.Handled = true;
            return;
        }
        if (p.X < HeaderW && tracks[i].Track.IsMidi && InstrumentRect(i).Contains(p))
        {
            InstrumentChipPressed(i, p, false);
            e.Handled = true;
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
        if (p.X < HeaderW && e.ClickCount == 2 && !RecordingInto(i) && TitleRect(i).Contains(p))
        {
            NameEditRequested?.Invoke(tracks[i].Track.Id, NameRect(i));
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
            fxRow = hit.Knob;
            string fx = KnobKey(tracks[i], hit.Knob);
            if (!hit.Dial) EffectRequested?.Invoke(tracks[i], fx, true, null);
            else if (e.ClickCount == 2) EffectRequested?.Invoke(tracks[i], fx, false, 0);
            else
            {
                fxDragValue = FxState(tracks[i], hit.Knob).Amount;
                fxDragY = p.Y;
                StartGesture("fx", e);
            }
        }
        else if (LaneH(i) >= ControlsAt && VolumeRect(i).Contains(p))
        {
            if (e.ClickCount == 2) ChannelRequested?.Invoke(tracks[i], 1, null);
            else { StartGesture("volume", e); UpdateGesture(); }
        }
        else if (LaneH(i) >= ControlsAt && PanRect(i).Contains(p))
        {
            if (e.ClickCount == 2) ChannelRequested?.Invoke(tracks[i], null, 0);
            else { originalPan = tracks[i].Channel.Pan; StartGesture("pan-knob", e); }
        }
        else if (p.X >= HeaderW && (modifiers & KeyModifiers.Shift) != 0 && e.ClickCount == 1)
        {
            // Shift+drag over a lane selects a range, like the Rango tool (native only)
            StartRange(i, p, e);
        }
        else if (p.X >= HeaderW && tracks[i].Track.IsMidi) MidiPressed(i, p, e);
        else if (p.X >= HeaderW)
        {
            var clip = ClipAt(i, p.X);
            // a double click on a curve point removes it; anywhere else it moves the playhead
            if (e.ClickCount == 2 && clip != null && HitHandle(clip, p) == ClipEditMode.Move && CurvePointAt(i, clip, p) is var gone and >= 0)
            {
                RemoveCurvePoint(clip, gone);
                e.Handled = true;
                return;
            }
            if (e.ClickCount == 2)
            {
                SelectClip(i, clip?.Clip.Id);
                SeekRequested?.Invoke(Math.Max(0, ClipEditing.Snap(Sec(p.X), Grid, (modifiers & KeyModifiers.Alt) != 0)));
                return;
            }
            if (clip == null)
            {
                // a drag from here draws the selection box; with Ctrl it adds to what is selected
                if ((modifiers & KeyModifiers.Control) == 0) Select(tracks[i].Clips.Count == 0 ? i : -1);
                StartBox(e);
                return;
            }
            if (HitHandle(clip, p) == ClipEditMode.Move && CurvePointAt(i, clip, p) is var point and >= 0)
            {
                StartCurve(i, clip, point, e);
                return;
            }
            if (HitHandle(clip, p) == ClipEditMode.Move && OnCurve(i, clip, p))
            {
                StartCurve(i, clip, -1, e);
                return;
            }
            dragMode = HitHandle(clip, p);
            // a locked clip is selected but neither moved nor trimmed; its fades still drag
            if (ClipEditing.IsLocked(tracks[i].Track, clip.Clip) && dragMode is ClipEditMode.Move or ClipEditMode.TrimStart or ClipEditMode.TrimEnd)
            {
                SelectClip(i, clip.Clip.Id);
                LockedTouched?.Invoke(tracks[i], clip);
                return;
            }
            // with Ctrl, a click (released without moving) adds the clip to the selection or takes it out
            toggleBase = (modifiers & KeyModifiers.Control) != 0 && e.ClickCount == 1 ? SelectionSeed() : null;
            // a boxed clip grabbed by its body carries the other boxed ones; its edges trim it alone
            if (boxed.Count > 1 && boxed.Contains(clip.Clip.Id) && dragMode == ClipEditMode.Move)
            {
                SelectedClipId = clip.Clip.Id;
                SelectOne(i);
                StartCarry(clip);
            }
            else SelectClip(i, clip.Clip.Id);
            dragged = clip;
            original = ClipEditing.Copy(clip.Clip);
            preview = ClipEditing.Copy(clip.Clip);
            StartGesture("clip", e);
            return;
        }
        Select(i);
        InvalidateVisual();
    }

    /// <summary>A press on a folder's row: its handle drags it, the arrow folds it, M S and × act on it, a double click on its name renames it and anywhere else folds it.</summary>
    void FolderPressed(Folder f, Point p, PointerPressedEventArgs e)
    {
        e.Handled = true;
        if (f.Separator)
        {
            // a separator moves by its handle and renames by a double click; it is never selected or folded
            if (p.X < 20) StartReorder(null, f, e);
            else if (e.ClickCount == 2 && p.X < HeaderW && NameRectOf(f.Id) is { } nameRect) NameEditRequested?.Invoke(f.Id, nameRect);
            return;
        }
        // like a track's header, any touch on the row selects the folder first, except the remove button
        if (!FolderRemoveRect(f).Contains(p) || p.X >= HeaderW) SelectFolder(f.Id);
        if (p.X < 20) { StartReorder(null, f, e); return; }
        if (p.X >= HeaderW)
        {
            if (e.ClickCount == 2) FolderFoldRequested?.Invoke(f);
            return;
        }
        if (FoldRect(f).Contains(p)) FolderFoldRequested?.Invoke(f);
        else if (FolderIconRect(f).Contains(p)) FolderLookRequested?.Invoke(f, FolderIconRect(f).BottomLeft + new Point(0, 4));
        else if (FolderRemoveRect(f).Contains(p)) FolderRemoveRequested?.Invoke(f, FolderRemoveRect(f).BottomLeft + new Point(0, 4));
        else if (e.ClickCount == 2 && FolderTitleRect(f).Contains(p)) NameEditRequested?.Invoke(f.Id, FolderNameRect(f));
        else if (e.ClickCount == 2) FolderFoldRequested?.Invoke(f);
    }

    /// <summary>Lifts a track or a folder by its handle: it follows the pointer and the others make room (<see cref="FindDrop"/>).</summary>
    void StartReorder(string? trackId, Folder? folder, PointerPressedEventArgs e)
    {
        dragTrackId = trackId;
        dragFolder = folder;
        string id = folder?.Id ?? trackId!;
        var seq = Seq();
        // a picked track carries every picked one on screen with it, stacked in their order
        dragIds.Clear();
        dragOffset.Clear();
        liftedAlso.Clear();
        if (trackId != null)
        {
            bool group = picked.Count > 1 && picked.Contains(trackId);
            double y = 0;
            foreach (var t in seq.OfType<Track>())
            {
                if (t.Id != trackId && (!group || !picked.Contains(t.Id) || !targetTop.ContainsKey(t.Id))) continue;
                dragIds.Add(t.Id);
                dragOffset[t.Id] = y;
                y += RowH(t);
                if (t.Id != trackId) liftedAlso.Add(t.Id);
            }
        }
        liftedId = id;
        dragTop = Top(id) - BlockOffset(id);
        shownTop.Remove(id);
        drop = null;
        StartGesture("reorder", e);
        if (folder != null)
        {
            dropFrom = (seq.IndexOf(folder), null);
            dragGathers = false;
            return;
        }
        var first = seq.OfType<Track>().First(t => t.Id == dragIds[0]);
        int at = seq.IndexOf(first);
        dropFrom = (at, first.FolderId);
        // apart or from different folders, dropping them where they were still moves them together
        dragGathers = false;
        for (int k = 1; k < dragIds.Count; k++)
            if (seq[at + k] is not Track t || t.Id != dragIds[k] || t.FolderId != first.FolderId) dragGathers = true;
    }

    /// <summary>Selects a folder (no track stays selected); raises <see cref="SelectionChanged"/> with -1.</summary>
    public void SelectFolder(string id)
    {
        if (SelectedFolderId == id && Selected < 0) return;
        picked.Clear();
        boxed.Clear();
        Selected = -1;
        SelectedClipId = null;
        SelectedFolderId = id;
        SelectionChanged?.Invoke(-1);
        InvalidateVisual();
    }

    public void SelectClip(int index, string? clipId)
    {
        SelectedClipId = clipId;
        Select(index);
    }

    /// <summary>Px from a clip's edge (or a fade tab, ±half of it) that grab its handle.</summary>
    const double EdgeGrab = 9;

    ClipEditMode HitHandle(LiveClip clip, Point p)
    {
        var w = Window(clip);
        double left = X(w.Start), right = X(w.End);
        var c = DisplayClip(clip);
        var f = ClipEnvelope.FitFades(c.FadeInSec, c.FadeOutSec, w.End - w.Start);
        // the top half of an edge (or of the tab where a fade ends) drags the fade; the bottom half trims
        int lane = tracks.ToList().FindIndex(t => t.Clips.Contains(clip));
        double mid = LaneY(lane) + LaneH(lane) / 2;
        if (p.Y < mid)
        {
            double fadeOut = Math.Min(right - 5, right - f.FadeOut * pps), fadeIn = Math.Max(left + 5, left + f.FadeIn * pps);
            if (right - p.X < EdgeGrab || Math.Abs(p.X - fadeOut) <= EdgeGrab / 2) return ClipEditMode.FadeOut;
            if (p.X - left < EdgeGrab || Math.Abs(p.X - fadeIn) <= EdgeGrab / 2) return ClipEditMode.FadeIn;
            return ClipEditMode.Move;
        }
        if (p.X - left < EdgeGrab) return ClipEditMode.TrimStart;
        if (right - p.X < EdgeGrab) return ClipEditMode.TrimEnd;
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
        if (kind is "clip" or "reorder" or "scrub" or "range" or "box" or "erase" or "zoom") dragScroll.Start();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        lastPointer = e.GetPosition(this);
        modifiers = e.KeyModifiers;
        if (gesture != null) UpdateGesture();
        else
        {
            UpdateCursor();
            if (tool == TimelineTool.Split) UpdateSplitHover();
        }
        UpdateMediaTip();
        InvalidateVisual();
    }

    /// <summary>The pointer left: the header buttons stop glowing (no gesture holds it outside).</summary>
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (gesture != null) return;
        lastPointer = new Point(-1, -1);
        splitHover = null;
        UpdateMediaTip();
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
        {
            // a plain click is one jump, on press: seeking again on release (or on a pixel of jitter) sent the
            // playhead back to where it already was and crossfaded again. A real drag holds playback (see ScrubChanged)
            bool first = !scrubSought;
            if (!scrubHeld && Math.Abs(lastPointer.X - down.X) >= ScrubSlop)
            {
                scrubHeld = true;
                ScrubChanged?.Invoke(true);
            }
            if (first || scrubHeld)
            {
                scrubSought = true;
                SeekRequested?.Invoke(Math.Max(0, ClipEditing.Snap(Sec(lastPointer.X), Grid, (modifiers & KeyModifiers.Alt) != 0)));
            }
        }
        else if (gesture is "marker" or "loop")
            StripMoved();
        else if (gesture == "volume")
        {
            var r = VolumeRect(fromIndex);
            ChannelRequested?.Invoke(tracks[fromIndex], Math.Clamp((lastPointer.X - r.X) / r.Width * 1.5, 0, 1.5), null);
        }
        else if (gesture == "header")
            SetHeaderWidth(originalHeaderW + lastPointer.X - down.X);
        else if (gesture == "row-height" && heightDrag is { } hd)
        {
            // an open folder's row only holds its header; a folded one's mini map goes as tall as a track
            bool folder = folderById.TryGetValue(hd.Id, out var hf);
            double h = Math.Clamp(Math.Round((heightFrom + lastPointer.Y - down.Y) / 2) * 2,
                folder ? Folder.MinHeight : Track.MinHeight, folder && !hf!.Collapsed ? Folder.MaxHeight : Track.MaxHeight);
            if (h != hd.H)
            {
                moved = true;
                heightDrag = (hd.Id, h);
                Relayout();
            }
        }
        else if (gesture == "fx")
        {
            double dy = fxDragY - lastPointer.Y;
            fxDragY = lastPointer.Y;
            if (dy != 0)
            {
                fxDragValue = Math.Clamp(fxDragValue + dy / FxDragPx * ((modifiers & KeyModifiers.Shift) != 0 ? 0.2 : 1), 0, 1);
                EffectRequested?.Invoke(tracks[fromIndex], KnobKey(tracks[fromIndex], fxRow), false, Math.Round(fxDragValue * 1000) / 1000);
            }
        }
        else if (gesture == "pan-knob")
        {
            double pan = Math.Clamp(originalPan - (lastPointer.Y - down.Y) / 80, -1, 1);
            ChannelRequested?.Invoke(tracks[fromIndex], null, Math.Abs(pan) < 0.04 ? 0 : pan);
        }
        else if (gesture == "curve")
        {
            if (Math.Abs(dxPx) >= 2 || Math.Abs(lastPointer.Y - down.Y) >= 2) moved = true;
            if (moved) DragCurve();
        }
        else if (gesture == "range")
            DragRange();
        else if (gesture == "erase")
            DragErase();
        else if (gesture == "midi") UpdateMidiDrag();
        else if (gesture is "zoom" or "listen")
        {
            // nothing follows the pointer: Zoom acts on release (its band is drawn from the pointer), Escuchar on press
        }
        else
        {
            if (!moved && Math.Abs(dxPx) < 3 && Math.Abs(lastPointer.Y - down.Y) < 3) return;
            bool wasMoved = moved;
            moved = true;
            if (gesture == "box") UpdateBox();
            else if (gesture == "reorder")
            {
                var d = FindDrop();
                // a lifted folder folds its children away; then each new place makes room again
                if (!wasMoved || d != drop)
                {
                    // the rows that make room glide from where they are drawn now
                    // (and on lifting, the other carried tracks glide from there to their place under the grabbed one)
                    foreach (var r in rows)
                    {
                        string id = RowIdOf(r);
                        if (id == liftedId || IsDragged(id) && wasMoved) continue;
                        shownTop.TryAdd(id, shownTop.TryGetValue(id, out double s) ? s : r.Top);
                    }
                    drop = d;
                    Relayout();
                    slide.Start();
                }
            }
            else if (dragged != null && original != null && carriedAlong.Count > 0)
            {
                // the boxed clips move in time together, each on its own track (no swapping, no landing elsewhere)
                overIndex = fromIndex;
                preview = ClipEditing.Drag(original, dragged.Media, dragMode, dxPx / pps, Grid, (modifiers & KeyModifiers.Alt) != 0, out stuckTo, dragged.LengthSec,
                    SnapToClips != SnapEdges.None ? tracks.SelectMany(t => t.Clips).Where(c => c != dragged && !boxed.Contains(c.Clip.Id)).Select(c => Window(c)) : null, SnapToClips, pps);
                double at = preview.AtSec;
                DragCarried();
                // a carried clip that would cross zero holds them all back, off the edge they stuck to
                if (preview.AtSec != at) stuckTo = null;
            }
            else if (dragged != null && original != null)
            {
                int lane = HitTrack(lastPointer.Y);
                overIndex = dragMode == ClipEditMode.Move && lane >= 0 && lane < tracks.Count ? lane : fromIndex;
                // Ctrl over another track swaps with the clip under the pointer, as in the web (the time stays); without
                // it the clip lands on that track at the time under the pointer, shown by a ghost (native only).
                // a take dropped on a MIDI track gives its notes there ("Extraer MIDI"): nothing to swap with
                swapping = overIndex != fromIndex && (modifiers & KeyModifiers.Control) != 0 && !tracks[overIndex].Track.IsMidi;
                swapId = swapping ? ClipAt(overIndex, lastPointer.X)?.Clip.Id : null;
                stuckTo = null;
                if (swapping) preview = ClipEditing.Copy(original);
                else preview = ClipEditing.Drag(original, dragged.Media, dragMode, dxPx / pps, Grid, (modifiers & KeyModifiers.Alt) != 0, out stuckTo, dragged.LengthSec,
                    SnapToClips != SnapEdges.None ? tracks.SelectMany(t => t.Clips).Where(c => c != dragged).Select(c => Window(c)) : null, SnapToClips, pps);
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
        var extractTo = target != null && tracks[to].Track.IsMidi && dragged != null ? (Clip: dragged, Track: tracks[to]) : default;
        string? swap = swapId;
        bool changed = moved;
        var landed = drop;
        var from0 = dropFrom;
        var movedFolder = dragFolder;
        var movedTracks = dragIds.ToList();
        bool gathers = dragGathers;
        List<string> erased = [.. erasing];
        bool midiCarry = kind == "midi" && midiCarrying;
        List<Clip>? together = kind == "clip" && (carriedAlong.Count > 0 || midiCarried.Count > 0) && value != null ? [value, .. carriedPreview.Values]
            : midiCarry ? [.. carriedPreview.Values] : null;
        var midiMoves = together != null ? MidiMoves() : [];
        string? grabbedId = kind == "midi" ? midiHeld?.Id : dragged?.Clip.Id;
        var midi = kind == "midi" && !midiCarry ? MidiResult() : null;
        var toggle = toggleBase;
        // the new height is applied while the dragged one still holds: applied after, the rows below went back to the
        // old height for a moment and glided from there, so everything jumped on release
        if (kind == "row-height" && changed && heightDrag is { } rh) RowHeightRequested?.Invoke(rh.Id, rh.H);
        ClearGesture();
        e.Pointer.Capture(null);
        if (kind == "range" && Range == null) ClearRange();
        if (kind == "erase" && erased.Count > 0) ClipsErased?.Invoke(erased);
        if (kind == "zoom") EndZoom(lastPointer, (modifiers & KeyModifiers.Alt) != 0);
        if (kind is "marker" or "loop") StripReleased(kind, changed);
        if (kind == "box" && changed) EndBox();
        // Ctrl+click on a take adds it or takes it out (with Ctrl held, a drag still swaps it with another track's clip)
        if (toggle != null && !changed && grabbedId != null) ToggleBoxed(grabbedId, toggle);
        // a click on a boxed clip without moving it leaves just that one selected
        else if (together != null && !changed)
        {
            if (kind == "midi") SelectMidiClip(from, grabbedId);
            else SelectClip(from, grabbedId);
        }
        else if (together != null) ClipsMoved?.Invoke(together, midiMoves);
        else if (extractTo.Clip != null) ExtractMidiRequested?.Invoke(extractTo.Clip, extractTo.Track);
        else if (kind == "clip" && changed && value != null) ClipEditCommitted?.Invoke(value, target, swap);
        if (kind == "curve" && changed && value != null) ClipEditCommitted?.Invoke(value, null, null);
        if (kind == "midi") ReleaseMidi(midi);
        if (kind == "reorder" && changed && landed is { } l && (l != from0 || gathers))
        {
            if (movedFolder != null) FolderMoveRequested?.Invoke(movedFolder.Id, l.Pos);
            else if (movedTracks.Count > 0) TrackMoveRequested?.Invoke(movedTracks, l.Pos, l.Folder);
        }
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
        if (gesture == "listen") ListenEnded?.Invoke();
        if (erasing.Count > 0) erasing.Clear();
        bool reordered = gesture == "reorder" && moved;
        if (reordered)
        {
            // keep every row where it is drawn; once the window applies the new order they glide to their places
            foreach (var r in rows) { string id = RowIdOf(r); shownTop[id] = Top(id); }
            slide.Start();
        }
        else if (gesture == "reorder")
        {
            liftedId = null;
            liftedAlso.Clear();
        }
        gesture = null;
        dragFolder = null;
        dragTrackId = null;
        dragIds.Clear();
        drop = null;
        bool resized = heightDrag != null;
        heightDrag = null;
        if (reordered || resized) Relayout();
        scrubSought = false;
        if (scrubHeld)
        {
            scrubHeld = false;
            ScrubChanged?.Invoke(false);
        }
        var pointer = capturedPointer;
        capturedPointer = null;
        dragged = null;
        original = preview = null;
        toggleBase = null;
        ClearMidiGesture();
        carriedAlong.Clear();
        carriedPreview.Clear();
        stuckTo = null;
        curvePoint = -1;
        swapId = null;
        swapping = false;
        fromIndex = overIndex = -1;
        dragScroll.Stop();
        pointer?.Capture(null);
        Cursor = ToolCursor() ?? CursorOf(StandardCursorType.Arrow);
        InvalidateVisual();
    }

    /// <summary>The cursor says what a press does where the pointer is (the timeline has no hover help: it got in the way).</summary>
    void UpdateCursor()
    {
        var row = HitRow(lastPointer.Y);
        int i = row is { Folder: null } h ? h.Track : -1;
        if (ToolCursor() is { } toolCursor) { Cursor = toolCursor; return; }
        var cursor = StandardCursorType.Arrow;
        if (InStrip(lastPointer)) cursor = StripCursor(lastPointer);
        else if (OnGrip(lastPointer)) cursor = StandardCursorType.SizeWestEast;
        else if (AddRect.Contains(lastPointer)) cursor = StandardCursorType.Hand;
        else if (RowEdgeAt(lastPointer) != null) cursor = StandardCursorType.SizeNorthSouth;
        else if (row?.Folder is { } fo)
        {
            if (lastPointer.X < 20 || (lastPointer.X < HeaderW && (FoldRect(fo).Contains(lastPointer) || FolderIconRect(fo).Contains(lastPointer))))
                cursor = StandardCursorType.Hand;
        }
        else if (MidiCursor(i, lastPointer) is { } midiCursor) cursor = midiCursor;
        else if (i >= 0 && i < tracks.Count)
        {
            if (OnHandle(i, lastPointer) || BadgeRect(i).Contains(lastPointer) || JoinChipAt(i, lastPointer) != null
                || tracks[i].Track.IsMidi && lastPointer.X < HeaderW && InstrumentRect(i).Contains(lastPointer)) cursor = StandardCursorType.Hand;
            else if (lastPointer.X < HeaderW && FxHit(i, lastPointer) is { } fx)
                cursor = fx.Dial ? StandardCursorType.SizeNorthSouth : StandardCursorType.Hand;
            else if (lastPointer.X >= HeaderW && ClipAt(i, lastPointer.X) is { } clip)
            {
                var mode = HitHandle(clip, lastPointer);
                if (mode is ClipEditMode.TrimStart or ClipEditMode.TrimEnd && ClipEditing.IsLocked(tracks[i].Track, clip.Clip)) mode = ClipEditMode.Move;
                cursor = mode switch
                {
                    ClipEditMode.Move when CurvePointAt(i, clip, lastPointer) >= 0 => StandardCursorType.SizeAll,
                    ClipEditMode.Move when OnCurve(i, clip, lastPointer) => StandardCursorType.Cross,
                    // the diagonal follows the fade line: / for a fade in, \ for a fade out
                    ClipEditMode.FadeIn => StandardCursorType.TopRightCorner, ClipEditMode.FadeOut => StandardCursorType.TopLeftCorner,
                    ClipEditMode.TrimStart => StandardCursorType.LeftSide, ClipEditMode.TrimEnd => StandardCursorType.RightSide,
                    _ => StandardCursorType.Hand,
                };
            }
        }
        Cursor = CursorOf(cursor);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var p = e.GetPosition(this);
        int i = HitTrack(p.Y);
        if (p.Y >= RulerH && i >= 0 && i < tracks.Count && LaneH(i) >= ControlsAt && PanRect(i).Contains(p))
        {
            double pan = Math.Clamp(tracks[i].Channel.Pan + e.Delta.Y * ((e.KeyModifiers & KeyModifiers.Shift) != 0 ? 0.01 : 0.05), -1, 1);
            ChannelRequested?.Invoke(tracks[i], null, Math.Abs(pan) < 0.04 ? 0 : pan);
        }
        else if (p.Y >= RulerH && i >= 0 && i < tracks.Count && p.X < HeaderW && FxHit(i, p) is { Dial: true } fx)
        {
            // like the mixer's knobs: 50 steps over the range, Shift = fine
            // Transponer: a semitone per notch
            double amount = FxState(tracks[i], fx.Knob).Amount + Math.Sign(e.Delta.Y) * ((e.KeyModifiers & KeyModifiers.Shift) != 0 ? 0.004 : 0.02);
            EffectRequested?.Invoke(tracks[i], KnobKey(tracks[i], fx.Knob), false, Math.Round(Math.Clamp(amount, 0, 1) * 1000) / 1000);
        }
        // plain wheel: up and down; the modifiers for zoom, sideways and track height are the user's (Shortcuts.Wheel:
        // Shift, Ctrl and Alt by default), and so is each one's direction (Shortcuts.WheelSign); a sideways wheel or
        // touchpad swipe always goes sideways
        else if (Shortcuts.FindWheel(e.KeyModifiers) is { } wheel)
        {
            // some mice and drivers turn a modified wheel into a sideways delta, so take whichever axis moved
            double d = (e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X) * Shortcuts.WheelSign(wheel);
            if (wheel == "wheelZoom") ZoomTo(pps * Math.Pow(1.18, d));
            else if (wheel == "wheelHeight") SetTrackHeight(laneH + d * 6);
            // wheel up goes right, toward later in the song (reversed: down)
            else scrollX = Math.Max(0, scrollX + (e.Delta.Y + e.Delta.X) * Shortcuts.WheelSign(wheel) * 80);
        }
        else
        {
            scrollY -= e.Delta.Y * Shortcuts.WheelSign(Shortcuts.WheelScroll) * 60;
            ClampScroll();
            if (e.Delta.X != 0) scrollX = Math.Max(0, scrollX - e.Delta.X * 80);
        }
        e.Handled = true;
        InvalidateVisual();
    }
}

/// <summary>A send shown as a knob in a track's header: the bus, its name (the knob's label) and its <see cref="FxIcon"/> icon.</summary>
public readonly record struct SendKnob(string BusId, string Name, string Icon);
