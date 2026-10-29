namespace WildTrack.Audio.Dsp;

/// <summary>
/// A note as the engine plays it: from frame <see cref="Start"/>, held <see cref="Length"/> frames, at <see cref="Hz"/>,
/// with its velocity's gain (the test tone), and as MIDI (<see cref="Pitch"/>, <see cref="Velocity"/>) for an instrument plugin.
/// </summary>
public readonly record struct NoteEvent(long Start, long Length, double Hz, float Gain, int Pitch = 60, int Velocity = 100);

/// <summary>
/// A MIDI track's notes in frames, sorted by start (<c>ChannelDsp.Notes</c> builds it from the project). Never changed
/// once built: the UI swaps a new one in whole and the audio thread reads the reference once per block.
/// </summary>
public sealed class NoteSequence
{
    public readonly NoteEvent[] Notes;
    /// <summary>The longest a note sounds, release included: how far back a block looks for notes still ringing.</summary>
    public readonly long MaxSpan;
    /// <summary>The frame after the last note's release.</summary>
    public readonly long End;

    public NoteSequence(NoteEvent[] notes, int sampleRate)
    {
        Array.Sort(notes, (a, b) => a.Start.CompareTo(b.Start));
        Notes = notes;
        long release = NoteSynth.ReleaseFrames(sampleRate);
        foreach (var n in notes)
        {
            MaxSpan = Math.Max(MaxSpan, n.Length + release);
            End = Math.Max(End, n.Start + n.Length + release);
        }
    }
}

/// <summary>
/// The test tone a MIDI track plays until it has an instrument (stage 1 of the MIDI editor; the sampler and VST3
/// come next): a soft electric-piano-like tone, three harmonics with a short attack, a decay to a held level and a
/// release after the note ends. Every sample is a function of the time since its note began, with no state, so the
/// live engine (<c>LiveTrack</c>) and the export (<see cref="MixRenderer"/>) render it in any block size and from any
/// position (a seek, a loop, a crossfade) and get the same floats.
/// </summary>
public static class NoteSynth
{
    /// <summary>Attack and release, short enough to feel immediate, long enough not to click.</summary>
    public const double AttackSec = 0.004, ReleaseSec = 0.12;
    /// <summary>How fast the tone falls from the attack to <see cref="Sustain"/> (seconds to fall by e).</summary>
    const double DecaySec = 0.45;
    /// <summary>The level a held note settles at, relative to its attack.</summary>
    const double Sustain = 0.35;
    /// <summary>The level of a note at full velocity (−12 dB): a chord of four stays under 0 dBFS.</summary>
    const double Level = 0.25;
    /// <summary>The second and third harmonics, relative to the fundamental: a little body without buzz.</summary>
    const double H2 = 0.3, H3 = 0.12;
    const double Norm = 1 / (1 + H2 + H3);

    public static long ReleaseFrames(int sampleRate) => (long)Math.Round(ReleaseSec * sampleRate);

    /// <summary>The frequency of a MIDI pitch (69 = A4 = 440 Hz); fractions are cents (a track's Transponer).</summary>
    public static double Hz(double pitch) => 440 * Math.Pow(2, (pitch - 69) / 12.0);

    /// <summary>The gain of a velocity (1..127): squared, as most instruments respond.</summary>
    public static float Gain(int velocity) => (float)(Level * Math.Pow(Math.Clamp(velocity, 1, 127) / 127.0, 2));

    /// <summary>
    /// Adds the notes sounding in frames <c>[pos .. pos + n)</c> to <paramref name="l"/> and <paramref name="r"/>
    /// (times <paramref name="weight"/> when given: a seek's crossfade). Zero allocations; safe on the audio thread.
    /// </summary>
    public static void Render(NoteSequence seq, long pos, int n, int sampleRate, Span<float> l, Span<float> r, ReadOnlySpan<float> weight = default)
    {
        var notes = seq.Notes;
        if (notes.Length == 0 || pos >= seq.End) return;
        long release = ReleaseFrames(sampleRate);
        // the first note that may still ring: notes start in order, none rings longer than MaxSpan
        int lo = 0, hi = notes.Length;
        long from = pos - seq.MaxSpan;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (notes[mid].Start < from) lo = mid + 1; else hi = mid;
        }
        double invSr = 1.0 / sampleRate;
        for (int k = lo; k < notes.Length; k++)
        {
            var note = notes[k];
            if (note.Start >= pos + n) break;
            long stop = note.Start + note.Length;
            if (stop + release <= pos) continue;
            int i0 = (int)Math.Max(0, note.Start - pos);
            int i1 = (int)Math.Min(n, stop + release - pos);
            double w = 2 * Math.PI * note.Hz * invSr;
            // the level the note had when it was let go, so the release starts from there
            double held = Envelope(note.Length * invSr);
            for (int i = i0; i < i1; i++)
            {
                long t = pos + i - note.Start;
                double env = t < note.Length ? Envelope(t * invSr) : held * (1 - (t - note.Length) / (double)release);
                double ph = w * t;
                double s = (Math.Sin(ph) + H2 * Math.Sin(2 * ph) + H3 * Math.Sin(3 * ph)) * Norm * env * note.Gain;
                float v = (float)s;
                if (!weight.IsEmpty) v *= weight[i];
                l[i] += v;
                r[i] += v;
            }
        }
    }

    /// <summary>
    /// One note played live (a key held, then let go: <c>LiveNotes</c>), frames <c>[t .. t + n)</c> since it began, added
    /// to <paramref name="l"/> / <paramref name="r"/>. <paramref name="heldFrames"/> is how long the key was held, or -1
    /// while it still is. The same samples <see cref="Render"/> gives a note of that length. False once its release ended.
    /// </summary>
    public static bool RenderLive(double hz, float gain, long t, long heldFrames, int n, int sampleRate, Span<float> l, Span<float> r)
    {
        long release = ReleaseFrames(sampleRate);
        double invSr = 1.0 / sampleRate;
        double w = 2 * Math.PI * hz * invSr;
        double held = heldFrames < 0 ? 0 : Envelope(heldFrames * invSr);
        for (int i = 0; i < n; i++)
        {
            long tt = t + i;
            double env;
            if (heldFrames < 0 || tt < heldFrames) env = Envelope(tt * invSr);
            else if (tt < heldFrames + release) env = held * (1 - (tt - heldFrames) / (double)release);
            else return false;
            double ph = w * tt;
            float v = (float)((Math.Sin(ph) + H2 * Math.Sin(2 * ph) + H3 * Math.Sin(3 * ph)) * Norm * env * gain);
            l[i] += v;
            r[i] += v;
        }
        return heldFrames < 0 || t + n < heldFrames + release;
    }

    /// <summary>The level while the note is held: a linear attack, then an exponential fall to <see cref="Sustain"/>.</summary>
    static double Envelope(double t) => t < AttackSec ? t / AttackSec : Sustain + (1 - Sustain) * Math.Exp(-(t - AttackSec) / DecaySec);

    /// <summary>One note on its own (the piano roll's keys and new notes), mono: what <see cref="Render"/> plays for it.</summary>
    public static float[] Audition(int pitch, int velocity, int sampleRate, double heldSec = 0.35)
    {
        long len = (long)Math.Round(heldSec * sampleRate);
        var seq = new NoteSequence([new NoteEvent(0, len, Hz(pitch), Gain(velocity))], sampleRate);
        var l = new float[seq.End];
        var r = new float[seq.End];
        Render(seq, 0, l.Length, sampleRate, l, r);
        return l;
    }
}
