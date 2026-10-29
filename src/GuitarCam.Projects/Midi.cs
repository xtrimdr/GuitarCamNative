using GuitarCam.Audio;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GuitarCam.Projects;

/// <summary>
/// What a track holds (native only): takes (<see cref="Audio"/>, the clips of a media) or notes for an instrument
/// (<see cref="Midi"/>, <see cref="Track.MidiClips"/>). A MIDI track never gets a video panel and never records audio:
/// it is for building the backing track the guitar is played over, by hand or from a MIDI keyboard.
/// </summary>
public static class TrackKinds
{
    public const string Audio = "audio", Midi = "midi";

    public static string Normalize(string? kind) => kind == Midi ? Midi : Audio;
}

/// <summary>
/// A VST3 plugin a track uses (native only): the file and the class inside it, its name and maker to show, and its
/// state as the plugin saved it (base64; what its window was left at), so the project sounds the same when it opens.
/// The file stays where it was installed: a project opened on a computer without it says so and plays the test tone.
/// </summary>
public sealed class PluginRef
{
    public string Path { get; set; } = "";
    public string ClassId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Vendor { get; set; } = "";
    public string? State { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>False when it doesn't say which plugin it is.</summary>
    internal bool Normalize()
    {
        Path = Path?.Trim() ?? "";
        ClassId = ClassId?.Trim() ?? "";
        Name = string.IsNullOrWhiteSpace(Name) ? System.IO.Path.GetFileNameWithoutExtension(Path) : Name.Trim();
        Vendor = Vendor?.Trim() ?? "";
        if (string.IsNullOrEmpty(State)) State = null;
        return Path.Length > 0 && ClassId.Length > 0;
    }

    /// <summary>The same plugin (not the same state).</summary>
    public bool SamePlugin(PluginRef? o) => o != null && string.Equals(o.Path, Path, StringComparison.OrdinalIgnoreCase) && o.ClassId == ClassId;
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

    /// <summary>A copy with a new id and its own notes (duplicate, copy and paste).</summary>
    public MidiClip Copy() => new() { AtSec = AtSec, LengthBeats = LengthBeats, Notes = [.. Notes.Select(n => n.Copy())], Extra = Extra };
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
            var copy = c.Copy();
            copy.AtSec = ClipEditing.ToSample(c.AtSec + shift);
            t.MidiClips!.Insert(t.MidiClips.IndexOf(c) + 1, copy);
            copies[id] = copy.Id;
        }
        return [.. unique.Where(copies.ContainsKey).Select(id => copies[id])];
    }

    /// <summary>A copy right after the clip, on the same track; returns the copy's id.</summary>
    public static string Duplicate(Project p, string clipId)
    {
        var (t, c) = Find(p, clipId);
        var copy = c.Copy();
        copy.AtSec = ClipEditing.ToSample(c.EndSec(p.Settings.Bpm));
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

    /// <summary>
    /// Moves where each note starts to the nearest step of <paramref name="gridBeats"/> (the piano roll's magnet) on
    /// the song's grid, not the clip's: a clip that doesn't start on a beat still lands its notes on the beats. The
    /// length stays. <paramref name="which"/> = the notes to move (null = all). Returns how many moved; a grid of 0
    /// (the magnet off) moves none.
    /// </summary>
    public static int Quantize(IList<MidiNote> notes, double clipAtSec, double bpm, double gridBeats, IReadOnlyCollection<MidiNote>? which = null)
    {
        if (gridBeats <= 0) return 0;
        double clipBeat = clipAtSec * Math.Max(1, bpm) / 60;
        int moved = 0;
        foreach (var n in notes)
        {
            if (which != null && !which.Contains(n)) continue;
            double song = clipBeat + n.Beat;
            double snapped = Math.Round(song / gridBeats) * gridBeats;
            // a note pulled before the clip's start stays at its start
            double beat = Math.Max(0, snapped - clipBeat);
            if (Math.Abs(beat - n.Beat) < 1e-9) continue;
            n.Beat = beat;
            moved++;
        }
        return moved;
    }

    /// <summary>
    /// Scales the velocities around their average (asked by the user): 100% leaves them, under it they get closer
    /// (0% = all on the average, an even take), over it further apart (more dynamics). Only <paramref name="which"/>
    /// (null = all), kept in 1..127. Returns how many changed.
    /// </summary>
    public static int ScaleVelocity(IList<MidiNote> notes, double percent, IReadOnlyCollection<MidiNote>? which = null)
    {
        var set = notes.Where(n => which == null || which.Contains(n)).ToList();
        if (set.Count == 0) return 0;
        double mean = set.Average(n => n.Velocity), k = Math.Max(0, percent) / 100;
        int changed = 0;
        foreach (var n in set)
        {
            int v = Math.Clamp((int)Math.Round(mean + (n.Velocity - mean) * k), 1, 127);
            if (v == n.Velocity) continue;
            n.Velocity = v;
            changed++;
        }
        return changed;
    }

    /// <summary>
    /// Humanizes the velocities (asked by the user): each note moves at random up to <paramref name="amount"/> up or
    /// down, kept in 1..127, so a written part does not sound like a machine. Only <paramref name="which"/> (null = all).
    /// Returns how many changed.
    /// </summary>
    public static int HumanizeVelocity(IList<MidiNote> notes, int amount, Random random, IReadOnlyCollection<MidiNote>? which = null)
    {
        amount = Math.Clamp(amount, 0, 127);
        if (amount == 0) return 0;
        int changed = 0;
        foreach (var n in notes)
        {
            if (which != null && !which.Contains(n)) continue;
            int v = Math.Clamp(n.Velocity + random.Next(-amount, amount + 1), 1, 127);
            if (v == n.Velocity) continue;
            n.Velocity = v;
            changed++;
        }
        return changed;
    }

    /// <summary>Quantizes every note of a clip (<see cref="Quantize(IList{MidiNote}, double, double, double, IReadOnlyCollection{MidiNote}?)"/>).</summary>
    public static int Quantize(Project p, string clipId, double gridBeats)
    {
        var (_, c) = Find(p, clipId);
        int moved = Quantize(c.Notes, c.AtSec, p.Settings.Bpm, gridBeats);
        c.Normalize();
        return moved;
    }

    /// <summary>
    /// A clip made from notes played on a keyboard (start and length in seconds on the timeline, pitch, velocity), on
    /// a MIDI track: it starts on the bar where the take began (<paramref name="fromSec"/>, the count-in's notes come
    /// in there) and ends on the bar after the take (<paramref name="toSec"/>), so it lines up with the song. With
    /// <paramref name="gridBeats"/> &gt; 0 the notes are quantized to it. Null when nothing was played.
    /// </summary>
    public static MidiClip? AddRecorded(Project p, string trackId, double fromSec, double toSec,
        IReadOnlyList<(double Sec, double LengthSec, int Pitch, int Velocity)> played, double gridBeats = 0)
    {
        if (played.Count == 0) return null;
        var t = p.Tracks.Find(x => x.Id == trackId) ?? throw new ArgumentException(Texts.T("err.midi.trackNotFound", "No encontré esa pista."));
        if (!t.IsMidi) throw new ArgumentException(Texts.T("err.midi.notMidi", "Esa pista no es MIDI: los clips MIDI van en una pista MIDI."));
        double bpm = Math.Max(1, p.Settings.Bpm);
        double barSec = 4 * 60 / bpm;
        double start = Math.Floor(Math.Max(0, fromSec) / barSec + 1e-9) * barSec;
        double end = Math.Max(start + barSec, Math.Ceiling(Math.Max(toSec, played.Max(x => x.Sec + x.LengthSec)) / barSec - 1e-9) * barSec);
        var clip = new MidiClip { AtSec = ClipEditing.ToSample(start), LengthBeats = (end - start) / 60 * bpm };
        foreach (var (sec, len, pitch, velocity) in played)
            clip.Notes.Add(new MidiNote
            {
                Pitch = pitch,
                Beat = Math.Max(0, sec - clip.AtSec) / 60 * bpm,
                Beats = Math.Max(MidiNote.MinBeats, len / 60 * bpm),
                Velocity = velocity,
            });
        if (gridBeats > 0) Quantize(clip.Notes, clip.AtSec, bpm, gridBeats);
        clip.Normalize();
        (t.MidiClips ??= []).Add(clip);
        return clip;
    }

    /// <summary>
    /// A clip made from the notes taken out of a take's clip (<see cref="AudioToMidi"/>, "Extraer MIDI"): it covers the
    /// same stretch of the timeline (<paramref name="fromSec"/>..<paramref name="toSec"/>) and its notes, in seconds
    /// from <paramref name="fromSec"/>, land where they were heard. It goes on <paramref name="trackId"/>, a MIDI track,
    /// or (null) on a new one right under <paramref name="belowTrackId"/>, in its folder, named
    /// <paramref name="name"/>. With <paramref name="gridBeats"/> &gt; 0 the notes are quantized to it. Returns the
    /// track and the clip.
    /// </summary>
    public static (string TrackId, MidiClip Clip) AddExtracted(Project p, string? trackId, string belowTrackId, double fromSec, double toSec,
        IReadOnlyList<AudioToMidi.Note> notes, string? name = null, double gridBeats = 0)
    {
        var below = p.Tracks.Find(x => x.Id == belowTrackId) ?? throw new ArgumentException(Texts.T("err.midi.trackNotFound", "No encontré esa pista."));
        Track t;
        if (trackId != null)
        {
            t = p.Tracks.Find(x => x.Id == trackId) ?? throw new ArgumentException(Texts.T("err.midi.trackNotFound", "No encontré esa pista."));
            if (!t.IsMidi) throw new ArgumentException(Texts.T("err.midi.notMidi", "Esa pista no es MIDI: los clips MIDI van en una pista MIDI."));
        }
        else
        {
            string made = AddTrack(p);
            t = p.Tracks.Find(x => x.Id == made)!;
            if (!string.IsNullOrWhiteSpace(name) && p.Tracks.All(x => x.Name != name)) t.Name = name;
            var seq = TrackFolders.Sequence(p);
            seq.Remove(t);
            TrackFolders.Move(p, t.Id, seq.IndexOf(below) + 1, below.FolderId);
        }
        double bpm = Math.Max(1, p.Settings.Bpm);
        var clip = new MidiClip { AtSec = ClipEditing.ToSample(Math.Max(0, fromSec)) };
        double shift = fromSec - clip.AtSec;
        clip.LengthBeats = Math.Max(toSec - clip.AtSec, notes.Select(n => n.EndSec + shift).DefaultIfEmpty(0).Max()) / 60 * bpm;
        foreach (var n in notes)
            clip.Notes.Add(new MidiNote
            {
                Pitch = n.Pitch,
                Beat = Math.Max(0, n.StartSec + shift) / 60 * bpm,
                Beats = Math.Max(MidiNote.MinBeats, (n.EndSec - n.StartSec) / 60 * bpm),
                Velocity = n.Velocity,
            });
        if (gridBeats > 0) Quantize(clip.Notes, clip.AtSec, bpm, gridBeats);
        clip.Normalize();
        (t.MidiClips ??= []).Add(clip);
        return (t.Id, clip);
    }

    /// <summary>The end of the last MIDI clip of the project, in seconds (0 without any).</summary>
    public static double EndSec(Project p) =>
        p.Tracks.SelectMany(t => t.MidiClips ?? []).Select(c => c.EndSec(p.Settings.Bpm)).DefaultIfEmpty(0).Max();
}
