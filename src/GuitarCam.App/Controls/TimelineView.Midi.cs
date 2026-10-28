using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using GuitarCam.App.Model;
using GuitarCam.Audio.Dsp;
using GuitarCam.Projects;

namespace GuitarCam.App.Controls;

/// <summary>
/// The clips of MIDI tracks (native only, stage 1 of the MIDI editor): a rounded box in the track's color with its
/// notes drawn small inside, between the clip's lowest and highest pitch. A click selects one, its body moves it (to
/// another time and another MIDI track, with the grid's magnet; Alt, free), its right edge changes its length by grid
/// steps, a double click opens it in the piano roll and a double click on an empty stretch of the lane makes a new one
/// there. Ctrl+click and the selection box pick several, takes and MIDI clips together (asked by the user): grabbing one
/// of them by its body carries them all by the same time, each on its own track (<see cref="ClipsMoved"/>). The window
/// does the edit (<see cref="MidiClipChanged"/>): the control never touches the project.
/// </summary>
public sealed partial class TimelineView
{
    /// <summary>The MIDI clip selected (its track is <see cref="Selected"/>), or null.</summary>
    public string? SelectedMidiId { get; private set; }
    /// <summary>A MIDI clip moved, resized or carried to another track: its id, start, length in beats and new track (null = the same).</summary>
    public event Action<string, double, double, string?>? MidiClipChanged;
    /// <summary>A double click on a MIDI clip: open it in the piano roll.</summary>
    public event Action<LiveTrack, string>? MidiClipOpenRequested;
    /// <summary>A double click on an empty stretch of a MIDI lane: a new clip there (seconds, already snapped).</summary>
    public event Action<LiveTrack, double>? MidiClipCreateRequested;
    /// <summary>A right click on a MIDI clip: its menu.</summary>
    public event Action<LiveTrack, string, Point>? MidiClipContextRequested;

    // the "midi" gesture: the clip held, its start and length when it was grabbed and now, and whether it is the edge
    MidiClip? midiHeld;
    double midiFromSec, midiFromBeats, midiAtSec, midiBeats;
    bool midiEdge;
    /// <summary>The MIDI track the held clip is over, or -1.</summary>
    int midiOver = -1;
    /// <summary>The held MIDI clip is one of several boxed ones: it carries them all.</summary>
    bool midiCarrying;
    /// <summary>The boxed MIDI clips a carry moves (whichever clip was grabbed), with where each one started.</summary>
    readonly Dictionary<MidiClip, double> midiCarried = [];
    /// <summary>How far a carry moves everything (seconds), already snapped and kept from crossing 0.</summary>
    double carryShift;
    /// <summary>What was selected when a Ctrl+click on a take began: on release without moving, the take is added or taken out.</summary>
    HashSet<string>? toggleBase;

    /// <summary>Px from a MIDI clip's right edge that grab it to change its length.</summary>
    const double MidiEdgeGrab = 7;

    double BeatSec => 60 / Math.Max(1, Bpm);

    MidiClip? MidiClipAt(int i, double x)
    {
        if (i < 0 || i >= tracks.Count || tracks[i].Track.MidiClips is not { } clips) return null;
        for (int k = clips.Count - 1; k >= 0; k--)
        {
            var (start, end) = MidiSpan(clips[k]);
            if (x >= X(start) && x <= X(end)) return clips[k];
        }
        return null;
    }

    /// <summary>A clip's start and end as drawn: a carried or held one where the gesture has it.</summary>
    (double Start, double End) MidiSpan(MidiClip c)
    {
        if (moved && midiCarried.TryGetValue(c, out double from)) return (from + carryShift, from + carryShift + c.LengthBeats * BeatSec);
        return c == midiHeld && moved ? (midiAtSec, midiAtSec + midiBeats * BeatSec) : (c.AtSec, c.EndSec(Bpm));
    }

    bool OnMidiEdge(MidiClip c, double x) => Math.Abs(x - X(MidiSpan(c).End)) <= MidiEdgeGrab;

    void DrawMidiClips(DrawingContext ctx, int i, double y, double W)
    {
        var t = tracks[i];
        if (t.Track.MidiClips is not { } clips) return;
        foreach (var c in clips)
            if (c != midiHeld || midiOver == i || midiOver < 0) DrawMidiClip(ctx, t, c, y, RowH(t.Track), W, c == midiHeld && moved);
    }

    /// <summary>The held clip over another MIDI track, drawn there.</summary>
    void DrawMidiCarried(DrawingContext ctx, double W)
    {
        if (midiHeld == null || !moved || midiOver < 0 || midiOver >= tracks.Count || midiOver == fromIndex) return;
        DrawMidiClip(ctx, tracks[midiOver], midiHeld, LaneY(midiOver), LaneH(midiOver), W, true);
    }

    void DrawMidiClip(DrawingContext ctx, LiveTrack t, MidiClip c, double y, double lh, double W, bool held)
    {
        var (start, end) = MidiSpan(c);
        double x0 = X(start), x1 = X(end);
        if (x1 < HeaderW || x0 > W) return;
        bool sel = c.Id == SelectedMidiId || boxed.Contains(c.Id);
        var color = TrackIcons.ColorOf(t.Track) ?? Palette.AccentColor;
        bool silent = t.Silenced || tracks.Any(o => o.Soloed) && !t.Soloed;
        var r = new Rect(x0, y + 5, Math.Max(2, x1 - x0), lh - 10);
        using var opacity = ctx.PushOpacity(silent ? 0.5 : held ? 0.85 : 1);
        ctx.DrawRectangle(Palette.Alpha(color, sel ? 0.24 : 0.14), Palette.Pen(Palette.Alpha(color, sel ? 0.95 : 0.45), sel ? 1.5 : 1), r, 5, 5);
        using (ctx.PushClip(new RoundedRect(r, 5)))
        {
            var notes = c.Notes;
            if (notes.Count == 0)
            {
                if (r.Height >= 26 && r.Width > 60)
                    ctx.DrawText(Loc.T("midi.clip.empty"), 11, Palette.Faint, new Point(Math.Max(x0, HeaderW) + 8, y + 9), Palette.Ui);
                return;
            }
            int lo = 127, hi = 0;
            foreach (var n in notes) { lo = Math.Min(lo, n.Pitch); hi = Math.Max(hi, n.Pitch); }
            // a range of at least an octave, so a single note doesn't fill the clip from top to bottom
            int span = Math.Max(12, hi - lo + 1);
            lo -= (span - (hi - lo + 1)) / 2;
            double top = r.Top + 4, h = Math.Max(4, r.Height - 8);
            double rowH = Math.Max(1, Math.Min(4, h / span));
            var fill = Palette.Alpha(color, sel ? 1 : 0.85);
            double beatPx = BeatSec * pps;
            foreach (var n in notes)
            {
                if (n.Beat >= c.LengthBeats) break;
                double nx = x0 + n.Beat * beatPx;
                if (nx > W) break;
                double nw = Math.Max(1.5, Math.Min(n.EndBeat, c.LengthBeats) * beatPx - n.Beat * beatPx - 1);
                if (nx + nw < HeaderW) continue;
                double ny = top + (span - 1 - (n.Pitch - lo)) / (double)span * h;
                ctx.FillRectangle(fill, new Rect(nx, ny, nw, rowH));
            }
        }
    }

    /// <summary>A left press on a MIDI lane (past the headers). Always handles it.</summary>
    void MidiPressed(int i, Point p, PointerPressedEventArgs e)
    {
        var c = MidiClipAt(i, p.X);
        if (e.ClickCount == 2)
        {
            if (c != null) MidiClipOpenRequested?.Invoke(tracks[i], c.Id);
            else MidiClipCreateRequested?.Invoke(tracks[i], Math.Max(0, ClipEditing.Snap(Sec(p.X), Grid, (modifiers & KeyModifiers.Alt) != 0)));
            e.Handled = true;
            return;
        }
        if (c == null)
        {
            if ((modifiers & KeyModifiers.Control) == 0)
            {
                SelectedMidiId = null;
                Select(i);
            }
            StartBox(e);
            return;
        }
        if ((modifiers & KeyModifiers.Control) != 0)
        {
            ToggleBoxed(c.Id, SelectionSeed());
            e.Handled = true;
            return;
        }
        midiEdge = OnMidiEdge(c, p.X);
        // a boxed clip grabbed by its body carries the other boxed ones; its edge resizes it alone
        midiCarrying = boxed.Count > 1 && boxed.Contains(c.Id) && !midiEdge;
        SelectedMidiId = c.Id;
        if (midiCarrying)
        {
            SelectOne(i);
            StartCarry(null);
        }
        else Select(i);
        midiHeld = c;
        midiFromSec = midiAtSec = c.AtSec;
        midiFromBeats = midiBeats = c.LengthBeats;
        fromIndex = midiOver = i;
        StartGesture("midi", e);
    }

    /// <summary>A right press on a MIDI clip asks for its menu; false when there is none under the pointer.</summary>
    bool MidiContext(int i, Point p)
    {
        if (MidiClipAt(i, p.X) is not { } c) return false;
        SelectedMidiId = c.Id;
        // on a boxed clip the others stay boxed, so the menu acts on them all
        if (boxed.Count > 1 && boxed.Contains(c.Id)) SelectOne(i);
        else Select(i);
        MidiClipContextRequested?.Invoke(tracks[i], c.Id, p);
        return true;
    }

    void UpdateMidiDrag()
    {
        if (midiHeld == null) return;
        double dxPx = lastPointer.X - down.X + scrollX - downScrollX;
        if (!moved && Math.Abs(dxPx) < 3 && Math.Abs(lastPointer.Y - down.Y) < 3) return;
        moved = true;
        bool free = (modifiers & KeyModifiers.Alt) != 0;
        double grid = Grid;
        if (midiCarrying)
        {
            // everything boxed moves by the same snapped time, each on its own track, none before 0
            double dt = dxPx / pps;
            if (!free && grid > 0) dt = Math.Round(dt / grid) * grid;
            foreach (double from in midiCarried.Values) dt = Math.Max(dt, -from);
            foreach (var (lc, o) in carriedAlong) dt = Math.Max(dt, ClipEnvelope.MinClipSec - Window(lc, o).End);
            carryShift = dt;
            foreach (var (lc, o) in carriedAlong)
            {
                var v = ClipEditing.Copy(o);
                v.AtSec = ClipEditing.ToSample(o.AtSec + dt);
                carriedPreview[lc] = v;
            }
        }
        else if (midiEdge)
        {
            // the length goes by grid steps (a sixteenth when the magnet is off), never under a sixteenth
            double step = free || grid <= 0 ? MidiClip.MinBeats : Math.Max(MidiClip.MinBeats, grid / BeatSec);
            double beats = midiFromBeats + dxPx / pps / BeatSec;
            midiBeats = Math.Clamp(Math.Round(beats / step) * step, MidiClip.MinBeats, MidiClip.MaxBeats);
        }
        else
        {
            // whole grid steps from where it was, like a take: a clip off the grid stays as far off
            double dt = dxPx / pps;
            if (!free && grid > 0) dt = Math.Round(dt / grid) * grid;
            midiAtSec = Math.Max(0, midiFromSec + dt);
            int over = HitTrack(lastPointer.Y);
            if (over >= 0 && over < tracks.Count && tracks[over].Track.IsMidi) midiOver = over;
        }
    }

    /// <summary>The cursor over a MIDI lane: the edge resizes, a clip moves.</summary>
    StandardCursorType? MidiCursor(int i, Point p)
    {
        if (i < 0 || i >= tracks.Count || !tracks[i].Track.IsMidi || p.X < HeaderW) return null;
        if (MidiClipAt(i, p.X) is not { } c) return StandardCursorType.Arrow;
        return OnMidiEdge(c, p.X) ? StandardCursorType.RightSide : StandardCursorType.Hand;
    }

    /// <summary>Selects a MIDI clip by id (after the window made or moved it).</summary>
    public void SelectMidiClip(int index, string? clipId)
    {
        SelectedMidiId = clipId;
        Select(index);
    }

    /// <summary>What the "midi" gesture ends in (taken before the gesture is cleared), or null when the clip didn't change.</summary>
    (string Id, double At, double Beats, string? To)? MidiResult()
    {
        var c = midiHeld;
        if (c == null || !moved) return null;
        string? to = midiOver >= 0 && midiOver < tracks.Count && midiOver != fromIndex ? tracks[midiOver].Track.Id : null;
        return midiAtSec != c.AtSec || midiBeats != c.LengthBeats || to != null ? (c.Id, midiAtSec, midiBeats, to) : null;
    }

    void ReleaseMidi((string Id, double At, double Beats, string? To)? result)
    {
        if (result is { } r) MidiClipChanged?.Invoke(r.Id, r.At, r.Beats, r.To);
    }

    void ClearMidiGesture()
    {
        midiHeld = null;
        midiOver = -1;
        midiEdge = false;
        midiCarrying = false;
        midiCarried.Clear();
        carryShift = 0;
    }

    /// <summary>Notes every boxed MIDI clip a carry moves, with where it starts.</summary>
    void StartMidiCarry()
    {
        midiCarried.Clear();
        carryShift = 0;
        foreach (var t in tracks)
            if (t.Track.MidiClips is { } clips)
                foreach (var c in clips)
                    if (boxed.Contains(c.Id)) midiCarried[c] = c.AtSec;
    }

    /// <summary>Where each carried MIDI clip lands (taken before the gesture is cleared).</summary>
    List<(string Id, double AtSec)> MidiMoves() => [.. midiCarried.Select(kv => (kv.Key.Id, ClipEditing.ToSample(kv.Value + carryShift)))];

    /// <summary>Boxes the MIDI clips of lane <paramref name="i"/> that the selection box touches.</summary>
    void BoxMidi(int i, Rect box)
    {
        if (tracks[i].Track.MidiClips is not { } clips) return;
        foreach (var c in clips)
        {
            var (start, end) = MidiSpan(c);
            if (new Rect(X(start), LaneY(i) + 5, Math.Max(2, X(end) - X(start)), LaneH(i) - 10).Intersects(box)) boxed.Add(c.Id);
        }
    }

    /// <summary>The earliest boxed MIDI clip of a lane, or null.</summary>
    MidiClip? FirstBoxedMidi(int i) => tracks[i].Track.MidiClips?.Where(c => boxed.Contains(c.Id)).MinBy(c => c.AtSec);

    /// <summary>What looks selected now: the boxed clips, else the selected MIDI clip, else the selected track's take.</summary>
    HashSet<string> SelectionSeed()
    {
        var s = new HashSet<string>();
        if (boxed.Count > 1) s.UnionWith(boxed);
        else if (SelectedMidiId != null) s.Add(SelectedMidiId);
        else if (Selected >= 0 && Selected < tracks.Count && SelectedClipId != null && tracks[Selected].Clips.Any(c => c.Clip.Id == SelectedClipId))
            s.Add(SelectedClipId);
        return s;
    }

    /// <summary>The lane of a take or a MIDI clip, or -1.</summary>
    int LaneOf(string id)
    {
        for (int i = 0; i < tracks.Count; i++)
            if (tracks[i].Clips.Any(c => c.Clip.Id == id) || tracks[i].Track.MidiClips?.Any(c => c.Id == id) == true) return i;
        return -1;
    }

    bool IsMidiClip(string id) => tracks.Any(t => t.Track.MidiClips?.Any(c => c.Id == id) == true);

    /// <summary>
    /// Ctrl+click on a clip (take or MIDI): adds it to <paramref name="set"/> (what was selected) or takes it out. One
    /// left is a plain selection; more are boxed together, with the one clicked (or another one) as the selected one.
    /// </summary>
    void ToggleBoxed(string id, HashSet<string> set)
    {
        if (!set.Remove(id)) set.Add(id);
        if (set.Count <= 1)
        {
            string only = set.Count == 1 ? set.First() : id;
            int lane = LaneOf(only);
            if (set.Count == 0) { SelectedMidiId = null; Select(lane); }
            else if (IsMidiClip(only)) SelectMidiClip(lane, only);
            else SelectClip(lane, only);
            return;
        }
        string primary = set.Contains(id) ? id : set.First();
        boxed.Clear();
        boxed.UnionWith(set);
        if (IsMidiClip(primary)) SelectedMidiId = primary;
        else
        {
            SelectedMidiId = null;
            SelectedClipId = primary;
        }
        SelectOne(LaneOf(primary));
    }
}
