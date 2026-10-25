using GuitarCam.Audio;
using GuitarCam.Audio.Dsp;
using System.Text.Json;

namespace GuitarCam.Projects;

/// <summary>The five clip handles in GuitarCam/src/components/Timeline.tsx.</summary>
public enum ClipEditMode { Move, TrimStart, TrimEnd, FadeIn, FadeOut }

/// <summary>
/// Port of Timeline.tsx clip gestures and App.tsx lane operations. Times use native seconds instead of
/// audioOffsetMs. Native tracks keep their IDs/channels; on multi-clip tracks only the clip under the
/// pointer swaps, and dropping on empty space moves the clip without disturbing the other clips.
/// lastTimelineClip / lastTimelineChannel in media extension data retain the web history's last edits
/// when a native clip leaves the timeline (the recording files remain immutable).
/// Media files, the media collection and recording sync corrections are never modified.
/// </summary>
public static class ClipEditing
{
    public static Clip Copy(Clip clip) => ProjectLibrary.Clone(clip);

    public static ClipEnvelope.ClipWindow Window(Clip c, Media? m, double? durationSec = null) =>
        ClipEnvelope.Window(durationSec ?? m?.DurationSec ?? 0, c.AtSec, c.TrimStartSec, c.TrimEndSec, c.EffectiveSyncMs(m));

    public static double GridSeconds(string mode, double bpm) => (mode switch
    {
        "bar" => 4, "1/2" => 2, "1/4" => 1, "1/8" => 0.5,
        "1/8T" => 1.0 / 3, "1/16" => 0.25, _ => 0,
    }) * 60 / Math.Max(1, bpm);

    // JavaScript Math.round: half ties go toward positive infinity, including negative offsets.
    static double JsRound(double v) => Math.Floor(v + 0.5);
    /// <summary>
    /// The finest step a clip moves, trims or fades by: one sample at the engine's 48 kHz. It was the millisecond, which
    /// at the deepest zoom (96 000 px/s) is 96 px, far coarser than the grid's finest lines, even moving free with Alt.
    /// </summary>
    const double SamplesPerSec = 48000;
    public static double ToSample(double sec) => JsRound(sec * SamplesPerSec) / SamplesPerSec;
    static double Limit(double v, double max) => Math.Max(0, Math.Min(max, v));
    public static double Snap(double sec, double grid, bool free) => free || grid <= 0 ? sec : JsRound(sec / grid) * grid;

    /// <summary>Calculates a gesture from its original clip, never incrementally from the previous pointer move.</summary>
    public static Clip Drag(Clip original, Media? media, ClipEditMode mode, double dx, double pps,
        double playhead, double grid, bool free, IEnumerable<ClipEnvelope.ClipWindow> others, double? durationSec = null)
    {
        var c = Copy(original);
        var w = Window(original, media, durationSec);
        var fades = ClipEnvelope.FitFades(c.FadeInSec, c.FadeOutSec, w.End - w.Start);
        double len = w.End - w.Start;
        switch (mode)
        {
            case ClipEditMode.Move:
                double shift = dx;
                if (!free)
                {
                    // Native only: with a grid the clip moves in whole grid steps and edges don't pull it. Near the start
                    // the edges of every take cluster there, and mixing both magnets made the clip jump between them.
                    // The step is relative (asked by the user): a clip off the grid keeps its offset and lands a bar (or
                    // a beat) away, as played; putting it on the grid is a free move with Alt.
                    if (grid > 0) shift = Snap(dx, grid, false);
                    double best = grid > 0 ? 0 : 8 / Math.Max(0.001, pps); // SNAP_PX in Timeline.tsx.
                    var targets = new List<double> { 0, Math.Max(0, playhead) };
                    foreach (var other in others) { targets.Add(other.Anchor); targets.Add(other.End); }
                    foreach (double target in targets)
                        foreach (double edge in new[] { w.Anchor, w.End })
                        {
                            double delta = target - (edge + dx);
                            if (Math.Abs(delta) >= best) continue;
                            best = Math.Abs(delta);
                            shift = dx + delta;
                        }
                }
                shift = Math.Max(shift, ClipEnvelope.MinClipSec - w.End);
                c.AtSec = ToSample(c.AtSec + shift);
                break;
            case ClipEditMode.TrimStart:
                double from = dx > 0 ? w.TrimStart + w.Cut : w.TrimStart;
                double start = Limit(from + dx, w.Dur - c.TrimEndSec - ClipEnvelope.MinClipSec);
                c.TrimStartSec = ToSample(free ? start : Limit(Snap(w.Origin + start, grid, false) - w.Origin,
                    w.Dur - c.TrimEndSec - ClipEnvelope.MinClipSec));
                break;
            case ClipEditMode.TrimEnd:
                double trim = Limit(c.TrimEndSec - dx, w.Dur - c.TrimStartSec - ClipEnvelope.MinClipSec);
                c.TrimEndSec = ToSample(Limit(w.Origin + w.Dur - Snap(w.Origin + w.Dur - trim, grid, free),
                    w.Dur - c.TrimStartSec - ClipEnvelope.MinClipSec));
                break;
            case ClipEditMode.FadeIn:
                double fi = Limit(fades.FadeIn + dx, len - fades.FadeOut);
                c.FadeInSec = ToSample(Limit(Snap(w.Start + fi, grid, free) - w.Start, len - fades.FadeOut));
                break;
            case ClipEditMode.FadeOut:
                double fo = Limit(fades.FadeOut - dx, len - fades.FadeIn);
                c.FadeOutSec = ToSample(Limit(w.End - Snap(w.End - fo, grid, free), len - fades.FadeIn));
                break;
        }
        return c;
    }

    public static (Track Track, Clip Clip) Find(Project p, string clipId)
    {
        foreach (var t in p.Tracks)
            if (t.Clips.Find(c => c.Id == clipId) is { } c) return (t, c);
        throw new ArgumentException(Texts.T("err.clip.clipNotFound", "No encontré ese clip en el proyecto."));
    }

    public static void Replace(Project p, Clip value)
    {
        var (track, clip) = Find(p, value.Id);
        track.Clips[track.Clips.IndexOf(clip)] = Copy(value);
    }

    public static string MoveToTrack(Project p, string clipId, string targetId, string? swapId = null)
    {
        var (from, clip) = Find(p, clipId);
        var to = p.Tracks.Find(t => t.Id == targetId) ?? throw new ArgumentException(Texts.T("err.clip.trackNotFound", "No encontré esa pista."));
        if (from == to) return to.Id;
        Clip? other = swapId == null ? null : to.Clips.Find(c => c.Id == swapId)
            ?? throw new ArgumentException(Texts.T("err.clip.swapNotFound", "No encontré el clip para intercambiar."));
        int index = from.Clips.IndexOf(clip);
        from.Clips.RemoveAt(index);
        if (other != null)
        {
            int targetIndex = to.Clips.IndexOf(other);
            to.Clips[targetIndex] = clip;
            from.Clips.Insert(index, other);
        }
        else to.Clips.Add(clip);
        return to.Id;
    }

    public static void RemoveClip(Project p, string id)
    {
        var (track, clip) = Find(p, id);
        Remember(p, track, clip);
        track.Clips.Remove(clip);
        if (track.Clips.Count == 0) track.Name = FreeTrackName(p, p.Tracks.IndexOf(track) + 1);
    }

    public static string AddEmpty(Project p, bool video = true)
    {
        var track = new Track { Name = FreeTrackName(p, p.Tracks.Count + 1), Video = video };
        p.Tracks.Add(track);
        return track.Id;
    }

    static string FreeTrackName(Project p, int n)
    {
        var used = p.Tracks.Select(t => t.Name).ToHashSet();
        while (used.Contains($"Pista {n}")) n++;
        return $"Pista {n}";
    }

    public static string AddMedia(Project p, string mediaId, string? selectedTrackId)
    {
        var media = p.FindMedia(mediaId) ?? throw new ArgumentException(Texts.T("err.clip.mediaNotFound", "No encontré ese canal en el historial."));
        if (p.Tracks.Any(t => t.Clips.Any(c => c.MediaId == mediaId))) throw new ArgumentException(Texts.T("err.clip.mediaInUse", "Ese canal ya está en el timeline."));
        var target = p.Tracks.Find(t => t.Id == selectedTrackId && t.Clips.Count == 0);
        Channel? previousChannel = null;
        Clip? previousClip = null;
        if (media.Extra?.TryGetValue("lastTimelineChannel", out var channelJson) == true)
            previousChannel = channelJson.Deserialize<Channel>(ProjectStore.Json);
        if (media.Extra?.TryGetValue("lastTimelineClip", out var clipJson) == true)
            previousClip = clipJson.Deserialize<Clip>(ProjectStore.Json);
        if (target == null)
        {
            // an imported file comes back as it was imported: audio only and a neutral strip
            bool imported = media.Kind == "import";
            target = new Track { Name = media.Name, Video = !imported, Channel = previousChannel ?? (imported ? new Channel()
                : new Channel { Comp = new CompSettings { On = true, Preset = p.Tracks.Count == 1 ? "lead" : "rhythm" } }) };
            p.Tracks.Add(target);
        }
        else target.Name = media.Name;
        var clip = previousClip ?? new Clip { MediaId = media.Id, AtSec = media.SyncMs / 1000 };
        clip.Id = Ids.New("clp");
        target.Clips.Add(clip);
        return target.Id;
    }

    static void Remember(Project p, Track track, Clip clip)
    {
        if (p.FindMedia(clip.MediaId) is not { } media) return;
        media.Extra ??= [];
        media.Extra["lastTimelineClip"] = JsonSerializer.SerializeToElement(clip, ProjectStore.Json);
        media.Extra["lastTimelineChannel"] = JsonSerializer.SerializeToElement(track.Channel, ProjectStore.Json);
    }

    public static void RemoveTrack(Project p, string id)
    {
        if (p.Tracks.Find(t => t.Id == id) is not { } track) return;
        foreach (var c in track.Clips) Remember(p, track, c);
        int index = p.Tracks.IndexOf(track);
        p.Tracks.Remove(track);
        TrackFolders.TrackRemoved(p, index);
    }

    public static void Reorder(Project p, int from, int to)
    {
        if (from < 0 || to < 0 || from >= p.Tracks.Count || to >= p.Tracks.Count) throw new ArgumentOutOfRangeException(nameof(to));
        if (from == to) return;
        var track = p.Tracks[from];
        p.Tracks.RemoveAt(from);
        p.Tracks.Insert(to, track);
        TrackFolders.Normalize(p);
    }

    public static void ToStart(Project p, string id, double? durationSec = null)
    {
        var (_, clip) = Find(p, id);
        clip.AtSec = ToSample(clip.AtSec - Window(clip, p.FindMedia(clip.MediaId), durationSec).Anchor);
    }

    /// <summary>Puts a clip's start (its anchor, where the take begins) on the nearest line of the grid.</summary>
    public static void AlignToGrid(Project p, string id, double grid, double? durationSec = null)
    {
        var (_, clip) = Find(p, id);
        double anchor = Window(clip, p.FindMedia(clip.MediaId), durationSec).Anchor;
        clip.AtSec = ToSample(clip.AtSec + Snap(anchor, grid, false) - anchor);
    }

    public static void FixCut(Project p, string id, double? durationSec = null)
    {
        var (_, clip) = Find(p, id);
        var w = Window(clip, p.FindMedia(clip.MediaId), durationSec);
        clip.TrimStartSec = ToSample(w.TrimStart + w.Cut);
    }

    /// <summary>
    /// Splits a clip at a timeline time into two clips of the same media (native only: the web has one take
    /// per lane). The left piece keeps the fade in, the right one the fade out and everything else (gain,
    /// framing, sync); the boundary is shared exactly, so playback and export carry on without a gap.
    /// Returns the right piece's id.
    /// </summary>
    public static string Split(Project p, string id, double atSec, double? durationSec = null)
    {
        var (track, left) = Find(p, id);
        var w = Window(left, p.FindMedia(left.MediaId), durationSec);
        if (atSec < w.Start + ClipEnvelope.MinClipSec || atSec > w.End - ClipEnvelope.MinClipSec)
            throw new ArgumentException(Texts.T("err.clip.splitEdge", "El cabezal tiene que estar adentro del clip, a más de 0,1 s de los bordes."));
        double trimStart = ToSample(atSec - w.Origin);
        var right = Copy(left);
        right.Id = Ids.New("clp");
        right.TrimStartSec = trimStart;
        right.FadeInSec = right.VideoFadeInSec = 0;
        left.TrimEndSec = w.Dur - trimStart;
        left.FadeOutSec = left.VideoFadeOutSec = 0;
        track.Clips.Insert(track.Clips.IndexOf(left) + 1, right);
        return right.Id;
    }

    /// <summary>
    /// Deletes what is between two timeline times on one track (native only): every clip the range touches is cut
    /// there, the middle goes and the rest stays where it was, leaving a gap (nothing slides to fill it). A clip
    /// inside the range goes whole; one that sticks out on one side is trimmed; one that sticks out on both is
    /// split in two, the left piece keeping the fade in and the right one the fade out (the cut edges get none).
    /// A leftover shorter than <see cref="ClipEnvelope.MinClipSec"/> goes too, like the rest of the editing does not
    /// allow smaller clips. Media files are never touched. Returns how many clips changed (0 = the range was empty).
    /// </summary>
    public static int DeleteRange(Project p, string trackId, double startSec, double endSec, Func<Clip, double?>? lengthOf = null)
    {
        var track = p.Tracks.Find(t => t.Id == trackId) ?? throw new ArgumentException(Texts.T("err.clip.trackNotFound", "No encontré esa pista."));
        if (!(endSec > startSec)) return 0;
        int changed = 0;
        foreach (var clip in track.Clips.ToList())
        {
            var w = Window(clip, p.FindMedia(clip.MediaId), lengthOf?.Invoke(clip));
            if (endSec <= w.Start || startSec >= w.End) continue;
            changed++;
            bool keepLeft = startSec - w.Start >= ClipEnvelope.MinClipSec;
            bool keepRight = w.End - endSec >= ClipEnvelope.MinClipSec;
            if (!keepLeft && !keepRight) { RemoveClip(p, clip.Id); continue; }
            if (keepLeft && keepRight)
            {
                var right = Copy(clip);
                right.Id = Ids.New("clp");
                right.TrimStartSec = ToSample(endSec - w.Origin);
                right.FadeInSec = right.VideoFadeInSec = 0;
                clip.TrimEndSec = ToSample(w.Dur - (startSec - w.Origin));
                clip.FadeOutSec = clip.VideoFadeOutSec = 0;
                track.Clips.Insert(track.Clips.IndexOf(clip) + 1, right);
            }
            else if (keepLeft)
            {
                clip.TrimEndSec = ToSample(w.Dur - (startSec - w.Origin));
                clip.FadeOutSec = clip.VideoFadeOutSec = 0;
            }
            else
            {
                clip.TrimStartSec = ToSample(endSec - w.Origin);
                clip.FadeInSec = clip.VideoFadeInSec = 0;
            }
        }
        return changed;
    }

    /// <summary>
    /// Copies a clip on its own track right after its end (D in Studio One), including the part a cut at 0
    /// hides, so the copy starts exactly where the original stops. Returns the copy's id.
    /// </summary>
    public static string Duplicate(Project p, string id, double? durationSec = null)
    {
        var (track, clip) = Find(p, id);
        var w = Window(clip, p.FindMedia(clip.MediaId), durationSec);
        var copy = Copy(clip);
        copy.Id = Ids.New("clp");
        copy.AtSec = ToSample(clip.AtSec + w.End - (w.Origin + w.TrimStart));
        track.Clips.Insert(track.Clips.IndexOf(clip) + 1, copy);
        return copy.Id;
    }

    /// <summary>Clip gain in dB, within <see cref="Clip.MinGainDb"/>..<see cref="Clip.MaxGainDb"/>.</summary>
    public static void SetGain(Project p, string id, double db) =>
        Find(p, id).Clip.GainDb = Math.Clamp(double.IsFinite(db) ? db : 0, Clip.MinGainDb, Clip.MaxGainDb);

    /// <summary>Replaces the clip's volume curve (a new list, sorted and clamped like <see cref="Clip"/>'s Normalize).</summary>
    public static void SetCurve(Project p, string id, IEnumerable<GainPoint> points)
    {
        var c = Find(p, id).Clip;
        c.GainCurve = [.. points.Where(g => double.IsFinite(g.Sec) && double.IsFinite(g.Db)).OrderBy(g => g.Sec)
            .Select(g => new GainPoint { Sec = g.Sec, Db = Math.Clamp(g.Db, ClipGainCurve.MinDb, ClipGainCurve.MaxDb) })];
    }

    public static void Reset(Project p, string id)
    {
        var (_, c) = Find(p, id);
        c.TrimStartSec = c.TrimEndSec = c.FadeInSec = c.FadeOutSec = c.VideoFadeInSec = c.VideoFadeOutSec = c.GainDb = 0;
        c.GainCurve = [];
    }
}
