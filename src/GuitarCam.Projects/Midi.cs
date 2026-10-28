using GuitarCam.Audio;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GuitarCam.Projects;

/// <summary>
/// What a track holds (native only): takes (<see cref="Audio"/>, the clips of a media) or notes for an instrument
/// (<see cref="Midi"/>, <see cref="Track.MidiClips"/>). A MIDI track never gets a video panel and never records from an
/// input: it is for building the backing track the guitar is played over.
/// </summary>
public static class TrackKinds
{
    public const string Audio = "audio", Midi = "midi";

    public static string Normalize(string? kind) => kind == Midi ? Midi : Audio;
}

/// <summary>
/// A stretch of notes on a MIDI track (native only, asked by the user: clips per track, like Logic or Studio One).
/// It starts at a timeline time in seconds like every clip, but its length and its notes are in beats, so they follow
/// the tempo: at 90 bpm a four-bar clip still holds four bars. Notes past its end are kept but not heard.
/// </summary>
public sealed class MidiClip
{
    public string Id { get; set; } = Ids.New("mid");
    /// <summary>Timeline time of its first beat.</summary>
    public double AtSec { get; set; }
    /// <summary>How long it is, in beats (four per bar: the project's grid is always 4/4).</summary>
    public double LengthBeats { get; set; } = DefaultBeats;
    public List<MidiNote> Notes { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>Four bars, what a new clip gets.</summary>
    public const double DefaultBeats = 16;
    /// <summary>The shortest clip (a sixteenth) and the longest one (1024 bars).</summary>
    public const double MinBeats = 0.25, MaxBeats = 4096;

    public double LengthSec(double bpm) => LengthBeats * 60 / Math.Max(1, bpm);
    public double EndSec(double bpm) => AtSec + LengthSec(bpm);

    internal void Normalize()
    {
        AtSec = double.IsFinite(AtSec) ? Math.Max(0, AtSec) : 0;
        LengthBeats = double.IsFinite(LengthBeats) ? Math.Clamp(LengthBeats, MinBeats, MaxBeats) : DefaultBeats;
        Notes ??= [];
        Notes.RemoveAll(n => n == null);
        foreach (var n in Notes) n.Normalize();
        Notes.Sort(MidiNote.Order);
    }
}

/// <summary>
/// A note of a <see cref="MidiClip"/>: its pitch (MIDI number, 60 = C4, the middle C), where it starts and how long it
/// lasts in beats from the clip's start, and how hard it is played (1..127).
/// </summary>
public sealed class MidiNote
{
    public int Pitch { get; set; } = 60;
    public double Beat { get; set; }
    public double Beats { get; set; } = 1;
    public int Velocity { get; set; } = DefaultVelocity;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public const int DefaultVelocity = 100;
    /// <summary>The shortest note: a 1/64.</summary>
    public const double MinBeats = 1.0 / 16;
    public const int MinPitch = 0, MaxPitch = 127;

    public double EndBeat => Beat + Beats;

    /// <summary>The order notes are kept in: by start, then from low to high.</summary>
    public static int Order(MidiNote a, MidiNote b) => a.Beat != b.Beat ? a.Beat.CompareTo(b.Beat) : a.Pitch.CompareTo(b.Pitch);

    public MidiNote Copy() => new() { Pitch = Pitch, Beat = Beat, Beats = Beats, Velocity = Velocity, Extra = Extra };

    internal void Normalize()
    {
        Pitch = Math.Clamp(Pitch, MinPitch, MaxPitch);
        Beat = double.IsFinite(Beat) ? Math.Max(0, Beat) : 0;
        Beats = double.IsFinite(Beats) ? Math.Clamp(Beats, MinBeats, MidiClip.MaxBeats) : 1;
        Velocity = Math.Clamp(Velocity, 1, 127);
    }

    static readonly string[] Names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    /// <summary>"C4", "F#2"… (60 = C4).</summary>
    public static string Name(int pitch) => $"{Names[((pitch % 12) + 12) % 12]}{pitch / 12 - 1}";

    public static bool IsBlack(int pitch) => Names[((pitch % 12) + 12) % 12].Length > 1;

    /// <summary>A pitch from "60", "C4", "c#3" or "Eb2"; null when it isn't one.</summary>
    public static int? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (int.TryParse(s, out int n)) return n is >= MinPitch and <= MaxPitch ? n : null;
        int i = Array.FindIndex(Names, x => x.Length == 1 && char.ToUpperInvariant(s[0]) == x[0]);
        if (i < 0) return null;
        int k = 1;
        if (k < s.Length && s[k] == '#') { i++; k++; }
        else if (k < s.Length && s[k] == 'b') { i--; k++; }
        if (!int.TryParse(s[k..], out int octave)) return null;
        int pitch = (octave + 1) * 12 + i;
        return pitch is >= MinPitch and <= MaxPitch ? pitch : null;
    }
}

/// <summary>
/// The edits of MIDI tracks and clips, for the window and gclab alike (like <see cref="ClipEditing"/> for takes).
/// Every edit leaves the clip normalized (notes in order, inside their limits).
/// </summary>
public static class MidiEditing
{
    /// <summary>A new MIDI track at the end ("MIDI 1", "MIDI 2"…, with the piano icon); returns its id.</summary>
    public static string AddTrack(Project p)
    {
        var used = p.Tracks.Select(t => t.Name).ToHashSet();
        int n = 1;
        while (used.Contains($"MIDI {n}")) n++;
        var track = new Track { Name = $"MIDI {n}", Kind = TrackKinds.Midi, Video = false, Icon = "piano" };
        p.Tracks.Add(track);
        return track.Id;
    }

    public static (Track Track, MidiClip Clip) Find(Project p, string clipId)
    {
        foreach (var t in p.Tracks)
            if (t.MidiClips?.Find(c => c.Id == clipId) is { } c) return (t, c);
        throw new ArgumentException(Texts.T("err.midi.clipNotFound", "No encontré ese clip MIDI."));
    }

    public static MidiClip? TryFind(Project p, string? clipId) => clipId == null ? null : p.Tracks.SelectMany(t => t.MidiClips ?? []).FirstOrDefault(c => c.Id == clipId);

    public static MidiClip AddClip(Project p, string trackId, double atSec, double lengthBeats = MidiClip.DefaultBeats)
    {
        var t = p.Tracks.Find(x => x.Id == trackId) ?? throw new ArgumentException(Texts.T("err.midi.trackNotFound", "No encontré esa pista."));
        if (!t.IsMidi) throw new ArgumentException(Texts.T("err.midi.notMidi", "Esa pista no es MIDI: los clips MIDI van en una pista MIDI."));
        var c = new MidiClip { AtSec = ClipEditing.ToSample(Math.Max(0, atSec)), LengthBeats = lengthBeats };
        c.Normalize();
        (t.MidiClips ??= []).Add(c);
        return c;
    }

    /// <summary>Moves a clip to <paramref name="atSec"/> and gives it <paramref name="lengthBeats"/> (its notes go with it).</summary>
    public static void Place(Project p, string clipId, double atSec, double lengthBeats)
    {
        var (_, c) = Find(p, clipId);
        c.AtSec = ClipEditing.ToSample(Math.Max(0, atSec));
        c.LengthBeats = lengthBeats;
        c.Normalize();
    }

    /// <summary>Moves a clip to another MIDI track; false when that track isn't MIDI.</summary>
    public static bool MoveToTrack(Project p, string clipId, string trackId)
    {
        var (from, c) = Find(p, clipId);
        if (p.Tracks.Find(t => t.Id == trackId) is not { IsMidi: true } to) return false;
        if (to == from) return true;
        from.MidiClips?.Remove(c);
        (to.MidiClips ??= []).Add(c);
        return true;
    }

    public static void RemoveClip(Project p, string clipId)
    {
        foreach (var t in p.Tracks) t.MidiClips?.RemoveAll(c => c.Id == clipId);
    }

    /// <summary>Moves a clip to <paramref name="atSec"/>, its length as it is.</summary>
    public static void Move(Project p, string clipId, double atSec)
    {
        var (_, c) = Find(p, clipId);
        c.AtSec = ClipEditing.ToSample(Math.Max(0, atSec));
    }

    /// <summary>Removes a take's clip or a MIDI clip, whichever the id is.</summary>
    public static void RemoveAny(Project p, string clipId)
    {
        if (TryFind(p, clipId) != null) RemoveClip(p, clipId);
        else ClipEditing.RemoveClip(p, clipId);
    }

    /// <summary>
    /// Duplicates takes' clips and MIDI clips together: every copy goes the same time later, right after the block they
    /// all make, each on its own track. Returns the copies' ids in the order of <paramref name="ids"/>.
    /// </summary>
    public static List<string> DuplicateMany(Project p, IReadOnlyList<string> ids, Func<Clip, double?>? lengthOf = null)
    {
        var unique = ids.Distinct().ToList();
        var midi = unique.Where(id => TryFind(p, id) != null).ToList();
        var takes = unique.Where(id => !midi.Contains(id)).ToList();
        double start = double.MaxValue, end = double.MinValue;
        if (takes.Count > 0) (start, end) = ClipEditing.Span(p, takes.Select(id => ClipEditing.Find(p, id).Clip), lengthOf);
        foreach (var id in midi)
        {
            var c = TryFind(p, id)!;
            start = Math.Min(start, c.AtSec);
            end = Math.Max(end, c.EndSec(p.Settings.Bpm));
        }
        if (start > end) return [];
        double shift = end - start;
        var copies = new Dictionary<string, string>();
        var takeCopies = takes.Count > 0 ? ClipEditing.DuplicateMany(p, takes, lengthOf, shift) : [];
        for (int i = 0; i < takes.Count; i++) copies[takes[i]] = takeCopies[i];
        foreach (var id in midi)
        {
            var (t, c) = Find(p, id);
            var copy = new MidiClip { AtSec = ClipEditing.ToSample(c.AtSec + shift), LengthBeats = c.LengthBeats, Notes = [.. c.Notes.Select(n => n.Copy())], Extra = c.Extra };
            t.MidiClips!.Insert(t.MidiClips.IndexOf(c) + 1, copy);
            copies[id] = copy.Id;
        }
        return [.. unique.Where(copies.ContainsKey).Select(id => copies[id])];
    }

    /// <summary>A copy right after the clip, on the same track; returns the copy's id.</summary>
    public static string Duplicate(Project p, string clipId)
    {
        var (t, c) = Find(p, clipId);
        var copy = new MidiClip
        {
            AtSec = ClipEditing.ToSample(c.EndSec(p.Settings.Bpm)),
            LengthBeats = c.LengthBeats,
            Notes = [.. c.Notes.Select(n => n.Copy())],
            Extra = c.Extra,
        };
        (t.MidiClips ??= []).Add(copy);
        return copy.Id;
    }

    /// <summary>Puts <paramref name="notes"/> in the clip in place of the ones it had (the piano roll's edits).</summary>
    public static void SetNotes(Project p, string clipId, IEnumerable<MidiNote> notes)
    {
        var (_, c) = Find(p, clipId);
        c.Notes = [.. notes.Select(n => n.Copy())];
        c.Normalize();
    }

    public static MidiNote AddNote(Project p, string clipId, int pitch, double beat, double beats = 1, int velocity = MidiNote.DefaultVelocity)
    {
        var (_, c) = Find(p, clipId);
        var n = new MidiNote { Pitch = pitch, Beat = beat, Beats = beats, Velocity = velocity };
        c.Notes.Add(n);
        c.Normalize();
        return n;
    }

    /// <summary>The end of the last MIDI clip of the project, in seconds (0 without any).</summary>
    public static double EndSec(Project p) =>
        p.Tracks.SelectMany(t => t.MidiClips ?? []).Select(c => c.EndSec(p.Settings.Bpm)).DefaultIfEmpty(0).Max();
}
