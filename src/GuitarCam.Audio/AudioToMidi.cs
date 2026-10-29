using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace GuitarCam.Audio;

/// <summary>
/// Takes the notes out of a piece of audio ("Extraer MIDI", asked by the user: like Studio Pro's audio to notes) with
/// Spotify's Basic Pitch (Bittner et al., ICASSP 2022; the model is Apache 2.0, <c>tools/get-basic-pitch.ps1</c>): a
/// small network that hears several notes at once (chords, arpeggios) and was trained on guitar among other things.
/// It runs on the CPU through ONNX Runtime. The audio goes in as the model was trained (mono, 22.05 kHz, 2 s windows
/// that overlap 30 frames) and what comes out (how likely each piano key sounds, and starts, every ~11.6 ms) becomes
/// notes the way Basic Pitch's own <c>output_to_notes_polyphonic</c> does (checked note for note against Spotify's
/// code), with its thresholds (lower ones added more wrong notes than they found) but a shorter shortest note, so 16ths
/// and palm-muted chugs come out. On top of that a doubled onset is one attack (<c>Echo</c> in <see cref="Decode"/>),
/// the ghosts the model hears under a ringing low string are dropped (<see cref="WithoutGhosts"/>), and
/// <see cref="Mode.Melody"/> keeps one note at a time (the strongest), for solos and riffs where an overtone would
/// otherwise add notes. <c>gclab extraer-midi --verdad</c> scores it against known notes.
/// Offline only (allocates, runs on a worker thread); the model is loaded once and shared.
/// </summary>
public static class AudioToMidi
{
    public enum Mode { Chords, Melody }

    /// <param name="StartSec">From the start of the stretch that was given.</param>
    /// <param name="Velocity">1..127, from how clearly the model hears it.</param>
    public readonly record struct Note(double StartSec, double EndSec, int Pitch, int Velocity);

    /// <param name="Onset">How sure the model has to be that a note starts (lower = more notes).</param>
    /// <param name="Frame">How sure it has to be that a note still sounds (lower = longer notes, more of them).</param>
    /// <param name="MinNoteMs">Shorter notes are dropped.</param>
    public sealed record Settings(Mode Mode = Mode.Chords, double Onset = DefaultOnset, double Frame = DefaultFrame,
        double MinNoteMs = DefaultMinNoteMs, int MinPitch = 0, int MaxPitch = 127);

    /// <summary>Basic Pitch's thresholds. Its shortest note (127.7 ms) dropped 16ths from 120 BPM on and every chug but
    /// the last; 70 ms keeps them (16ths up to ~210 BPM) without more wrong notes on chords and riffs.</summary>
    public const double DefaultOnset = 0.5, DefaultFrame = 0.3, DefaultMinNoteMs = 70;

    /// <summary>The model's input: 22.05 kHz, a frame every 256 samples (86 a second, as the model's constants round
    /// it), 2 s windows (one hop short) that overlap 30 frames; half of the overlap is cut from each side.</summary>
    const int ModelRate = 22050, Hop = 256;
    const int FramesPerSec = ModelRate / Hop;
    const int WindowFrames = FramesPerSec * 2;
    const int WindowSamples = ModelRate * 2 - Hop;
    const int OverlapFrames = 30;
    const int HopSamples = WindowSamples - OverlapFrames * Hop;
    const int KeptFrames = WindowFrames - OverlapFrames;
    /// <summary>The 88 keys of a piano, from A0 (MIDI 21).</summary>
    const int Keys = 88, LowestPitch = 21;
    /// <summary>Windows run together, when the model takes batches.</summary>
    const int Batch = 8;
    /// <summary>Frames a note may dip under <see cref="Settings.Frame"/> and still go on.</summary>
    const int EnergyTolerance = 11;
    /// <summary>Basic Pitch's own correction so its frame times line up with the audio.</summary>
    const double AlignmentOffset = 0.0018;
    const string InputName = "serving_default_input_2:0", NoteName = "StatefulPartitionedCall:1", OnsetName = "StatefulPartitionedCall:2";

    /// <summary>Where the model goes: next to the program (<c>basic-pitch\nmp.onnx</c>).</summary>
    public static string ModelPath => Path.Combine(AppContext.BaseDirectory, "basic-pitch", "nmp.onnx");
    public static bool Available => File.Exists(ModelPath);

    static readonly Lock sessionLock = new();
    static InferenceSession? session;
    static bool batched;

    static InferenceSession Session()
    {
        lock (sessionLock)
        {
            if (session != null) return session;
            if (!Available) throw new InvalidOperationException(Texts.T("err.toMidi.noModel", "Falta el modelo que saca las notas (basic-pitch\\nmp.onnx). Reinstalá GuitarCam."));
            try
            {
                using var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
                var s = new InferenceSession(ModelPath, options);
                batched = s.InputMetadata.TryGetValue(InputName, out var input) && input.Dimensions.Length > 0 && input.Dimensions[0] < 0;
                return session = s;
            }
            catch (Exception e) when (e is OnnxRuntimeException or DllNotFoundException or TypeInitializationException)
            {
                throw new InvalidOperationException(Texts.T("err.toMidi.engine", "No pude arrancar el extractor de notas: {0}", e.Message), e);
            }
        }
    }

    /// <summary>
    /// The notes of <paramref name="count"/> samples of <paramref name="l"/>/<paramref name="r"/> from
    /// <paramref name="start"/>, in order of start then pitch. <paramref name="progress"/> goes 0..1.
    /// </summary>
    public static List<Note> Extract(float[] l, float[] r, int sampleRate, int start, int count, Settings? settings = null,
        Action<double>? progress = null, CancellationToken ct = default)
    {
        settings ??= new();
        start = Math.Clamp(start, 0, l.Length);
        count = Math.Clamp(count, 0, l.Length - start);
        if (count <= 0) return [];
        var s = Session();

        var mono = new float[count];
        for (int i = 0; i < count; i++) mono[i] = 0.5f * (l[start + i] + r[start + i]);
        var audio = sampleRate == ModelRate ? mono : AudioLoader.Resample(mono, mono, sampleRate, ModelRate).Left;
        ct.ThrowIfCancellationRequested();

        var (frames, onsets, n) = Run(s, audio, progress, ct);
        if (n < 2) return [];
        ConstrainPitch(frames, onsets, n, settings.MinPitch, settings.MaxPitch);
        int minLen = (int)Math.Round(settings.MinNoteMs / 1000 * ModelRate / Hop);
        var events = WithoutGhosts(Decode(frames, onsets, n, settings.Onset, settings.Frame, minLen));
        if (settings.Mode == Mode.Melody) events = OneAtATime(events, minLen);

        double windowShift = (double)Hop / ModelRate * (WindowFrames - (double)WindowSamples / Hop) + AlignmentOffset;
        double Time(int f) => f * (double)Hop / ModelRate - windowShift * (f / WindowFrames);
        var notes = events.Select(e => new Note(Math.Max(0, Time(e.Start)), Time(e.End), e.Pitch,
            Math.Clamp((int)Math.Round(127 * e.Amplitude), 1, 127))).Where(x => x.EndSec > x.StartSec).ToList();
        notes.Sort((a, b) => a.StartSec != b.StartSec ? a.StartSec.CompareTo(b.StartSec) : a.Pitch.CompareTo(b.Pitch));
        progress?.Invoke(1);
        return notes;
    }

    /// <summary>The model over every window; the frames of all of them in a row (n × <see cref="Keys"/>).</summary>
    static (float[] Frames, float[] Onsets, int N) Run(InferenceSession s, float[] audio, Action<double>? progress, CancellationToken ct)
    {
        int pad = OverlapFrames * Hop / 2;
        int padded = pad + audio.Length;
        int windows = (padded + HopSamples - 1) / HopSamples;
        var frames = new float[windows * KeptFrames * Keys];
        var onsets = new float[frames.Length];
        int batch = batched ? Batch : 1;
        var input = new float[batch * WindowSamples];
        for (int w0 = 0; w0 < windows; w0 += batch)
        {
            ct.ThrowIfCancellationRequested();
            int b = Math.Min(batch, windows - w0);
            Array.Clear(input);
            for (int k = 0; k < b; k++)
            {
                // window k starts at (w0 + k) × hop in the padded audio, whose first `pad` samples are silence
                int from = (w0 + k) * HopSamples - pad;
                for (int i = 0; i < WindowSamples; i++)
                {
                    int src = from + i;
                    if ((uint)src < (uint)audio.Length) input[k * WindowSamples + i] = audio[src];
                }
            }
            var tensor = new DenseTensor<float>(input.AsMemory(0, b * WindowSamples), [b, WindowSamples, 1]);
            using var results = s.Run([NamedOnnxValue.CreateFromTensor(InputName, tensor)], [NoteName, OnsetName]);
            foreach (var res in results)
            {
                var dst = res.Name == NoteName ? frames : onsets;
                var t = res.AsTensor<float>();
                for (int k = 0; k < b; k++)
                    for (int f = 0; f < KeptFrames; f++)
                        for (int key = 0; key < Keys; key++)
                            dst[((w0 + k) * KeptFrames + f) * Keys + key] = t[k, f + OverlapFrames / 2, key];
            }
            progress?.Invoke(Math.Min(0.95, (w0 + b) / (double)windows));
        }
        // only the frames the audio reaches, as Basic Pitch trims them
        int n = Math.Min(windows * KeptFrames, (int)(audio.Length / (double)HopSamples * KeptFrames));
        return (frames, onsets, n);
    }

    static void ConstrainPitch(float[] frames, float[] onsets, int n, int minPitch, int maxPitch)
    {
        int lo = Math.Clamp(minPitch - LowestPitch, 0, Keys), hi = Math.Clamp(maxPitch - LowestPitch, -1, Keys - 1);
        if (lo == 0 && hi == Keys - 1) return;
        for (int f = 0; f < n; f++)
            for (int key = 0; key < Keys; key++)
                if (key < lo || key > hi) frames[f * Keys + key] = onsets[f * Keys + key] = 0;
    }

    readonly record struct Event(int Start, int End, int Pitch, double Amplitude);

    /// <summary>Basic Pitch's <c>output_to_notes_polyphonic</c>: notes from the onset peaks, followed while they sound,
    /// then the "melodia trick" (notes the onsets missed, grown from the loudest frames left).</summary>
    static List<Event> Decode(float[] frames, float[] onsetsIn, int n, double onsetThresh, double frameThresh, int minLen)
    {
        var onsets = InferOnsets(onsetsIn, frames, n);
        var remaining = new float[n * Keys];
        Array.Copy(frames, remaining, remaining.Length);
        var events = new List<Event>();

        void Clear(int f, int key)
        {
            remaining[f * Keys + key] = 0;
            if (key < Keys - 1) remaining[f * Keys + key + 1] = 0;
            if (key > 0) remaining[f * Keys + key - 1] = 0;
        }
        double Mean(int from, int to, int key)
        {
            double sum = 0;
            for (int f = from; f < to; f++) sum += frames[f * Keys + key];
            return to > from ? sum / (to - from) : 0;
        }

        bool Peak(int f, int key)
        {
            float o = onsets[f * Keys + key];
            return f >= 1 && f < n - 1 && o >= onsetThresh && o > onsets[(f - 1) * Keys + key] && o > onsets[(f + 1) * Keys + key];
        }
        // A second, weaker peak shortly after another on the same key while the note never stopped is the same attack
        // (the model often marks a strummed low string twice): taken as a note of its own, it ate the real start and
        // that one was dropped as too short, so the note came out ~60 ms late. Not in Basic Pitch.
        bool Echo(int f, int key)
        {
            for (int g = f - 1; g >= Math.Max(1, f - EchoFrames); g--)
            {
                if (frames[(g + 1) * Keys + key] < frameThresh) return false;
                if (Peak(g, key)) return onsets[g * Keys + key] >= onsets[f * Keys + key];
            }
            return false;
        }

        // onset peaks (higher than the frame before and after), latest first
        for (int start = n - 2; start >= 1; start--)
            for (int key = Keys - 1; key >= 0; key--)
            {
                if (!Peak(start, key) || Echo(start, key)) continue;
                int i = start + 1, k = 0;
                while (i < n - 1 && k < EnergyTolerance)
                {
                    k = remaining[i * Keys + key] < frameThresh ? k + 1 : 0;
                    i++;
                }
                i -= k;
                if (i - start <= minLen) continue;
                for (int f = start; f < i; f++) Clear(f, key);
                events.Add(new Event(start, i, key + LowestPitch, Mean(start, i, key)));
            }

        // the loudest frame left grows into a note both ways; every frame above the threshold, loudest first
        // (cells only ever drop to 0, so this order is what Basic Pitch's repeated argmax visits)
        var peaks = new List<int>();
        for (int x = 0; x < remaining.Length; x++) if (remaining[x] > frameThresh) peaks.Add(x);
        peaks = [.. peaks.OrderByDescending(x => remaining[x]).ThenBy(x => x)];
        foreach (int x in peaks)
        {
            if (remaining[x] <= frameThresh) continue;
            int mid = x / Keys, key = x % Keys;
            remaining[x] = 0;
            int i = mid + 1, k = 0;
            while (i < n - 1 && k < EnergyTolerance)
            {
                k = remaining[i * Keys + key] < frameThresh ? k + 1 : 0;
                Clear(i, key);
                i++;
            }
            int end = i - 1 - k;
            i = mid - 1;
            k = 0;
            while (i > 0 && k < EnergyTolerance)
            {
                k = remaining[i * Keys + key] < frameThresh ? k + 1 : 0;
                Clear(i, key);
                i--;
            }
            int begin = i + 1 + k;
            if (end - begin <= minLen) continue;
            events.Add(new Event(begin, end, key + LowestPitch, Mean(begin, end, key)));
        }
        return events;
    }

    /// <summary>Basic Pitch's <c>get_infered_onsets</c>: a note that gets suddenly louder (a re-picked string the onset
    /// output missed) starts there too.</summary>
    static float[] InferOnsets(float[] onsets, float[] frames, int n)
    {
        var diff = new float[n * Keys];
        float maxDiff = 0, maxOnset = 0;
        for (int f = 0; f < n; f++)
            for (int key = 0; key < Keys; key++)
            {
                int x = f * Keys + key;
                maxOnset = Math.Max(maxOnset, onsets[x]);
                if (f < 2) continue;
                float d = Math.Min(frames[x] - frames[x - Keys], frames[x] - frames[x - 2 * Keys]);
                if (d <= 0) continue;
                diff[x] = d;
                maxDiff = Math.Max(maxDiff, d);
            }
        var result = new float[n * Keys];
        float scale = maxDiff > 0 ? maxOnset / maxDiff : 0;
        for (int x = 0; x < result.Length; x++) result[x] = Math.Max(onsets[x], diff[x] * scale);
        return result;
    }

    /// <summary>
    /// Drops the model's ghosts under a ringing string (measured on plucked strings: they were almost all its extra
    /// notes): a note an octave, an octave and a fifth or two octaves below a clearly stronger one, heard only while
    /// that one sounds. A real bass note under a chord starts with it or outlasts it, so it stays. Notes an octave
    /// above are left alone: in a chord they can't be told from a played octave (melody mode drops them).
    /// </summary>
    static List<Event> WithoutGhosts(List<Event> events) =>
        [.. events.Where(g => !events.Any(m => m.Pitch - g.Pitch is 12 or 19 or 24 && g.Amplitude < GhostRatio * m.Amplitude
            && m.Start <= g.Start + GhostSlackFrames && m.End >= g.End - GhostSlackFrames))];

    /// <summary>How far (≈ 70 ms) a weaker onset peak still counts as the same attack (<c>Echo</c> in <see cref="Decode"/>): the
    /// doubled ones measured 5–6 frames apart; a tremolo at 16ths of 200 BPM (75 ms) is still notes of its own.</summary>
    const int EchoFrames = 6;
    /// <summary>A ghost is under this share of the note that makes it (the ones measured were about half).</summary>
    const double GhostRatio = 0.7;
    /// <summary>How far (≈ 23 ms) a ghost may stick out of the note that makes it.</summary>
    const int GhostSlackFrames = 2;

    /// <summary>
    /// One note at a time (<see cref="Mode.Melody"/>): the strongest notes (clear and long) go first and every weaker
    /// one keeps only what doesn't overlap them, when that is still a note.
    /// </summary>
    static List<Event> OneAtATime(List<Event> events, int minLen)
    {
        var taken = new List<Event>();
        foreach (var e in events.OrderByDescending(e => e.Amplitude * (e.End - e.Start)))
        {
            var pieces = new List<(int S, int E)> { (e.Start, e.End) };
            foreach (var t in taken)
            {
                var next = new List<(int, int)>();
                foreach (var (s, en) in pieces)
                {
                    if (t.End <= s || t.Start >= en) { next.Add((s, en)); continue; }
                    if (t.Start > s) next.Add((s, t.Start));
                    if (t.End < en) next.Add((t.End, en));
                }
                pieces = next;
            }
            foreach (var (s, en) in pieces)
                if (en - s > minLen) taken.Add(e with { Start = s, End = en });
        }
        return taken;
    }
}
