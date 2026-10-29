using System.Text.Json;
using System.Text.RegularExpressions;
using GuitarCam.Audio;

namespace GuitarCam.Projects;

/// <summary>
/// Where a new take goes and how it lands in the project. Port of the target choice in
/// handleStartRecordingSequence and of the take built in handleStopRecording (GuitarCam/src/App.tsx).
/// The web keeps one take per lane; here the take becomes a Media plus a Clip on the target track, and
/// the track keeps the channel strip it had (the web's withChannel). The camera's video goes with it, mirrored
/// if the live image was (the web's mirror: liveMirror). Native differences: without a camera the take is
/// audio only and a new track is created without a video panel (the web refuses to record without one), and a
/// track armed by hand can already have clips: the take replaces them (they stay in Historial, undo brings them back).
/// A take started from the playhead (punch-in, <see cref="ProjectStore.RecordingJournal.StartSec"/> &gt; 0) replaces only
/// the stretch it covers, like <see cref="ClipEditing.DeleteRange"/>; one started at 0 still replaces the whole track.
/// </summary>
public static class TakeRecording
{
    /// <summary>Same as the web: takes shorter than this are dropped silently.</summary>
    public const double MinTakeSec = TakeRecorder.MinTakeDurationSec;

    static readonly Regex TakeNumberInName = new(@"^(?:Canal|Toma) (\d+)\b", RegexOptions.Compiled);

    /// <summary>
    /// The selected empty track; with a take (or nothing) selected, the next empty track after it, else the
    /// first one, so a template's lanes fill in order. Null means a new track at the end.
    /// </summary>
    public static Track? PickTarget(Project p, string? selectedTrackId)
    {
        int sel = p.Tracks.FindIndex(t => t.Id == selectedTrackId);
        static bool Free(Track t) => t.Clips.Count == 0 && !t.IsMidi && !t.Locked;
        if (sel >= 0 && Free(p.Tracks[sel])) return p.Tracks[sel];
        return p.Tracks.Where((t, i) => i > sel && Free(t)).FirstOrDefault() ?? p.Tracks.Find(Free);
    }

    /// <summary>The web's isFirstTake: no track has a clip yet, so nothing plays against the take.</summary>
    public static bool IsFirstTake(Project p) => !p.Tracks.Any(t => t.Clips.Count > 0);

    /// <summary>Position in the timeline, which decides the role: 0 base, 1 solo, the rest extra.</summary>
    public static int Slot(Project p, Track? target) => target == null ? p.Tracks.Count : p.Tracks.IndexOf(target);

    /// <summary>Numbers takes across the whole project (history included) so names stay unique.</summary>
    public static int NextTakeNumber(Project p) => p.Media.Select(Number).DefaultIfEmpty(0).Max() + 1;

    static int Number(Media m)
    {
        if (m.Extra?.TryGetValue("number", out var n) == true && n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out int v)) return v;
        var match = TakeNumberInName.Match(m.Name);
        return match.Success && int.TryParse(match.Groups[1].Value, out v) ? v : 0;
    }

    public static string TakeName(Project p, Track? target) => TakeName(p, target, NextTakeNumber(p));

    /// <summary>With the number given: several tracks recorded at once take consecutive numbers.</summary>
    public static string TakeName(Project p, Track? target, int number) =>
        TakeRecorder.DefaultTakeName(number, Slot(p, target), target?.Name);

    static string Role(int slot) => slot == 0 ? "base" : slot == 1 ? "solo" : "extra";

    /// <summary>
    /// Adds the recorded WAV as a media and a clip where recording started plus its sync correction.
    /// Returns the track that got it. A target that was removed meanwhile counts as no target; one with clips
    /// (armed by hand) loses them to the take: all of them from 0, only the covered stretch from the playhead. A target
    /// with a lock (<see cref="ClipEditing.HasLock"/>) keeps what it has: the take goes to a new track, never lost.
    /// </summary>
    public static Track AddTake(Project p, ProjectStore.RecordingJournal j, double durationSec, int sampleRate, int channels, int takeNumber)
    {
        var target = p.Tracks.Find(t => t.Id == j.TrackId);
        if (target != null && ClipEditing.HasLock(target)) target = null;
        if (target != null && j.StartSec > 0)
            ClipEditing.DeleteRange(p, target.Id, j.AtSec, j.AtSec + durationSec);
        else if (target != null)
            foreach (var c in target.Clips.ToList()) ClipEditing.RemoveClip(p, c.Id);
        int slot = Slot(p, target);
        var media = new Media
        {
            Id = j.MediaId,
            Name = j.Name,
            Kind = "recording",
            Audio = j.Audio,
            Video = j.Video,
            DurationSec = durationSec,
            SampleRate = sampleRate,
            Channels = channels,
            CreatedAt = j.StartedAt,
            SyncMs = j.SyncMs,
            VideoOffsetMs = j.VideoOffsetMs,
            Extra = new() { ["number"] = JsonSerializer.SerializeToElement(takeNumber) },
        };
        p.Media.Add(media);
        var track = target;
        if (track == null)
        {
            track = new Track { Video = j.Video != null };
            p.Tracks.Add(track);
        }
        // A lane whose strip was never touched gets the take defaults: compressor on, lead on the second lane.
        if (IsDefaultChannel(track.Channel))
        {
            track.Channel.Comp.On = true;
            track.Channel.Comp.Preset = TakeRecorder.DefaultCompPreset(slot);
        }
        track.Name = j.Name;
        track.Clips.Add(new Clip
        {
            MediaId = media.Id,
            AtSec = j.AtSec,
            Framing = new Framing { Mirror = j.Mirror && j.Video != null },
            Extra = new() { ["role"] = JsonSerializer.SerializeToElement(Role(slot)) },
        });
        p.AddLog("take-recorded", j.Name);
        return track;
    }

    static bool IsDefaultChannel(Channel c)
    {
        var d = new Channel();
        d.Normalize();
        return Shape(c) == Shape(d);
    }

    /// <summary>The channel as JSON without its slots' ids, which every new channel draws at random.</summary>
    static string Shape(Channel c)
    {
        var copy = ProjectLibrary.Clone(c);
        foreach (var s in copy.Slots) s.Id = "";
        return JsonSerializer.Serialize(copy, ProjectStore.Json);
    }
}
