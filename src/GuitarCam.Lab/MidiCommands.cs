using System.Globalization;
using GuitarCam.Audio.Dsp;
using GuitarCam.Projects;

namespace GuitarCam.Lab;

/// <summary>
/// MIDI tracks, clips and notes without the window (stage 1 of the MIDI editor), and the checks of the notes' sound:
/// the test tone is the same in any block size and from any position, and the export plays what the live engine does.
/// <c>gclab mezclar carpeta</c> renders a project with its MIDI tracks.
/// </summary>
static class MidiCommands
{
    public static int Run(Args a)
    {
        string sub = a.Positional.FirstOrDefault()?.ToLowerInvariant() ?? "";
        if (sub == "comprobar") return Check() ? 0 : 3;
        string? dir = a.Positional.Skip(1).FirstOrDefault();
        if (dir == null) throw new ArgumentException("midi ver|pista|clip|nota|acordes|quitar <carpeta> (o midi comprobar)");
        using var store = ProjectStore.Open(Path.GetFullPath(dir));
        var p = store.Project;
        if (sub != "ver" && store.ReadOnly) throw new IOException(store.ReadOnlyReason);
        switch (sub)
        {
            case "ver": break;
            case "pista":
                Console.WriteLine(MidiEditing.AddTrack(p));
                break;
            case "clip":
            {
                var t = TrackOf(p, a.Get("pista")) ?? throw new ArgumentException(a.Get("pista") == null
                    ? "No hay pistas MIDI: agregá una con midi pista."
                    : $"No encontré la pista {a.Get("pista")} (--pista: su número, contando todas, o su id).");
                var c = MidiEditing.AddClip(p, t.Id, a.Double("en", 0), a.Double("compases", MidiClip.DefaultBeats / 4) * 4);
                Console.WriteLine(c.Id);
                break;
            }
            case "nota":
            {
                string clip = a.Get("clip") ?? throw new ArgumentException("Falta --clip.");
                int pitch = MidiNote.Parse(a.Get("altura", "60")) ?? throw new ArgumentException("--altura: un número de 0 a 127 o una nota como C4 o F#2.");
                MidiEditing.AddNote(p, clip, pitch, a.Double("pulso", 0), a.Double("largo", 1), a.Int("vel", MidiNote.DefaultVelocity));
                break;
            }
            case "acordes":
            {
                // C, G, Am, F: one per bar, as a quick backing track to hear the tone and the tempo
                string clip = a.Get("clip") ?? throw new ArgumentException("Falta --clip.");
                int[][] chords = [[48, 60, 64, 67], [43, 59, 62, 67], [45, 60, 64, 69], [41, 60, 65, 69]];
                var notes = new List<MidiNote>();
                for (int bar = 0; bar < chords.Length; bar++)
                    foreach (int pitch in chords[bar]) notes.Add(new MidiNote { Pitch = pitch, Beat = bar * 4, Beats = 3.75, Velocity = 90 });
                MidiEditing.SetNotes(p, clip, notes);
                break;
            }
            case "quitar":
                MidiEditing.RemoveClip(p, a.Get("clip") ?? throw new ArgumentException("Falta --clip."));
                break;
            default:
                throw new ArgumentException("midi ver|pista|clip|nota|acordes|quitar <carpeta>");
        }
        if (sub != "ver")
        {
            p.AddLog("midi-edited", sub);
            store.Save();
        }
        Show(p);
        return 0;
    }

    /// <summary>A MIDI track by its number among all tracks (1 = the first) or its id.</summary>
    static Track? TrackOf(Project p, string? key)
    {
        if (key == null) return p.Tracks.FirstOrDefault(t => t.IsMidi);
        if (int.TryParse(key, out int n)) return n >= 1 && n <= p.Tracks.Count ? p.Tracks[n - 1] : null;
        return p.Tracks.Find(t => t.Id == key);
    }

    static void Show(Project p)
    {
        double bpm = p.Settings.Bpm;
        var midi = p.Tracks.Where(t => t.IsMidi).ToList();
        if (midi.Count == 0) { Console.WriteLine("Sin pistas MIDI."); return; }
        foreach (var t in midi)
        {
            Console.WriteLine($"{p.Tracks.IndexOf(t) + 1}. {t.Id} · {t.Name}");
            foreach (var c in t.MidiClips ?? [])
            {
                Console.WriteLine($"   {c.Id} · {c.AtSec:0.000}–{c.EndSec(bpm):0.000} s · {c.LengthBeats / 4:0.##} compases · {c.Notes.Count} notas");
                foreach (var n in c.Notes)
                    Console.WriteLine($"      {MidiNote.Name(n.Pitch),-4} pulso {n.Beat,6:0.###} · largo {n.Beats:0.###} · vel {n.Velocity}");
            }
        }
    }

    /// <summary>The notes' sound: block size and position don't change it, the release ends, and the export matches.</summary>
    public static bool Check()
    {
        const int sr = 48000;
        bool ok = true;
        var seq = new NoteSequence(
        [
            new NoteEvent(0, sr / 2, NoteSynth.Hz(60), NoteSynth.Gain(100)),
            new NoteEvent(sr / 4, sr, NoteSynth.Hz(64), NoteSynth.Gain(80)),
            new NoteEvent(sr, sr / 3, NoteSynth.Hz(67), NoteSynth.Gain(127)),
        ], sr);
        int len = (int)seq.End + 1000;

        float[] Whole(int block)
        {
            var l = new float[len];
            var r = new float[len];
            for (int at = 0; at < len; at += block)
            {
                int n = Math.Min(block, len - at);
                NoteSynth.Render(seq, at, n, sr, l.AsSpan(at, n), r.AsSpan(at, n));
            }
            return l;
        }
        var a = Whole(37);
        var b = Whole(4096);
        double diff = 0, peak = 0;
        for (int i = 0; i < len; i++)
        {
            diff = Math.Max(diff, Math.Abs(a[i] - b[i]));
            peak = Math.Max(peak, Math.Abs(a[i]));
        }
        bool sameOk = diff == 0;
        ok &= sameOk;
        Console.WriteLine($"{"MIDI: igual en bloques de 37 y 4096",-36} {diff,12:0.0e0} {"0",10}  {(sameOk ? "igual" : "DISTINTO")}");

        // silent before the first note, past every release, and never above 0 dBFS with a chord of three
        bool tailOk = a[(int)seq.End + 10] == 0 && peak is > 0.05 and < 1;
        ok &= tailOk;
        Console.WriteLine($"{"MIDI: termina después del release",-36} {peak,12:0.000} {"< 1",10}  {(tailOk ? "igual" : "DISTINTO")}");

        // the export (MixRenderer) plays the notes through the same chain as live playback
        var mix = MixRenderer.Render([new MixTrackSpec { Notes = seq, Inserts = [] }], new MixBusSpec(), len / (double)sr, sr, limit: false);
        double mixDiff = 0;
        for (int i = 0; i < len; i++) mixDiff = Math.Max(mixDiff, Math.Abs(mix.Left[i] - a[i]));
        bool mixOk = mixDiff < 1e-6;
        ok &= mixOk;
        Console.WriteLine($"{"MIDI: la exportación suena igual",-36} {mixDiff,12:0.0e0} {"< 1e-6",10}  {(mixOk ? "igual" : "DISTINTO")}");

        // the project's clips become notes in frames: tempo, clip start and the clip's end cutting a long note
        var p = new Project();
        p.Settings.Bpm = 120;
        string tid = MidiEditing.AddTrack(p);
        var clip = MidiEditing.AddClip(p, tid, 1, 4);
        MidiEditing.AddNote(p, clip.Id, 69, 1, 8);
        MidiEditing.AddNote(p, clip.Id, 72, 5);
        var built = ChannelDsp.Notes(p.Tracks[0], 120, sr);
        bool convOk = built is { Notes: [var only] } && only.Start == (long)(1.5 * sr) && only.Length == (long)(1.5 * sr)
            && Math.Abs(only.Hz - 440) < 1e-9;
        ok &= convOk;
        Console.WriteLine($"{"MIDI: clip a notas (tempo y cortes)",-36} {built?.Notes.Length ?? 0,12} {"1",10}  {(convOk ? "igual" : "DISTINTO")}");

        bool parseOk = MidiNote.Parse("C4") == 60 && MidiNote.Parse("f#2") == 42 && MidiNote.Parse("Eb3") == 51 && MidiNote.Name(61) == "C#4"
            && MidiNote.Parse("H2") == null;
        ok &= parseOk;
        Console.WriteLine($"{"MIDI: nombres de notas",-36} {"C4 = 60",12} {"",10}  {(parseOk ? "igual" : "DISTINTO")}");
        return ok;
    }
}
