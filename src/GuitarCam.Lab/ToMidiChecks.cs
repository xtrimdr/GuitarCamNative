using GuitarCam.Audio;
using GuitarCam.Projects;
using NAudio.Midi;

namespace GuitarCam.Lab;

/// <summary>
/// gclab extraer-midi (the notes of a file, like "Extraer MIDI" on a clip) and the rows of comprobar-dsp for
/// <see cref="AudioToMidi"/>: plucked strings (Karplus–Strong, close enough to a guitar for the model) playing a
/// riff, strummed chords, chugs, a tremolo and a melody that rings over itself, scored like transcription papers do (a note is found
/// when its pitch matches and it starts within 50 ms), plus a 44.1 kHz file, silence and the clip it makes.
/// </summary>
static class ToMidiChecks
{
    const int Sr = 48000;
    /// <summary>How far a found note may start from the played one (mir_eval's default).</summary>
    const double OnsetToleranceSec = 0.05;

    /// <summary>gclab extraer-midi --comprobar (only its rows of comprobar-dsp), or extraer-midi archivo [--melodia] [--desde s] [--hasta s] [--inicio 0.5] [--nota 0.3] [--min-ms 128] [--midi x.mid] [--bpm 120].</summary>
    public static int Run(Args a)
    {
        if (a.Flag("comprobar")) return Check() ? 0 : 3;
        string file = a.Positional.ElementAtOrDefault(0) ?? throw new ArgumentException("extraer-midi: falta el archivo.");
        var audio = AudioLoader.Load(file, Sr);
        int from = (int)(a.Double("desde", 0) * audio.SampleRate);
        int to = a.Get("hasta") != null ? (int)(a.Double("hasta", 0) * audio.SampleRate) : audio.Left.Length;
        var settings = new AudioToMidi.Settings(a.Flag("melodia") ? AudioToMidi.Mode.Melody : AudioToMidi.Mode.Chords,
            a.Double("inicio", AudioToMidi.DefaultOnset), a.Double("nota", AudioToMidi.DefaultFrame), a.Double("min-ms", AudioToMidi.DefaultMinNoteMs));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var notes = AudioToMidi.Extract(audio.Left, audio.Right, audio.SampleRate, from, to - from, settings);
        sw.Stop();
        double offset = from / (double)audio.SampleRate;
        if (!a.Flag("callado"))
            foreach (var n in notes)
                Console.WriteLine($"{n.StartSec + offset,8:0.000} s  {n.EndSec - n.StartSec,6:0.000} s  {MidiNote.Name(n.Pitch),-4} ({n.Pitch,3})  fuerza {n.Velocity,3}");
        if (a.Get("verdad") is { } truthFile)
        {
            // [[start s, length s, pitch], …] of the whole file, as datasets and the checks write them
            var truth = System.Text.Json.JsonSerializer.Deserialize<double[][]>(File.ReadAllText(truthFile))!
                .Select(x => (At: x[0] - offset, Len: x[1], Pitch: (int)x[2])).Where(x => x.At >= 0 && x.At < (to - from) / (double)audio.SampleRate).ToList();
            var (f, err) = Score(truth, notes);
            var (precision, recall) = PrecisionRecall(truth, notes);
            Console.WriteLine($"Contra la verdad: F {f:0.00} (precisión {precision:0.00}, encontradas {recall:0.00}), error de inicio {err * 1000:0} ms.");
        }
        double seconds = (to - from) / (double)audio.SampleRate;
        Console.WriteLine($"{notes.Count} notas en {seconds:0.0} s de audio ({sw.ElapsedMilliseconds} ms, {(settings.Mode == AudioToMidi.Mode.Melody ? "melodía" : "acordes")}).");
        if (a.Get("midi") is { } midi)
        {
            Write(midi, notes, a.Double("bpm", 120));
            Console.WriteLine($"MIDI: {midi}");
        }
        return 0;
    }

    /// <summary>A type-1 .mid file with the notes on one track, at <paramref name="bpm"/>.</summary>
    static void Write(string path, IReadOnlyList<AudioToMidi.Note> notes, double bpm)
    {
        const int Ppq = 480;
        var events = new MidiEventCollection(1, Ppq);
        events.AddTrack();
        events.AddEvent(new TempoEvent((int)Math.Round(60_000_000 / bpm), 0), 0);
        long Tick(double sec) => (long)Math.Round(sec * bpm / 60 * Ppq);
        foreach (var n in notes)
        {
            long on = Tick(n.StartSec);
            var e = new NoteOnEvent(on, 1, n.Pitch, n.Velocity, (int)Math.Max(1, Tick(n.EndSec) - on));
            events.AddEvent(e, 0);
            events.AddEvent(e.OffEvent, 0);
        }
        events.PrepareForExport();
        MidiFile.Export(path, events);
    }

    /// <summary>Prints its rows; true when all of them pass (or the model isn't there: it says so and skips).</summary>
    public static bool Check()
    {
        bool allOk = true;
        void Row(string name, string got, string want, bool ok)
        {
            allOk &= ok;
            Console.WriteLine($"{name,-36} {got,12} {want,10}  {(ok ? "igual" : "DISTINTO")}");
        }
        if (!AudioToMidi.Available)
        {
            Row("Extraer MIDI: el modelo", "no está", "está", false);
            Console.WriteLine("  Falta lib\\basic-pitch\\nmp.onnx: corré tools\\get-basic-pitch.ps1 y compilá de nuevo.");
            return false;
        }

        // a riff, one note at a time (E minor pentatonic, low and high)
        int[] riff = [40, 43, 45, 47, 50, 52, 55, 57, 59, 62, 64, 67, 69, 72, 76, 79];
        var played = riff.Select((p, i) => (At: 0.3 + i * 0.35, Len: 0.3, Pitch: p)).ToList();
        var (l, r) = Pluck(played, 7, Sr);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var found = AudioToMidi.Extract(l, r, Sr, 0, l.Length, new(AudioToMidi.Mode.Melody));
        sw.Stop();
        var (f, timing) = Score(played, found);
        Row("Extraer MIDI: riff (melodía)", $"F {f:0.00}", ">= 0.95", f >= 0.95);
        Row("Extraer MIDI: a tiempo (riff)", $"{timing * 1000:0} ms", "<= 25 ms", timing <= 0.025);
        Row("Extraer MIDI: más rápido que el audio", $"{sw.Elapsed.TotalSeconds:0.00} s", $"< {l.Length / (double)Sr:0.0} s", sw.Elapsed.TotalSeconds < l.Length / (double)Sr);
        // in chords mode the overtones an octave up may stay (a played octave sounds the same), but no note is lost
        var (_, recall) = PrecisionRecall(played, AudioToMidi.Extract(l, r, Sr, 0, l.Length));
        Row("Extraer MIDI: riff (acordes), todas", $"{recall:0.00}", ">= 0.95", recall >= 0.95);

        // strummed chords: E, A, G, D (open shapes), 12 ms between strings, two bars each
        int[][] shapes = [[40, 47, 52, 56, 59, 64], [45, 52, 57, 61, 64], [43, 47, 50, 55, 59, 67], [50, 57, 62, 66]];
        var strum = new List<(double At, double Len, int Pitch)>();
        for (int c = 0; c < 8; c++)
        {
            var shape = shapes[c % shapes.Length];
            for (int s = 0; s < shape.Length; s++) strum.Add((0.4 + c * 1.2 + s * 0.012, 1.1, shape[s]));
        }
        (l, r) = Pluck(strum, 11, Sr);
        var chords = AudioToMidi.Extract(l, r, Sr, 0, l.Length);
        (f, timing) = Score(strum, chords);
        var (precision, _) = PrecisionRecall(strum, chords);
        Row("Extraer MIDI: acordes rasgueados", $"F {f:0.00}", ">= 0.85", f >= 0.85 && timing <= 0.025);
        Row("Extraer MIDI: acordes, sin notas de más", $"{precision:0.00}", ">= 0.9", precision >= 0.9);

        // fast ones: palm-muted chugs on the low E in 16ths at 120 BPM, and a tremolo at 11 notes a second
        foreach (var (name, gap) in new[] { ("chugs en semicorcheas", 0.125), ("trémolo a 90 ms", 0.09) })
        {
            var fast = Enumerable.Range(0, 24).Select(i => (At: 0.3 + i * gap, Len: gap - 0.01, Pitch: name[0] == 'c' ? 40 : 64)).ToList();
            (l, r) = Pluck(fast, 3, Sr);
            (f, _) = Score(fast, AudioToMidi.Extract(l, r, Sr, 0, l.Length, new(AudioToMidi.Mode.Melody)));
            Row($"Extraer MIDI: {name}", $"F {f:0.00}", ">= 0.9", f >= 0.9);
        }

        // a melody whose notes ring over the next one: in Melody mode only the melody comes out
        var legato = new[] { 64, 67, 69, 71, 69, 67, 64, 62, 64 }.Select((p, i) => (At: 0.3 + i * 0.4, Len: 0.7, Pitch: p)).ToList();
        (l, r) = Pluck(legato, 5, Sr);
        var melody = AudioToMidi.Extract(l, r, Sr, 0, l.Length, new(AudioToMidi.Mode.Melody));
        (f, _) = Score(legato, melody);
        bool alone = melody.Zip(melody.Skip(1)).All(x => x.First.EndSec <= x.Second.StartSec + 1e-9);
        Row("Extraer MIDI: melodía que suena encima", $"F {f:0.00}", ">= 0.9", f >= 0.9);
        Row("Extraer MIDI: melodía de a una nota", alone ? "sí" : "se pisan", "sí", alone);

        // 44.1 kHz, as an imported file can be, from the middle of the audio
        const int Sr44 = 44100;
        var (l44, r44) = Pluck(played, 7, Sr44);
        int from = (int)(2.0 * Sr44);
        var part = AudioToMidi.Extract(l44, r44, Sr44, from, l44.Length - from, new(AudioToMidi.Mode.Melody));
        var wanted = played.Where(x => x.At >= 2.0).Select(x => (At: x.At - 2.0, x.Len, x.Pitch)).ToList();
        (f, timing) = Score(wanted, part);
        Row("Extraer MIDI: 44,1 kHz y desde 2 s", $"F {f:0.00}", ">= 0.95", f >= 0.95 && timing <= 0.025);

        var quiet = new float[Sr * 4];
        Row("Extraer MIDI: silencio", $"{AudioToMidi.Extract(quiet, quiet, Sr, 0, quiet.Length).Count} notas", "0", AudioToMidi.Extract(quiet, quiet, Sr, 0, quiet.Length).Count == 0);

        // the clip it makes: under the take's track, in its folder, the same stretch, the notes on the beat
        var p = new Project();
        p.Settings.Bpm = 120;
        string take = ClipEditing.AddEmpty(p, false), after = ClipEditing.AddEmpty(p, false);
        string folder = TrackFolders.Add(p, "Guitarras", [take, after]);
        var (tid, clip) = MidiEditing.AddExtracted(p, null, take, 2.0, 6.0, [new(0.5, 1.0, 64, 90), new(1.0, 1.5, 67, 80)], "Guitarra (MIDI)");
        var t = p.Tracks.Find(x => x.Id == tid)!;
        bool placed = p.Tracks.IndexOf(t) == p.Tracks.FindIndex(x => x.Id == take) + 1 && t.FolderId == folder && t.IsMidi && t.Name == "Guitarra (MIDI)";
        bool beats = Math.Abs(clip.AtSec - 2.0) < 1e-6 && Math.Abs(clip.LengthBeats - 8) < 1e-6
            && Math.Abs(clip.Notes[0].Beat - 1) < 1e-6 && Math.Abs(clip.Notes[1].Beat - 2) < 1e-6 && Math.Abs(clip.Notes[0].Beats - 1) < 1e-6;
        Row("Extraer MIDI: pista nueva debajo", placed ? "debajo" : "en otro lado", "debajo", placed);
        Row("Extraer MIDI: el clip y sus tiempos", beats ? "a tiempo" : "corrido", "a tiempo", beats);
        return allOk;
    }

    /// <summary>
    /// Note F-measure on onsets (pitch equal, start within <see cref="OnsetToleranceSec"/>, each played note found
    /// once) and the mean start error of the ones found.
    /// </summary>
    static (double F, double MeanErrorSec) Score(IReadOnlyList<(double At, double Len, int Pitch)> played, IReadOnlyList<AudioToMidi.Note> found)
    {
        var (hits, err) = Match(played, found);
        if (hits == 0) return (0, double.PositiveInfinity);
        return (2.0 * hits / (found.Count + played.Count), err / hits);
    }

    /// <summary>Precision (how many found notes were played) and recall (how many played notes were found).</summary>
    static (double Precision, double Recall) PrecisionRecall(IReadOnlyList<(double At, double Len, int Pitch)> played, IReadOnlyList<AudioToMidi.Note> found)
    {
        int hits = Match(played, found).Hits;
        return (found.Count == 0 ? 0 : hits / (double)found.Count, hits / (double)played.Count);
    }

    static (int Hits, double ErrorSec) Match(IReadOnlyList<(double At, double Len, int Pitch)> played, IReadOnlyList<AudioToMidi.Note> found)
    {
        var used = new bool[found.Count];
        int hits = 0;
        double err = 0;
        foreach (var x in played)
        {
            int best = -1;
            for (int i = 0; i < found.Count; i++)
                if (!used[i] && found[i].Pitch == x.Pitch && Math.Abs(found[i].StartSec - x.At) <= OnsetToleranceSec
                    && (best < 0 || Math.Abs(found[i].StartSec - x.At) < Math.Abs(found[best].StartSec - x.At)))
                    best = i;
            if (best < 0) continue;
            used[best] = true;
            hits++;
            err += Math.Abs(found[best].StartSec - x.At);
        }
        return (hits, err);
    }

    /// <summary>Plucked strings (Karplus–Strong: a noise burst through a damped, averaged delay of one period), each
    /// note muted after its length with a short fade, as a guitarist lifting the finger.</summary>
    static (float[] L, float[] R) Pluck(IReadOnlyList<(double At, double Len, int Pitch)> notes, int seed, int sr)
    {
        var rng = new Random(seed);
        double end = notes.Max(n => n.At + n.Len) + 0.5;
        var x = new float[(int)(end * sr)];
        foreach (var (at, len, pitch) in notes)
        {
            double hz = 440 * Math.Pow(2, (pitch - 69) / 12.0);
            // the two-point average delays half a sample; the fractional rest goes through a first-order allpass
            double period = sr / hz - 0.5;
            int n = (int)Math.Floor(period);
            double frac = period - n;
            double c = (1 - frac) / (1 + frac);
            var line = new double[n];
            double lp = 0;
            for (int i = 0; i < n; i++)
            {
                lp = 0.5 * lp + 0.5 * (rng.NextDouble() * 2 - 1);
                line[i] = lp;
            }
            double amp = 0.25 * (0.7 + 0.3 * rng.NextDouble());
            int start = (int)Math.Round(at * sr), stop = (int)Math.Round((at + len) * sr), fade = sr / 100;
            double prev = 0, apIn = 0, apOut = 0;
            for (int i = 0, k = 0; start + i < x.Length && i < stop - start + fade; i++, k = (k + 1) % n)
            {
                double y = line[k];
                double avg = 0.996 * 0.5 * (y + prev);
                prev = y;
                double ap = c * avg + apIn - c * apOut;
                apIn = avg;
                apOut = ap;
                line[k] = ap;
                double gain = i < stop - start ? 1 : 1 - (i - (stop - start)) / (double)fade;
                x[start + i] += (float)(amp * y * gain);
            }
        }
        return (x, (float[])x.Clone());
    }
}
