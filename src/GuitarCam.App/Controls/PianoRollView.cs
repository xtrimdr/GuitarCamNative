using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using GuitarCam.Projects;

namespace GuitarCam.App.Controls;

/// <summary>
/// The piano roll (native only, stage 1 of the MIDI editor, asked by the user "in the purest FL Studio style"): the notes
/// of one MIDI clip on a grid of pitches (rows, a keyboard on the left) and beats (columns, the timeline's bars on the ruler),
/// with each note's velocity as a bar in the lane at the bottom. Drawn by hand like the timeline.
/// <para>
/// Mouse, as in FL: a click on an empty cell writes a note there with the length of the last one touched (and drags
/// it), a note's body moves it (with the others selected; Alt, free in time), its right edge resizes it, the right button
/// erases what it touches, Ctrl+drag on empty space selects with a box, a key of the keyboard plays its note and the
/// ruler moves the playhead. The keys (Supr, Ctrl+A, arrows) come from the window (<see cref="HandleKey"/>).
/// </para>
/// <para>
/// The velocity lane grows or shrinks from the bar above it (double click: back to its height), and dragging a bar of
/// a selected note sets every selected note that starts there, a chord at once (both asked by the user).
/// </para>
/// <para>
/// The grid's lines follow the magnet or go their own way (<see cref="GridMode"/>, asked by the user, like the
/// timeline's): the magnet's steps, Auto (finer as it zooms in) or a fixed subdivision. The right button on the ruler
/// asks the window for that menu.
/// </para>
/// <para>
/// It shows the whole track on the timeline's own bars (asked by the user): the clip being edited is lit, the track's
/// other clips are marked on the ruler and in the grid with their notes faint, and a click inside one of them makes it
/// the one being edited (<see cref="ClipPicked"/>) and goes on as a click there. The grid and the magnet count from the
/// timeline's start, so they line up with the timeline's.
/// </para>
/// It edits a working copy of the clip's notes and hands the whole list to the window when a gesture ends
/// (<see cref="NotesCommitted"/>), which makes it one undo step; the control never touches the project or the engine.
/// </summary>
public sealed class PianoRollView : Control
{
    /// <summary>
    /// How <see cref="SetClip"/> frames the view: as it was, the clip filling the width, the clip scrolled into sight
    /// (the zoom kept), or the whole track with the playhead in sight.
    /// </summary>
    public enum Frame { Keep, Clip, Reveal, Track }

    const double KeyW = 56, RulerH = 24, VelGap = 8;
    /// <summary>The velocity lane's height range and default; the grid always keeps <see cref="MinGridH"/>.</summary>
    public const double MinVelH = 40, DefaultVelH = 64;
    const double MinGridH = 60;
    /// <summary>Rows' height range and default (px per semitone).</summary>
    const double MinRowH = 7, MaxRowH = 28, DefaultRowH = 14;
    /// <summary>Zoom range in px per beat.</summary>
    const double MinBeatW = 6, MaxBeatW = 480;
    /// <summary>Px from a note's right end that grab it to resize it.</summary>
    const double EdgeGrab = 6;
    /// <summary>Lines closer than this are not drawn; Auto picks the finest step at least <see cref="AutoGridPx"/> apart.</summary>
    const double MinGridPx = 6, AutoGridPx = 14;
    /// <summary>The whole track is fitted only down to this zoom (a bar = 48 px); a longer one is shown around the playhead.</summary>
    const double MinFitBeatW = 12;
    /// <summary>Room left before the first clip (or the playhead's margin) when the view is framed.</summary>
    const double FrameMargin = 12;
    const int Pitches = 128;

    static readonly string[] PitchNames = [.. Enumerable.Range(0, Pitches).Select(MidiNote.Name)];

    string? clipId;
    string? title;
    double clipBeats = MidiClip.DefaultBeats;
    double clipAtSec;
    Color color;
    readonly List<MidiNote> notes = [];
    readonly HashSet<MidiNote> selected = [];
    /// <summary>The track's other clips (the project's, only read to draw them and to pick one).</summary>
    readonly List<MidiClip> others = [];

    double rowH = DefaultRowH, beatW = 40, scrollX, scrollY;
    double velH = DefaultVelH;
    /// <summary>The framing <see cref="SetClip"/> asked for, done on the next render (when the size is known).</summary>
    Frame pending = Frame.Clip;
    double bpm = 120;
    double playheadSec = -1;

    double snapBeats = 0.25;
    string gridMode = "snap";

    /// <summary>The magnet in beats (0 = free): the window's selector sets it.</summary>
    public double SnapBeats { get => snapBeats; set { snapBeats = value; InvalidateVisual(); } }

    /// <summary>
    /// How the grid's lines are spaced: "snap" (the magnet's steps; Auto while it is free), "auto" (by zoom) or one of
    /// the timeline's subdivisions ("bar", "1/4", "1/8T"…).
    /// </summary>
    public string GridMode { get => gridMode; set { gridMode = value; InvalidateVisual(); } }
    /// <summary>The length and velocity the next written note gets: the last note touched.</summary>
    double lastBeats = 1;
    int lastVelocity = MidiNote.DefaultVelocity;

    /// <summary>The clip's id and its notes after an edit (one undo step).</summary>
    public event Action<string, List<MidiNote>>? NotesCommitted;
    /// <summary>A note to hear on its own: pitch, velocity.</summary>
    public event Action<int, int>? NoteAuditioned;
    /// <summary>A click on the ruler: the timeline time to move the playhead to.</summary>
    public event Action<double>? SeekRequested;
    /// <summary>The right button on the ruler: the grid's menu.</summary>
    public event Action? GridMenuRequested;
    /// <summary>The velocity lane was resized (its new height), for the window to remember.</summary>
    public event Action<double>? VelocityHeightChanged;
    /// <summary>A click inside another clip of the track: the window shows that one (<see cref="SetClip"/>) before the click goes on.</summary>
    public event Action<string>? ClipPicked;

    /// <summary>The velocity lane's height (the window restores it from config.json).</summary>
    public double VelocityHeight
    {
        get => velH;
        set { velH = Math.Max(MinVelH, value); ClampScroll(); InvalidateVisual(); }
    }

    /// <summary>The lane as drawn: never so tall that the grid gets under <see cref="MinGridH"/>.</summary>
    double VelH => Math.Max(MinVelH, Math.Min(velH, Bounds.Height - RulerH - VelGap - MinGridH));

    public string? ClipId => clipId;

    public PianoRollView()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    /// <summary>
    /// Shows a clip (null = none) with the track's clips around it (<paramref name="trackClips"/>, the clip among them).
    /// The same clip again (after an edit or an undo) keeps the selection where it can; <paramref name="frame"/> says how
    /// the view moves (a view never framed yet fits the clip).
    /// </summary>
    public void SetClip(MidiClip? clip, IEnumerable<MidiClip>? trackClips, string? trackName, Color? trackColor, double tempo, Frame frame = Frame.Keep)
    {
        bool same = clip != null && clip.Id == clipId;
        var keep = same ? selected.Select(n => (n.Pitch, n.Beat, n.Beats)).ToHashSet() : [];
        CancelGesture();
        clipId = clip?.Id;
        bpm = tempo;
        color = trackColor ?? Palette.AccentColor;
        notes.Clear();
        selected.Clear();
        others.Clear();
        if (clip != null)
        {
            clipBeats = clip.LengthBeats;
            clipAtSec = clip.AtSec;
            title = trackName;
            foreach (var n in clip.Notes)
            {
                var copy = n.Copy();
                notes.Add(copy);
                if (keep.Contains((n.Pitch, n.Beat, n.Beats))) selected.Add(copy);
            }
            if (trackClips != null) others.AddRange(trackClips.Where(c => c.Id != clip.Id));
        }
        if (frame != Frame.Keep) pending = frame;
        InvalidateVisual();
    }

    /// <summary>The playhead in timeline seconds; redraws only when it moved inside (or across) the view.</summary>
    public void SetPlayhead(double sec)
    {
        // kept while hidden too: framing the whole track looks for it
        if (clipId == null || !IsVisible) { playheadSec = sec; return; }
        double W = Bounds.Width;
        double x0 = XA(playheadSec * bpm / 60), x1 = XA(sec * bpm / 60);
        bool was = playheadSec >= 0 && x0 >= KeyW - 2 && x0 <= W + 2, now = x1 >= KeyW - 2 && x1 <= W + 2;
        if (sec == playheadSec || !was && !now) { playheadSec = sec; return; }
        playheadSec = sec;
        InvalidateVisual();
    }

    // --- geometry ---------------------------------------------------------------------------------

    double GridBottom => Math.Max(RulerH, Bounds.Height - VelH - VelGap);
    /// <summary>On the bar between the grid and the velocity lane, which resizes the lane.</summary>
    bool OnVelSplit(Point p) => p.Y >= GridBottom - 2 && p.Y <= GridBottom + VelGap + 2;
    /// <summary>Where the clip starts, in beats from the timeline's start (the view's own coordinate).</summary>
    double Origin => clipAtSec * bpm / 60;
    static double StartBeat(MidiClip c, double bpm) => c.AtSec * bpm / 60;
    /// <summary>X of a beat from the timeline's start (<see cref="XA"/>) or from the clip's (<see cref="X"/>, its notes).</summary>
    double XA(double beat) => KeyW + beat * beatW - scrollX;
    double X(double beat) => XA(Origin + beat);
    double BeatA(double x) => (x - KeyW + scrollX) / beatW;
    double Beat(double x) => BeatA(x) - Origin;
    double Y(int pitch) => RulerH + (Pitches - 1 - pitch) * rowH - scrollY;
    int PitchAt(double y) => Math.Clamp(Pitches - 1 - (int)Math.Floor((y + scrollY - RulerH) / rowH), 0, Pitches - 1);
    Rect NoteRect(MidiNote n) => new(X(n.Beat), Y(n.Pitch) + 1, Math.Max(3, n.Beats * beatW - 1), rowH - 2);
    double ContentH => Pitches * rowH;
    /// <summary>Up to the last clip's end, with two bars to spare.</summary>
    double ContentW
    {
        get
        {
            double end = Origin + clipBeats;
            foreach (var c in others) end = Math.Max(end, StartBeat(c, bpm) + c.LengthBeats);
            return Math.Max(end + 8, 16) * beatW;
        }
    }

    void ClampScroll()
    {
        scrollY = Math.Clamp(scrollY, 0, Math.Max(0, ContentH - (GridBottom - RulerH)));
        scrollX = Math.Clamp(scrollX, 0, Math.Max(0, ContentW - (Bounds.Width - KeyW)));
    }

    /// <summary>
    /// Does the framing <see cref="SetClip"/> asked for, once the size is known. Clip: it fills the width. Reveal: the
    /// zoom stays and the clip is scrolled into sight. Track: every clip fits the width, or, if that would be too small
    /// to work in, the playhead is in the middle. The notes shown (or C4) sit in the middle of the height.
    /// </summary>
    void Fit()
    {
        if (pending == Frame.Keep || Bounds.Width <= KeyW || clipId == null) return;
        var frame = pending;
        pending = Frame.Keep;
        double w = Bounds.Width - KeyW, origin = Origin;
        var pitches = notes.Select(n => n.Pitch);
        switch (frame)
        {
            case Frame.Clip:
                beatW = Math.Clamp((w - 2 * FrameMargin) / Math.Max(1, clipBeats), MinBeatW, MaxBeatW);
                scrollX = origin * beatW - FrameMargin;
                break;
            case Frame.Reveal:
                // only what is out of sight moves: its start, its end if it fits, its notes if none shows
                if (XA(origin) < KeyW || XA(origin) > Bounds.Width - FrameMargin || X(clipBeats) > Bounds.Width && clipBeats * beatW <= w)
                    scrollX = origin * beatW - FrameMargin;
                if (notes.Any(n => Y(n.Pitch) >= RulerH && Y(n.Pitch) + rowH <= GridBottom)) pitches = [];
                break;
            case Frame.Track:
            {
                double start = origin, end = origin + clipBeats;
                foreach (var c in others)
                {
                    start = Math.Min(start, StartBeat(c, bpm));
                    end = Math.Max(end, StartBeat(c, bpm) + c.LengthBeats);
                }
                double fit = (w - 2 * FrameMargin) / Math.Max(1, end - start);
                beatW = Math.Clamp(fit, MinFitBeatW, MaxBeatW);
                double play = playheadSec * bpm / 60;
                scrollX = fit >= MinFitBeatW ? start * beatW - FrameMargin
                    : (play >= start && play <= end ? play : origin) * beatW - w / 2;
                pitches = pitches.Concat(others.SelectMany(c => c.Notes.Select(n => n.Pitch)));
                break;
            }
        }
        if (pitches.Any() || frame != Frame.Reveal)
        {
            double mid = pitches.Any() ? (pitches.Min() + pitches.Max()) / 2.0 : 60;
            scrollY = (Pitches - 1 - mid) * rowH - (GridBottom - RulerH) / 2;
        }
        ClampScroll();
    }

    /// <summary>A beat from the clip's start to the magnet, whose steps count from the timeline's start.</summary>
    double Snap(double beat, bool floor = false) => SnapA(Origin + beat, floor) - Origin;

    double SnapA(double beat, bool floor = false)
    {
        if (SnapBeats <= 0 || (modifiers & KeyModifiers.Alt) != 0) return Math.Max(0, beat);
        double k = beat / SnapBeats;
        return Math.Max(0, (floor ? Math.Floor(k) : Math.Round(k)) * SnapBeats);
    }

    /// <summary>The track's other clip under a timeline beat (the last one drawn wins), or null.</summary>
    MidiClip? OtherAt(double beatA)
    {
        for (int i = others.Count - 1; i >= 0; i--)
        {
            double s = StartBeat(others[i], bpm);
            if (beatA >= s && beatA < s + others[i].LengthBeats) return others[i];
        }
        return null;
    }

    MidiNote? NoteAt(Point p)
    {
        if (p.Y < RulerH || p.Y >= GridBottom || p.X < KeyW) return null;
        for (int i = notes.Count - 1; i >= 0; i--)
            if (NoteRect(notes[i]).Inflate(new Thickness(0, 0, 2, 0)).Contains(p)) return notes[i];
        return null;
    }

    bool OnEdge(MidiNote n, Point p) => p.X >= NoteRect(n).Right - EdgeGrab;

    // --- drawing ----------------------------------------------------------------------------------

    public override void Render(DrawingContext ctx)
    {
        double W = Bounds.Width, H = Bounds.Height;
        ctx.FillRectangle(Palette.App, new Rect(0, 0, W, H));
        if (clipId == null)
        {
            ctx.DrawText(Loc.T("piano.empty.title"), 12.5, Palette.Dim, new Point(24, 28), Palette.UiBold);
            ctx.DrawText(Loc.T("piano.empty.hint"), 12, Palette.Faint, new Point(24, 52));
            return;
        }
        Fit();
        double gb = GridBottom;
        var line = Palette.Pen(Palette.Line);

        using (ctx.PushClip(new Rect(KeyW, RulerH, Math.Max(0, W - KeyW), Math.Max(0, gb - RulerH))))
        {
            // rows: the black keys' rows darker, a line under every B (between octaves)
            for (int p = 0; p < Pitches; p++)
            {
                double y = Y(p);
                if (y + rowH < RulerH || y > gb) continue;
                if (MidiNote.IsBlack(p)) ctx.FillRectangle(Palette.Panel, new Rect(KeyW, y, W - KeyW, rowH));
                if (p % 12 == 0) ctx.DrawLine(line, new Point(KeyW, y + rowH - 0.5), new Point(W, y + rowH - 0.5));
            }
            DrawColumns(ctx, RulerH, gb, W);
            DrawRegions(ctx, gb, W);
            foreach (var c in others) DrawOtherNotes(ctx, c, W, gb);
            foreach (var n in notes) DrawNote(ctx, n, W, gb);
            if (gesture == "box" && moved)
                ctx.DrawRectangle(Palette.Alpha(Palette.AccentColor, 0.08), Palette.Pen(Palette.Alpha(Palette.AccentColor, 0.7)), BoxRect());
            DrawPlayhead(ctx, RulerH, gb);
        }

        DrawVelocity(ctx, gb + VelGap, W, H);
        DrawKeys(ctx, gb);
        DrawRuler(ctx, W);
        ctx.FillRectangle(Palette.Panel, new Rect(0, 0, KeyW, RulerH));
        ctx.DrawLine(line, new Point(KeyW - 0.5, 0), new Point(KeyW - 0.5, H));
        ctx.DrawLine(line, new Point(0, gb + 0.5), new Point(W, gb + 0.5));
    }

    /// <summary>Bars strong, beats softer, the magnet's steps faint (only when they are at least 6 px apart).</summary>
    void DrawColumns(DrawingContext ctx, double top, double bottom, double W)
    {
        var bar = Palette.Pen(Palette.Alpha(Palette.LineColor, 1));
        var beat = Palette.Pen(Palette.Alpha(Palette.LineColor, 0.55));
        var step = Palette.Pen(Palette.Alpha(Palette.LineColor, 0.25));
        double unit = GridUnit();
        if (unit * beatW < MinGridPx) unit = beatW >= MinGridPx ? 1 : 4;
        double first = Math.Floor(BeatA(KeyW) / unit) * unit;
        for (double b = Math.Max(0, first); XA(b) <= W; b += unit)
        {
            double x = Math.Round(XA(b)) + 0.5;
            if (x < KeyW) continue;
            bool isBar = Math.Abs(b / 4 - Math.Round(b / 4)) < 1e-9, isBeat = Math.Abs(b - Math.Round(b)) < 1e-9;
            ctx.DrawLine(isBar ? bar : isBeat ? beat : step, new Point(x, top), new Point(x, bottom));
        }
    }

    /// <summary>The grid's step in beats (<see cref="GridMode"/>).</summary>
    double GridUnit()
    {
        double unit = gridMode switch { "auto" => 0, "snap" => snapBeats, _ => ClipEditing.GridSeconds(gridMode, 60) };
        if (unit > 0) return unit;
        // Auto: from a bar down to a 1/64, the finest step that still leaves room between the lines
        unit = 4;
        while (unit > 1.0 / 16 && unit / 2 * beatW >= AutoGridPx) unit /= 2;
        return unit;
    }

    /// <summary>
    /// Where the track's clips are: outside the one being edited the grid is darker (nothing of it is heard there),
    /// a bit less inside the other clips, and every clip's start and end is a line in the track's color.
    /// </summary>
    void DrawRegions(DrawingContext ctx, double gb, double W)
    {
        double h = gb - RulerH;
        double x0 = Math.Max(KeyW, X(0)), x1 = Math.Min(W, X(clipBeats));
        var dark = Palette.Alpha(Palette.AppColor, 0.6);
        if (x0 > KeyW) ctx.FillRectangle(dark, new Rect(KeyW, RulerH, x0 - KeyW, h));
        if (x1 < W) ctx.FillRectangle(dark, new Rect(Math.Max(KeyW, x1), RulerH, W - Math.Max(KeyW, x1), h));
        var tint = Palette.Alpha(color, 0.06);
        var otherEdge = Palette.Pen(Palette.Alpha(color, 0.4));
        foreach (var c in others)
        {
            double s = XA(StartBeat(c, bpm)), e = XA(StartBeat(c, bpm) + c.LengthBeats);
            if (e < KeyW || s > W) continue;
            double l = Math.Max(KeyW, s), r = Math.Min(W, e);
            if (r > l) ctx.FillRectangle(tint, new Rect(l, RulerH, r - l, h));
            DrawEdge(ctx, otherEdge, s, gb, W);
            DrawEdge(ctx, otherEdge, e, gb, W);
        }
        var edge = Palette.Pen(Palette.Alpha(color, 0.85));
        DrawEdge(ctx, edge, X(0), gb, W);
        DrawEdge(ctx, edge, X(clipBeats), gb, W);
    }

    static void DrawEdge(DrawingContext ctx, IPen pen, double x, double gb, double W)
    {
        if (x < KeyW || x > W) return;
        x = Math.Round(x) + 0.5;
        ctx.DrawLine(pen, new Point(x, RulerH), new Point(x, gb));
    }

    /// <summary>Corners of the notes (asked by the user: a bit rounded), never more than half the note.</summary>
    static double NoteRadius(Rect r) => Math.Min(4, Math.Min(r.Width, r.Height) / 2);

    void DrawNote(DrawingContext ctx, MidiNote n, double W, double gb)
    {
        var r = NoteRect(n);
        if (r.Right < KeyW || r.X > W || r.Bottom < RulerH || r.Y > gb) return;
        bool sel = selected.Contains(n);
        bool past = n.Beat >= clipBeats;
        double a = (0.55 + 0.45 * n.Velocity / 127.0) * (past ? 0.35 : 1);
        double rad = NoteRadius(r);
        ctx.DrawRectangle(Palette.Alpha(color, a), sel ? Palette.Pen(Palette.Fg, 1.5) : Palette.Pen(Palette.Alpha(Palette.AppColor, 0.6)), r, rad, rad);
        // the velocity at a glance (asked by the user): a gauge along the bottom, full at 127
        if (r.Width >= 10 && r.Height >= 8)
        {
            double gw = r.Width - 2 * rad, gy = r.Bottom - 3;
            ctx.FillRectangle(Palette.Alpha(Palette.AppColor, 0.18), new Rect(r.X + rad, gy, gw, 2));
            ctx.FillRectangle(Palette.Alpha(Palette.AppColor, 0.6), new Rect(r.X + rad, gy, gw * n.Velocity / 127.0, 2));
        }
        if (r.Width >= 26 && rowH >= 11)
            ctx.DrawText(PitchNames[n.Pitch], 9.5, Palette.AccentFg, new Point(r.X + 3, r.Y + (r.Height - 15) / 2), Palette.UiBold);
    }

    /// <summary>A note of another clip of the track: only there to see, faint and without its name.</summary>
    void DrawOtherNotes(DrawingContext ctx, MidiClip c, double W, double gb)
    {
        double start = StartBeat(c, bpm);
        if (XA(start + c.LengthBeats) < KeyW || XA(start) > W) return;
        var fill = Palette.Alpha(color, 0.28);
        foreach (var n in c.Notes)
        {
            if (n.Beat >= c.LengthBeats) continue;
            var r = new Rect(XA(start + n.Beat), Y(n.Pitch) + 1, Math.Max(3, Math.Min(n.Beats, c.LengthBeats - n.Beat) * beatW - 1), rowH - 2);
            if (r.Right < KeyW || r.X > W || r.Bottom < RulerH || r.Y > gb) continue;
            double rad = NoteRadius(r);
            ctx.DrawRectangle(fill, null, r, rad, rad);
        }
    }

    void DrawPlayhead(DrawingContext ctx, double top, double bottom)
    {
        if (playheadSec < 0) return;
        double x = Math.Round(XA(playheadSec * bpm / 60)) + 0.5;
        if (x < KeyW || x > Bounds.Width) return;
        ctx.DrawLine(Palette.Pen(Palette.Accent), new Point(x, top), new Point(x, bottom));
    }

    void DrawKeys(DrawingContext ctx, double gb)
    {
        using var clip = ctx.PushClip(new Rect(0, RulerH, KeyW, Math.Max(0, gb - RulerH)));
        ctx.FillRectangle(Palette.Fg, new Rect(0, RulerH, KeyW, gb - RulerH));
        var line = Palette.Pen(Palette.Alpha(Palette.AppColor, 0.35));
        for (int p = 0; p < Pitches; p++)
        {
            double y = Y(p);
            if (y + rowH < RulerH || y > gb) continue;
            bool lit = p == heardPitch;
            if (MidiNote.IsBlack(p)) ctx.FillRectangle(lit ? Palette.Accent : Palette.App, new Rect(0, y, KeyW * 0.62, rowH));
            else
            {
                if (lit) ctx.FillRectangle(Palette.Accent, new Rect(0, y, KeyW, rowH));
                // the line between two white keys (E–F and B–C have no black key between them)
                if (!MidiNote.IsBlack(p + 1)) ctx.DrawLine(line, new Point(0, y + 0.5), new Point(KeyW, y + 0.5));
            }
            if (p % 12 == 0 && rowH >= 9)
                ctx.DrawText(PitchNames[p], 9.5, Palette.App, new Point(KeyW - 24, y + (rowH - 12) / 2), Palette.Ui);
        }
    }

    void DrawRuler(DrawingContext ctx, double W)
    {
        ctx.FillRectangle(Palette.Panel, new Rect(KeyW, 0, W - KeyW, RulerH));
        using var clip = ctx.PushClip(new Rect(KeyW, 0, Math.Max(0, W - KeyW), RulerH));
        // the track's clips as bands, like the timeline's boxes: the one being edited lit
        foreach (var c in others) DrawBand(ctx, StartBeat(c, bpm), c.LengthBeats, 0.08, 0.4, W);
        DrawBand(ctx, Origin, clipBeats, 0.16, 0.9, W);
        var tick = Palette.Pen(Palette.Alpha(Palette.LineColor, 1));
        // the timeline's bars: a number every bar, or every few when they get close
        int every = beatW * 4 >= 40 ? 1 : beatW * 4 >= 16 ? 4 : 16;
        int firstBar = Math.Max(0, (int)Math.Floor(BeatA(KeyW) / 4));
        double origin = Origin;
        for (int b = firstBar; XA(b * 4) <= W; b++)
        {
            double x = Math.Round(XA(b * 4)) + 0.5;
            ctx.DrawLine(tick, new Point(x, RulerH - 8), new Point(x, RulerH - 4));
            bool inside = b * 4 >= origin - 1e-9 && b * 4 < origin + clipBeats;
            if (b % every == 0) ctx.DrawText(BarLabel(b + 1), 10, inside ? Palette.Dim : Palette.Faint, new Point(x + 3, 4), Palette.Mono);
        }
        ctx.DrawLine(Palette.Pen(Palette.Line), new Point(KeyW, RulerH - 0.5), new Point(W, RulerH - 0.5));
        if (playheadSec >= 0)
        {
            double x = XA(playheadSec * bpm / 60);
            if (x >= KeyW && x <= W) ctx.DrawLine(Palette.Pen(Palette.Accent, 2), new Point(x, 0), new Point(x, RulerH));
        }
    }

    /// <summary>A clip on the ruler: a tint over its stretch and a strip in the track's color along the bottom.</summary>
    void DrawBand(DrawingContext ctx, double start, double beats, double tint, double strip, double W)
    {
        double l = Math.Max(KeyW, XA(start)), r = Math.Min(W, XA(start + beats));
        if (r <= l) return;
        ctx.FillRectangle(Palette.Alpha(color, tint), new Rect(l, 0, r - l, RulerH));
        ctx.FillRectangle(Palette.Alpha(color, strip), new Rect(l, RulerH - 4, r - l, 3));
    }

    static readonly List<string> barLabels = [];
    static string BarLabel(int n)
    {
        while (barLabels.Count < n) barLabels.Add((barLabels.Count + 1).ToString());
        return barLabels[n - 1];
    }

    void DrawVelocity(DrawingContext ctx, double top, double W, double H)
    {
        ctx.FillRectangle(Palette.Panel, new Rect(0, top - VelGap, W, H - top + VelGap));
        // the bar's handle, lit while it is grabbed or under the pointer
        bool hot = gesture == "velsize" || gesture == null && OnVelSplit(lastPointer);
        ctx.DrawRectangle(hot ? Palette.Accent : Palette.Faint, null, new Rect(W / 2 - 16, top - VelGap / 2 - 1.5, 32, 3), 1.5, 1.5);
        ctx.DrawText(Loc.T("piano.velocity"), 10, Palette.Faint, new Point(6, top + 4), Palette.UiBold);
        using var clip = ctx.PushClip(new Rect(KeyW, top, Math.Max(0, W - KeyW), Math.Max(0, H - top)));
        double h = H - top - 6;
        // the other clips' velocities, faint (they are edited once their clip is picked)
        var faint = Palette.Alpha(color, 0.25);
        foreach (var c in others)
        {
            double start = StartBeat(c, bpm);
            if (XA(start + c.LengthBeats) < KeyW || XA(start) > W) continue;
            foreach (var n in c.Notes)
            {
                double x = XA(start + n.Beat);
                if (n.Beat >= c.LengthBeats || x < KeyW - 4 || x > W) continue;
                double bh = Math.Max(2, h * n.Velocity / 127.0);
                ctx.FillRectangle(faint, new Rect(Math.Round(x), H - 3 - bh, 3, bh));
            }
        }
        foreach (var n in notes)
        {
            double x = X(n.Beat);
            if (x < KeyW - 4 || x > W) continue;
            double bh = Math.Max(2, h * n.Velocity / 127.0);
            var brush = selected.Contains(n) || velGroup.Contains(n) ? Palette.Accent : Palette.Alpha(color, 0.8);
            ctx.FillRectangle(brush, new Rect(Math.Round(x), H - 3 - bh, 3, bh));
            ctx.FillRectangle(brush, new Rect(Math.Round(x) - 2, H - 3 - bh, 7, 2));
        }
    }

    // --- gestures ---------------------------------------------------------------------------------

    string? gesture;
    Point down, lastPointer;
    bool moved, changed;
    KeyModifiers modifiers;
    /// <summary>The note grabbed and, for every selected note, where it was when the gesture began.</summary>
    MidiNote? held;
    readonly Dictionary<MidiNote, (int Pitch, double Beat, double Beats)> from = [];
    MidiNote? velNote;
    /// <summary>The notes a velocity drag sets: the bar grabbed, or every selected note starting where it does.</summary>
    readonly List<MidiNote> velGroup = [];
    double velFrom;
    int heardPitch = -1;
    double downScrollX, downScrollY;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (clipId == null) return;
        var p = e.GetPosition(this);
        lastPointer = down = p;
        modifiers = e.KeyModifiers;
        var props = e.GetCurrentPoint(this).Properties;
        moved = changed = false;
        downScrollX = scrollX;
        downScrollY = scrollY;
        if (props.IsMiddleButtonPressed) { Start("pan", e); return; }
        // inside another clip of the track (not on a note of this one): that clip is the one edited from here on
        if (p.Y >= RulerH && p.Y < GridBottom && p.X >= KeyW && NoteAt(p) == null)
        {
            double at = Beat(p.X);
            if ((at < 0 || at >= clipBeats) && OtherAt(BeatA(p.X)) is { } other) ClipPicked?.Invoke(other.Id);
            if (clipId == null) return;
        }
        if (props.IsRightButtonPressed)
        {
            if (p.Y < RulerH && p.X >= KeyW)
            {
                GridMenuRequested?.Invoke();
                e.Handled = true;
                return;
            }
            if (p.Y >= RulerH && p.Y < GridBottom && p.X >= KeyW)
            {
                Start("erase", e);
                EraseAt(p);
            }
            return;
        }
        if (!props.IsLeftButtonPressed) return;
        if (p.Y < RulerH)
        {
            if (p.X >= KeyW) SeekRequested?.Invoke(SnapA(BeatA(p.X)) * 60 / bpm);
            e.Handled = true;
            return;
        }
        if (OnVelSplit(p))
        {
            if (e.ClickCount == 2)
            {
                VelocityHeight = DefaultVelH;
                VelocityHeightChanged?.Invoke(velH);
            }
            else
            {
                velFrom = VelH;
                Start("velsize", e);
            }
            e.Handled = true;
            return;
        }
        if (p.Y > GridBottom)
        {
            velNote = VelocityNoteAt(p.X);
            if (velNote == null) return;
            velGroup.Clear();
            if (selected.Contains(velNote))
            {
                foreach (var n in selected)
                    if (Math.Abs(n.Beat - velNote.Beat) < 1e-9) velGroup.Add(n);
            }
            else velGroup.Add(velNote);
            Start("velocity", e);
            SetVelocity(p);
            return;
        }
        if (p.X < KeyW)
        {
            heardPitch = PitchAt(p.Y);
            NoteAuditioned?.Invoke(heardPitch, lastVelocity);
            Start("keys", e);
            InvalidateVisual();
            return;
        }
        var hit = NoteAt(p);
        bool ctrl = (modifiers & KeyModifiers.Control) != 0;
        if (hit == null && ctrl)
        {
            Start("box", e);
            return;
        }
        if (hit == null)
        {
            // outside the clip nothing is written (it would not be heard): a click there only drops the selection
            double at = Beat(p.X);
            if (at < 0 || at >= clipBeats)
            {
                selected.Clear();
                InvalidateVisual();
                return;
            }
            // FL's pencil: a note in the cell under the pointer, with the last length, and it follows the drag
            hit = new MidiNote { Pitch = PitchAt(p.Y), Beat = Math.Max(0, Snap(at, floor: true)), Beats = lastBeats, Velocity = lastVelocity };
            notes.Add(hit);
            selected.Clear();
            changed = true;
        }
        else if (ctrl)
        {
            if (!selected.Remove(hit)) selected.Add(hit);
            InvalidateVisual();
            return;
        }
        if (!selected.Contains(hit))
        {
            selected.Clear();
            selected.Add(hit);
        }
        held = hit;
        lastBeats = hit.Beats;
        lastVelocity = hit.Velocity;
        from.Clear();
        foreach (var n in selected) from[n] = (n.Pitch, n.Beat, n.Beats);
        heardPitch = hit.Pitch;
        NoteAuditioned?.Invoke(hit.Pitch, hit.Velocity);
        Start(OnEdge(hit, p) && !changed ? "resize" : "move", e);
        InvalidateVisual();
    }

    void Start(string kind, PointerPressedEventArgs e)
    {
        gesture = kind;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        lastPointer = e.GetPosition(this);
        modifiers = e.KeyModifiers;
        if (gesture == null) { UpdateCursor(); return; }
        if (!moved && Math.Abs(lastPointer.X - down.X) < 3 && Math.Abs(lastPointer.Y - down.Y) < 3) return;
        moved = true;
        switch (gesture)
        {
            case "pan":
                scrollX = downScrollX - (lastPointer.X - down.X);
                scrollY = downScrollY - (lastPointer.Y - down.Y);
                ClampScroll();
                break;
            case "move": DragMove(); break;
            case "resize": DragResize(); break;
            case "erase": EraseAt(lastPointer); break;
            case "velocity": SetVelocity(lastPointer); break;
            case "velsize":
                velH = Math.Clamp(velFrom - (lastPointer.Y - down.Y), MinVelH, Math.Max(MinVelH, Bounds.Height - RulerH - VelGap - MinGridH));
                ClampScroll();
                break;
            case "keys":
                int k = PitchAt(lastPointer.Y);
                if (k != heardPitch) { heardPitch = k; NoteAuditioned?.Invoke(k, lastVelocity); }
                break;
        }
        InvalidateVisual();
    }

    void DragMove()
    {
        if (held == null) return;
        var (p0, b0, _) = from[held];
        int dp = PitchAt(lastPointer.Y) - p0;
        double db = Snap(b0 + (lastPointer.X - down.X) / beatW) - b0;
        // the group stays inside the keyboard and never before the clip's start
        foreach (var (n, f) in from)
        {
            dp = Math.Clamp(dp, MidiNote.MinPitch - f.Pitch, MidiNote.MaxPitch - f.Pitch);
            db = Math.Max(db, -f.Beat);
        }
        int before = held.Pitch;
        foreach (var (n, f) in from)
        {
            n.Pitch = f.Pitch + dp;
            n.Beat = f.Beat + db;
        }
        changed |= dp != 0 || db != 0;
        if (held.Pitch != before) { heardPitch = held.Pitch; NoteAuditioned?.Invoke(held.Pitch, held.Velocity); }
    }

    void DragResize()
    {
        if (held == null) return;
        var (_, b0, l0) = from[held];
        double end = Snap(b0 + l0 + (lastPointer.X - down.X) / beatW);
        double dl = Math.Max(end - b0, SnapBeats > 0 && (modifiers & KeyModifiers.Alt) == 0 ? SnapBeats : MidiNote.MinBeats) - l0;
        foreach (var (n, f) in from) n.Beats = Math.Max(MidiNote.MinBeats, f.Beats + dl);
        lastBeats = held.Beats;
        changed |= dl != 0;
    }

    void EraseAt(Point p)
    {
        if (NoteAt(p) is not { } n) return;
        notes.Remove(n);
        selected.Remove(n);
        changed = true;
        InvalidateVisual();
    }

    MidiNote? VelocityNoteAt(double x)
    {
        MidiNote? best = null;
        double bestD = 6;
        foreach (var n in notes)
        {
            double d = Math.Abs(X(n.Beat) + 1.5 - x);
            // a selected note wins a tie: the one being worked on
            if (d < bestD || d == bestD && best != null && selected.Contains(n)) { best = n; bestD = d; }
        }
        return best;
    }

    void SetVelocity(Point p)
    {
        if (velNote == null) return;
        double top = GridBottom + VelGap, h = Bounds.Height - top - 6;
        int v = Math.Clamp((int)Math.Round((Bounds.Height - 3 - p.Y) / h * 127), 1, 127);
        if (velGroup.All(n => n.Velocity == v)) return;
        foreach (var n in velGroup) n.Velocity = v;
        lastVelocity = v;
        changed = true;
    }

    Rect BoxRect() => new(new Point(Math.Min(down.X, lastPointer.X), Math.Min(down.Y, lastPointer.Y)),
        new Point(Math.Max(down.X, lastPointer.X), Math.Max(down.Y, lastPointer.Y)));

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (gesture == null) return;
        if (gesture == "box")
        {
            if ((modifiers & KeyModifiers.Shift) == 0 && (modifiers & KeyModifiers.Control) == 0) selected.Clear();
            var box = BoxRect();
            foreach (var n in notes) if (box.Intersects(NoteRect(n))) selected.Add(n);
        }
        bool commit = changed, resized = gesture == "velsize" && moved;
        EndGesture(e.Pointer);
        if (commit) Commit();
        if (resized) VelocityHeightChanged?.Invoke(velH);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (gesture == null) return;
        bool commit = changed;
        EndGesture(null);
        if (commit) Commit();
    }

    void EndGesture(IPointer? pointer)
    {
        gesture = null;
        held = null;
        velNote = null;
        velGroup.Clear();
        heardPitch = -1;
        from.Clear();
        pointer?.Capture(null);
        InvalidateVisual();
    }

    /// <summary>Drops a gesture in flight without committing it (another clip or an undo replaces the notes anyway).</summary>
    public void CancelGesture()
    {
        if (gesture == null) return;
        gesture = null;
        held = null;
        velNote = null;
        heardPitch = -1;
        from.Clear();
        changed = false;
        InvalidateVisual();
    }

    /// <summary>
    /// Moves the selected notes (all of them with none selected) to the magnet's steps on the song's grid, as one edit
    /// (<see cref="MidiEditing.Quantize(IList{MidiNote}, double, double, double, IReadOnlyCollection{MidiNote}?)"/>).
    /// Returns how many moved; -1 with no clip or the magnet in Libre.
    /// </summary>
    public int Quantize()
    {
        if (clipId == null || snapBeats <= 0) return -1;
        CancelGesture();
        int moved = MidiEditing.Quantize(notes, clipAtSec, bpm, snapBeats, selected.Count > 0 ? selected : null);
        if (moved > 0) Commit();
        return moved;
    }

    void Commit()
    {
        if (clipId == null) return;
        notes.Sort(MidiNote.Order);
        NotesCommitted?.Invoke(clipId, [.. notes.Select(n => n.Copy())]);
    }

    void UpdateCursor()
    {
        var p = lastPointer;
        var c = StandardCursorType.Arrow;
        if (clipId != null && p.X >= KeyW && p.Y >= RulerH && p.Y < GridBottom)
            c = NoteAt(p) is { } n ? (OnEdge(n, p) ? StandardCursorType.RightSide : StandardCursorType.SizeAll)
                : Beat(p.X) >= 0 && Beat(p.X) < clipBeats || OtherAt(BeatA(p.X)) != null ? StandardCursorType.Cross : StandardCursorType.Arrow;
        else if (p.X < KeyW && p.Y >= RulerH && p.Y < GridBottom) c = StandardCursorType.Hand;
        if (clipId != null && OnVelSplit(p)) c = StandardCursorType.SizeNorthSouth;
        InvalidateVisual(); // the bar's handle lights under the pointer
        Cursor = cursors.TryGetValue(c, out var cur) ? cur : cursors[c] = new Cursor(c);
    }

    static readonly Dictionary<StandardCursorType, Cursor> cursors = [];

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (clipId == null) return;
        var p = e.GetPosition(this);
        double d = e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X;
        // the same modifiers as the timeline (Configuración → Atajos): zoom, sideways, height
        switch (Shortcuts.FindWheel(e.KeyModifiers))
        {
            case "wheelZoom":
            {
                double at = BeatA(p.X);
                beatW = Math.Clamp(beatW * Math.Pow(1.2, d * Shortcuts.WheelSign("wheelZoom")), MinBeatW, MaxBeatW);
                scrollX = at * beatW - (p.X - KeyW);
                break;
            }
            case "wheelSideways":
                scrollX -= d * 60 * Shortcuts.WheelSign("wheelSideways");
                break;
            case "wheelHeight":
            {
                int at = PitchAt(p.Y);
                double offset = p.Y - Y(at);
                rowH = Math.Clamp(rowH + d * Shortcuts.WheelSign("wheelHeight"), MinRowH, MaxRowH);
                scrollY = (Pitches - 1 - at) * rowH + RulerH - p.Y + offset;
                break;
            }
            default:
                scrollY -= d * rowH * 3 * Shortcuts.WheelSign(Shortcuts.WheelScroll);
                break;
        }
        ClampScroll();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        ClampScroll();
    }

    /// <summary>
    /// The piano roll's keys while it has the focus: Supr / Retroceso erase the selected notes, Ctrl+A selects them all,
    /// ↑ ↓ move them a semitone (Shift: an octave), Esc drops the selection. True when it used the key.
    /// </summary>
    public bool HandleKey(KeyEventArgs e)
    {
        if (clipId == null || !IsKeyboardFocusWithin) return false;
        bool ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
        switch (e.Key)
        {
            case Key.Delete or Key.Back:
                // used even with nothing selected: let through, it would remove the whole clip in the timeline
                if (selected.Count == 0) return true;
                notes.RemoveAll(selected.Contains);
                selected.Clear();
                Commit();
                return true;
            case Key.A when ctrl:
                selected.Clear();
                selected.UnionWith(notes);
                InvalidateVisual();
                return true;
            case Key.Up or Key.Down when selected.Count > 0:
                int step = (e.Key == Key.Up ? 1 : -1) * ((e.KeyModifiers & KeyModifiers.Shift) != 0 ? 12 : 1);
                if (selected.Any(n => n.Pitch + step is < MidiNote.MinPitch or > MidiNote.MaxPitch)) return true;
                foreach (var n in selected) n.Pitch += step;
                if (selected.Count == 1) NoteAuditioned?.Invoke(selected.First().Pitch, selected.First().Velocity);
                Commit();
                return true;
            case Key.Escape when gesture != null:
            {
                // what the gesture did stays, as if it had been released
                bool commit = changed;
                EndGesture(null);
                if (commit) Commit();
                return true;
            }
            case Key.Escape when selected.Count > 0:
                selected.Clear();
                InvalidateVisual();
                return true;
        }
        return false;
    }
}
